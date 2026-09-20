using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;
public sealed class ModelLibrary(string directory="/var/lib/xur")
{
    readonly object gate=new();ModelScanStatus snapshot=new(null,false,[],[],[]);
    public ModelScanStatus Status(){lock(gate)return snapshot;}
    public void Start(bool allStorage)
    {
        lock(gate){if(snapshot.Scanning)return;snapshot=snapshot with{Scanning=true};}
        _=Task.Run(async()=>{
            var models=new Dictionary<string,ModelFolder>();var roots=new List<string>();var warnings=new List<string>();
            try
            {
                if(!allStorage)foreach(var entry in Status().Models)if(Directory.Exists(entry.Path)||File.Exists(entry.Path))models[entry.Path]=entry;
                var caches=new[]{directory+"/models",directory+"/model-sets"};
                foreach(var root in caches)if(Directory.Exists(root))roots.Add(root);
                const string volumes="/var/lib/containers/storage/volumes";
                if(Directory.Exists(volumes))roots.AddRange(Directory.GetDirectories(volumes,"xur-cache-*").Select(p=>p+"/_data"));
                if(allStorage)
                {
                    var mounts=await Processes.Run("findmnt",["--json","--real","--output","TARGET,FSTYPE,SOURCE"],10);
                    if(mounts.ExitCode==0)
                    {
                        using var doc=JsonDocument.Parse(mounts.Output);
                        void Add(JsonElement item){var type=item.GetProperty("fstype").GetString();var target=item.GetProperty("target").GetString();if(target!=null&&type is "ext4" or "ext3" or "ext2" or "xfs" or "btrfs" or "vfat" or "exfat" or "ntfs3" or "fuseblk" or "nfs" or "nfs4" or "cifs")roots.Add(target);if(item.TryGetProperty("children",out var children))foreach(var c in children.EnumerateArray())Add(c);}
                        foreach(var m in doc.RootElement.GetProperty("filesystems").EnumerateArray())Add(m);
                    }
                }
                var seen=new HashSet<string>();var count=0;var deadline=DateTimeOffset.UtcNow.AddMinutes(5);
                foreach(var root in roots.Distinct())Scan(root,root.StartsWith(directory)?"Xur model storage":root.StartsWith(volumes)?"Persistent engine cache":"Attached storage",models,seen,warnings,ref count,deadline);
                if(allStorage)await ScanUnmounted(models,seen,roots,warnings,count,deadline);
                try { var catalog=new RecipeCatalog(Environment.GetEnvironmentVariable("XUR_CATALOG")??"/usr/share/xur/catalog",directory+"/catalog-selected");
                foreach(var recipe in catalog.Recipes)
                    foreach(var asset in (recipe.Model==null?Array.Empty<ModelAsset>():[recipe.Model]).Concat(recipe.Files?.Select(f=>f.Asset)??[]))
                        foreach(var key in models.Keys.Where(k=>Path.GetFileName(k)==asset.Sha256+".gguf").ToArray())models[key]=models[key] with{Name=recipe.Name};
                } catch(IOException) { warnings.Add("Catalog names could not be loaded; model paths are still listed."); }
                var value=new ModelScanStatus(DateTimeOffset.UtcNow,false,models.Values.OrderBy(m=>m.Name).ToArray(),roots.Distinct().ToArray(),warnings.Distinct().ToArray());
                Directory.CreateDirectory(directory);using(var f=new FileStream(directory+"/model-library.json.tmp",FileMode.Create,FileAccess.Write)){JsonSerializer.Serialize(f,value);f.Flush(true);}File.Move(directory+"/model-library.json.tmp",directory+"/model-library.json",true);
                lock(gate)snapshot=value;
            }catch(Exception e){lock(gate)snapshot=snapshot with{Scanning=false,Warnings=["Model scan could not finish: "+e.Message]};}
        });
    }
    public void Load()
    {try{if(File.Exists(directory+"/model-library.json"))snapshot=JsonSerializer.Deserialize<ModelScanStatus>(File.ReadAllText(directory+"/model-library.json"))! with{Scanning=false};}catch{}Start(false);}
    static long Size(FileInfo f)=>f.LinkTarget!=null&&f.ResolveLinkTarget(true) is FileInfo target?target.Length:f.Length;
    static bool IsGguf(FileInfo f){try{if(Size(f)<4)return false;using var stream=f.OpenRead();Span<byte> magic=stackalloc byte[4];return stream.Read(magic)==4&&magic.SequenceEqual("GGUF"u8);}catch{return false;}}
    public static void Scan(string root,string source,Dictionary<string,ModelFolder> result,HashSet<string> seen,List<string> warnings,ref int count,DateTimeOffset deadline)
    {
        var pending=new Stack<string>();pending.Push(root);
        while(pending.TryPop(out var path))
        {
            if(++count>250000||DateTimeOffset.UtcNow>deadline){warnings.Add("Scan limit reached; some folders were not searched.");return;}
            if(!seen.Add(path))continue;
            if(path is "/proc" or "/sys" or "/dev" or "/run" or "/usr" or "/boot" || path.StartsWith("/var/lib/containers/storage/overlay"))continue;
            try
            {
                var directoryInfo=new DirectoryInfo(path);if(directoryInfo.LinkTarget!=null)continue;
                var files=directoryInfo.GetFiles();var weights=files.Where(f=>f.Name.EndsWith(".gguf",StringComparison.OrdinalIgnoreCase)&&IsGguf(f)||f.Name.EndsWith(".safetensors",StringComparison.OrdinalIgnoreCase)||f.Name=="pytorch_model.bin"||f.Name.StartsWith("pytorch_model-")&&f.Name.EndsWith(".bin")).ToArray();
                if(weights.Length>0)
                {
                    var name=directoryInfo.Name;var hf=path.Split('/').LastOrDefault(n=>n.StartsWith("models--"));if(hf!=null)name=hf[8..].Replace("--","/");
                    foreach(var gguf in weights.Where(w=>w.Extension.Equals(".gguf",StringComparison.OrdinalIgnoreCase)))
                        result[gguf.FullName]=new(gguf.Name,gguf.FullName,"GGUF",Size(gguf),1,source);
                    var tensors=weights.Where(w=>!w.Extension.Equals(".gguf",StringComparison.OrdinalIgnoreCase)).ToArray();
                    if(tensors.Length>0)result[path]=new(name,path,tensors.Any(w=>w.Extension==".safetensors")?"Safetensors":"PyTorch",tensors.Sum(Size),tensors.Length,source);
                }
                foreach(var child in directoryInfo.GetDirectories())if(child.LinkTarget==null)pending.Push(child.FullName);
            }catch(UnauthorizedAccessException){warnings.Add("Permission denied: "+path);}catch(IOException){warnings.Add("Unavailable folder: "+path);}
        }
    }
    static async Task ScanUnmounted(Dictionary<string,ModelFolder> result,HashSet<string> seen,List<string> roots,List<string> warnings,int count,DateTimeOffset deadline)
    {
        var r=await Processes.Run("lsblk",["--json","--paths","--output","PATH,TYPE,FSTYPE,MOUNTPOINTS,RO"],10);if(r.ExitCode!=0){warnings.Add("Could not enumerate unmounted storage.");return;}
        using var doc=JsonDocument.Parse(r.Output);var candidates=new List<JsonElement>();
        void Walk(JsonElement item){if(item.TryGetProperty("children",out var children)){foreach(var child in children.EnumerateArray())Walk(child);return;}if(item.GetProperty("type").GetString() is "disk" or "part" && item.GetProperty("mountpoints").EnumerateArray().All(m=>m.ValueKind==JsonValueKind.Null))candidates.Add(item);}
        foreach(var item in doc.RootElement.GetProperty("blockdevices").EnumerateArray())Walk(item);
        foreach(var item in candidates)
        {
            if(count>250000||DateTimeOffset.UtcNow>deadline){warnings.Add("Scan limit reached; some storage was not searched.");break;}
            var device=item.GetProperty("path").GetString()!;var fs=item.GetProperty("fstype").GetString();
            if(fs is not ("ext2" or "ext3" or "ext4" or "xfs" or "btrfs" or "vfat" or "exfat" or "ntfs" or "ntfs3"))continue;
            var mount="/run/xur/model-scan-"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(mount);var wasReadOnly=item.GetProperty("ro").GetBoolean();var changed=false;
            try
            {
                // Recheck mounts immediately before acquiring the read-only scan boundary.
                var mounted=await Processes.Run("findmnt",["--source",device,"--noheadings"],5);if(mounted.ExitCode!=1)continue;
                var ro=await Processes.Run("blockdev",["--setro",device],5);if(ro.ExitCode!=0){warnings.Add("Could not scan read-only: "+device);continue;}changed=true;
                var options="ro,nosuid,nodev,noexec"+(fs.StartsWith("ext")?",noload":fs=="xfs"?",norecovery":fs=="btrfs"?",nologreplay":"");
                var mountedResult=await Processes.Run("mount",["-t",fs=="ntfs"?"ntfs3":fs,"-o",options,device,mount],10);
                if(mountedResult.ExitCode!=0){warnings.Add("Could not mount for read-only search: "+device);continue;}
                var found=new Dictionary<string,ModelFolder>();Scan(mount,"Attached storage",found,seen,warnings,ref count,deadline);
                foreach(var model in found.Values){var path=device+":"+model.Path[mount.Length..];result[path]=model with{Path=path};}roots.Add(device+" (read-only)");
            }
            finally
            {
                var unmount=await Processes.Run("umount",[mount],10);
                if(Directory.Exists(mount)&&!Directory.EnumerateFileSystemEntries(mount).Any())Directory.Delete(mount);
                if(changed&&!wasReadOnly){var mounted=await Processes.Run("findmnt",["--mountpoint",mount,"--noheadings"],5);if(mounted.ExitCode==1)await Processes.Run("blockdev",["--setrw",device],5);else warnings.Add("Scan mount is still active; device remains read-only: "+device);}
            }
        }
    }
}
