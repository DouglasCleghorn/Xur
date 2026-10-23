using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xur.Domain;
namespace Xur.Agent;

// Configuration stays on the system disk. Each move writes a fresh generation;
// the only commit point is the durable, atomic replacement of storage-locations.json.
public sealed class DataStorage(string state,Func<string,string[],int,Task<ProcessResult>>? execute=null,string mountRoot="/var/mnt/xur",string volumes="/var/lib/containers/storage/volumes",Func<string,long>? available=null)
{
    readonly Func<string,string[],int,Task<ProcessResult>> run=execute??((exe,args,seconds)=>Processes.Run(exe,args,seconds));
    readonly SemaphoreSlim mounting=new(1,1);
    readonly object sync=new();bool busy;
    static readonly string[] groups=["models","unsloth"];
    string ConfigPath=>Path.Combine(state,"storage-locations.json");
    string JobPath=>Path.Combine(state,"storage-move.json");
    public bool Busy {get{lock(sync)return busy;}}
    public DataStorageConfig Config()=>File.Exists(ConfigPath)?JsonSerializer.Deserialize<DataStorageConfig>(File.ReadAllText(ConfigPath))??throw new InvalidOperationException("Storage configuration is invalid."):new(Guid.NewGuid().ToString("N"),null,[],[]);
    static string[] Parts(string group)=>group switch{"models"=>["models","model-sets"],"unsloth"=>["workspace","studio"],_=>throw new InvalidOperationException("Select Models or Unsloth storage.")};
    public string PathFor(string group,string part,DataStorageConfig? config=null)
    {
        if(!Parts(group).Contains(part))throw new InvalidOperationException("Unknown storage folder.");
        if((config??Config()).Locations.TryGetValue(group,out var location))return Path.Combine(location.Path,part);
        return group=="models"?Path.Combine(state,part):Path.Combine(volumes,UnslothWorkload.Mounts[part=="workspace"?0:1].Volume,"_data");
    }
    public bool Selected(string group)=>Config().Locations.ContainsKey(group);
    static bool Uuid(string value)=>Regex.IsMatch(value,@"\A[a-fA-F0-9][a-fA-F0-9-]{3,63}\z");
    static bool Candidate(StorageMount mount)=>mount.Type is "ext4" or "xfs" && mount.Bytes>20L*1024*1024*1024 && !mount.ReadOnly && mount.Uuid is {} uuid && Uuid(uuid);
    async Task Checked(string exe,string[] args,int timeout,string failure)
    {var result=await run(exe,args,timeout);if(result.ExitCode!=0)throw new InvalidOperationException(failure+" "+Redaction.Logs(result.Output));}
    async Task Save<T>(string path,T value)
    {
        Directory.CreateDirectory(state);
        using(var stream=new FileStream(path+".tmp",FileMode.Create,FileAccess.Write)){JsonSerializer.Serialize(stream,value);stream.Flush(true);}
        File.Move(path+".tmp",path,true);
        await Checked("sync",["-f",state],30,"Could not make storage configuration durable.");
    }
    async Task<string> Mount(string uuid)
    {
        if(!Uuid(uuid))throw new InvalidOperationException("Invalid storage filesystem UUID.");
        var path=Path.Combine(mountRoot,uuid);
        await mounting.WaitAsync();try
        {
            var mounted=await StorageMounts.Read(run);
            var current=mounted.SingleOrDefault(m=>m.Path==path);
            if(current==null)
            {
                // No path from the caller is passed to mount. Never create the
                // destination data directory until the exact mount is verified.
                Directory.CreateDirectory(path);NoLinks(path);
                if(Directory.EnumerateFileSystemEntries(path).Any())throw new InvalidOperationException("The storage mount point contains unexpected files.");
                await Checked("mount",["--types","ext4,xfs","--options","nodev,nosuid","--uuid",uuid,path],30,"The selected data drive is unavailable. Reconnect it; system storage will not be used as a fallback.");
                current=(await StorageMounts.Read(run)).SingleOrDefault(m=>m.Path==path);
            }
            if(current==null || current.Uuid!=uuid || !Candidate(current))throw new InvalidOperationException("The selected storage mount is missing, read only, or has the wrong filesystem identity.");
            return path;
        }finally{mounting.Release();}
    }
    public async Task Require(string group)
    {
        if(Config().Locations.TryGetValue(group,out var location))
        {
            if(location.Uuid is {} uuid)
            {
                var root=await Mount(uuid);
                if(!location.Path.StartsWith(root+"/xur/",StringComparison.Ordinal))throw new InvalidOperationException("Storage configuration has an invalid destination.");
            }
            else if(!location.Path.StartsWith(Path.Combine(state,"data")+"/",StringComparison.Ordinal))throw new InvalidOperationException("Storage configuration has an invalid system destination.");
            NoLinks(location.Path);
            foreach(var part in Parts(group)){NoLinks(PathFor(group,part));if(!Directory.Exists(PathFor(group,part)))throw new InvalidOperationException("Selected storage data is missing. Restore the drive or inspect the retained migration copy.");}
        }
    }
    static void NoLinks(string path)
    {
        for(var d=new DirectoryInfo(path);d!=null;d=d.Parent)
            if(d.LinkTarget!=null)throw new InvalidOperationException("Managed storage directories cannot be symbolic links.");
    }
    public async Task<DataStorageStatus> Status()
    {
        var config=Config();var mounts=await StorageMounts.Read(run);var rows=new List<DataStorageGroup>();
        foreach(var group in groups)
        {
            var paths=Parts(group).Select(p=>PathFor(group,p,config)).ToArray();string? error=null;long? free=null;
            var mount=mounts.Where(m=>paths[0]==m.Path||paths[0].StartsWith(m.Path.TrimEnd('/')+"/",StringComparison.Ordinal)).OrderByDescending(m=>m.Path.Length).FirstOrDefault();
            if(config.Locations.TryGetValue(group,out var location) && location.Uuid!=null && (mount?.Uuid!=location.Uuid || mount.Path!=Path.Combine(mountRoot,location.Uuid)))error="Selected drive is not mounted. Workload startup will try its UUID and fail if it is unavailable.";
            else if(mount?.ReadOnly==true)error="Selected storage is read only.";
            else if(config.Locations.ContainsKey(group)&&paths.Any(p=>!Directory.Exists(p)))error="Selected storage folders are missing. Inspect the retained migration copy.";
            else if(mount!=null)free=mount.Available;
            rows.Add(new(group,group=="models"?"Xur model library":"Unsloth workspace and Studio",config.Overrides.GetValueOrDefault(group,"inherit"),paths,free,error));
        }
        var job=File.Exists(JobPath)?JsonSerializer.Deserialize<DataStorageJob>(File.ReadAllText(JobPath)):null;
        if(!Busy && job is {Stage:"Copying" or "Verifying"})job=job with{Stage="Interrupted",Message="The move was interrupted. Verified locations remain selected; partial copies and originals were retained. Retry the move."};
        return new(config.DefaultUuid??"system",mounts.Where(Candidate).DistinctBy(m=>m.Uuid).ToArray(),rows.ToArray(),Busy,job);
    }
    public async Task Move(DataStorageRequest request)
    {
        lock(sync){if(busy)throw new InvalidOperationException("A storage move is already running.");busy=true;}
        var id=Guid.NewGuid().ToString("N");var retained=new List<string>();
        async Task Report(string stage,string message)
        {
            var job=new DataStorageJob(id,stage,message,DateTimeOffset.UtcNow,retained.ToArray());
            Directory.CreateDirectory(Path.Combine(state,"storage-moves"));
            await Save(Path.Combine(state,"storage-moves",id+".json"),job);await Save(JobPath,job);
        }
        try
        {
            if(request.Group!="default"&&!groups.Contains(request.Group))throw new InvalidOperationException("Unknown storage selection.");
            if(request.Destination is not ("system" or "inherit")&&!Uuid(request.Destination))throw new InvalidOperationException("Select a storage drive.");
            if(request.Group=="default"&&request.Destination=="inherit")throw new InvalidOperationException("The default cannot inherit itself.");
            var old=Config();var next=old with{Overrides=new(old.Overrides),Locations=new(old.Locations)};
            if(request.Group=="default")next=next with{DefaultUuid=request.Destination=="system"?null:request.Destination};
            else if(request.Destination=="inherit")next.Overrides.Remove(request.Group);
            else next.Overrides[request.Group]=request.Destination;
            var copies=new List<(string Group,string? Uuid,string Root,string Destination,string[] Sources,long Bytes)>();
            foreach(var group in groups.Where(g=>request.Group==g||request.Group=="default"&&!next.Overrides.ContainsKey(g)))
            {
                var selection=next.Overrides.GetValueOrDefault(group,next.DefaultUuid??"system");var uuid=selection=="system"?null:selection;
                var currentUuid=old.Locations.GetValueOrDefault(group)?.Uuid;
                if(uuid==currentUuid)continue;
                await Require(group);
                if(group=="unsloth"&&!old.Locations.ContainsKey(group))
                {
                    await new UnslothRuntime(run).EnsureStorage();
                    foreach(var mount in UnslothWorkload.Mounts)
                        await ContainerLibrary.VerifyVolume(mount.Volume,run,PathFor(group,mount.Destination=="/workspace"?"workspace":"studio",old));
                }
                var root=uuid==null?Path.Combine(state,"data"):Path.Combine(await Mount(uuid),"xur",old.Installation);
                NoLinks(root);
                var destination=Path.Combine(root,id,group);
                if(Directory.Exists(destination))throw new InvalidOperationException("Migration destination already exists.");
                var sources=Parts(group).Select(p=>PathFor(group,p,old)).ToArray();
                foreach(var source in sources){Directory.CreateDirectory(source);NoLinks(source);}
                long bytes=0;
                foreach(var source in sources)
                {
                    var size=await run("du",["--summarize","--bytes","--",source],300);
                    if(size.ExitCode!=0||!long.TryParse(size.Output.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),out var measured))throw new InvalidOperationException("Could not measure existing storage.");
                    bytes=checked(bytes+measured);
                    var special=await run("find",[source,"-xdev","!","-type","f","!","-type","d","!","-type","l","-print","-quit"],300);
                    if(special.ExitCode!=0||!string.IsNullOrWhiteSpace(special.Output))throw new InvalidOperationException("Storage contains a special file. Inspect it before migrating: "+source);
                }
                Directory.CreateDirectory(root);
                copies.Add((group,uuid,root,destination,sources,bytes));
            }
            // Reserve the whole request's apparent size on each destination
            // filesystem before writing even the first group's partial copy.
            foreach(var batch in copies.GroupBy(c=>c.Root))
                CheckAvailable(available?.Invoke(batch.Key)??new DriveInfo(batch.Key).AvailableFreeSpace,batch.Sum(c=>c.Bytes));
            foreach(var copy in copies)
            {
                var (group,uuid,root,destination,sources,bytes)=copy;
                retained.AddRange(sources);
                await Report("Copying","Copying "+group+". Originals remain in place.");
                Directory.CreateDirectory(destination);
                // One invocation preserves hard links between model blobs and snapshots.
                if(group=="models")await Checked("cp",["--archive","--reflink=auto","--",..sources,destination],86400,"Storage copy failed; original data remains selected.");
                else for(int i=0;i<sources.Length;i++)
                    await Checked("cp",["--archive","--reflink=auto","--",sources[i],Path.Combine(destination,Parts(group)[i])],86400,"Storage copy failed; original data remains selected.");
                await Report("Verifying","Verifying every file in "+group+" before switching storage.");
                for(int i=0;i<sources.Length;i++)await Verify(sources[i],Path.Combine(destination,Parts(group)[i]));
                await Checked("sync",["-f",destination],300,"Could not flush the copied data.");
                next.Locations[group]=new(uuid,destination);
            }
            await Save(ConfigPath,next);
            await Report("Complete","Storage changed. Originals are retained at the paths below; no files were deleted.");
        }
        catch(Exception e){await Report("Failed",Redaction.Logs(e.Message));throw;}
        finally{lock(sync)busy=false;}
    }
    public static void CheckCapacity(string path,long bytes)
    {
        CheckAvailable(new DriveInfo(path).AvailableFreeSpace,bytes);
    }
    public static void CheckAvailable(long free,long bytes)
    {
        var required=checked(bytes+Math.Max(10L*1024*1024*1024,bytes/10));
        if(free<required)throw new InvalidOperationException($"Insufficient storage: need {required/1e9:0.0} GB including working space, but only {free/1e9:0.0} GB is free.");
    }
    public static async Task Verify(string source,string target)
    {
        var left=new DirectoryInfo(source);var right=new DirectoryInfo(target);
        if(left.LinkTarget!=null||right.LinkTarget!=null)throw new InvalidOperationException("Migration roots must be directories.");
        if(File.GetUnixFileMode(source)!=File.GetUnixFileMode(target))throw new InvalidOperationException("Copied directory permissions differ.");
        var entries=left.GetFileSystemInfos().OrderBy(f=>f.Name,StringComparer.Ordinal).ToArray();
        if(!entries.Select(f=>f.Name).SequenceEqual(right.GetFileSystemInfos().Select(f=>f.Name).Order(StringComparer.Ordinal)))throw new InvalidOperationException("Copied directory entries differ.");
        foreach(var entry in entries)
        {
            var path=Path.Combine(target,entry.Name);var other=new FileInfo(path);
            if(entry.LinkTarget!=null || other.LinkTarget!=null)
            {if(entry.LinkTarget!=other.LinkTarget)throw new InvalidOperationException("Copied symbolic link differs.");continue;}
            if(File.GetUnixFileMode(entry.FullName)!=File.GetUnixFileMode(path))throw new InvalidOperationException("Copied file permissions differ.");
            if(entry is DirectoryInfo){await Verify(entry.FullName,path);continue;}
            if(other.Length!=((FileInfo)entry).Length)throw new InvalidOperationException("Copied file size differs.");
            await using var a=File.OpenRead(entry.FullName);await using var b=File.OpenRead(path);
            var first=await SHA256.HashDataAsync(a);var second=await SHA256.HashDataAsync(b);
            if(!first.SequenceEqual(second))throw new InvalidOperationException("Copied file checksum differs.");
        }
    }
}
