using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

// A cached metadata scan. Never mount devices, traverse remote filesystems, or
// block the HTTP host while a large model or home directory is being measured.
public sealed class StorageUsage
{
    readonly object gate=new();
    StorageUsageSnapshot snapshot=new(null,false,[],[],[]);
    Task? worker;
    public StorageUsageSnapshot Status(bool refresh=false)
    {
        lock(gate)
        {
            if(worker is not {IsCompleted:false} && (refresh || snapshot.CapturedAt==null || DateTimeOffset.UtcNow-snapshot.CapturedAt>TimeSpan.FromMinutes(5)))
            {snapshot=snapshot with {Scanning=true,Error=null};worker=Task.Run(Collect);}
            return snapshot;
        }
    }
    static string S(JsonElement e,string key)=>e.TryGetProperty(key,out var v)&&v.ValueKind!=JsonValueKind.Null?v.ToString():"";
    static long N(JsonElement e,string key)=>long.TryParse(S(e,key),out var n)?n:0;
    public static StorageFilesystem[] Filesystems(string json)
    {
        using var doc=JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("filesystems").EnumerateArray().Where(e=>S(e,"source").StartsWith("/dev/") && N(e,"size")>0)
            .GroupBy(e=>S(e,"source").Split('[')[0]).Select(g=>{
                var first=g.First();var mounts=g.Select(e=>S(e,"target")).Distinct().OrderBy(p=>p=="/"?0:p=="/var"?1:p.Length).ToArray();
                return new StorageFilesystem(g.Key,S(first,"fstype"),mounts,N(first,"size"),N(first,"used"),N(first,"avail"));
            }).OrderByDescending(f=>f.Mounts.Contains("/var")).ThenBy(f=>f.Source).ToArray();
    }
    async Task Collect()
    {
        try
        {
            var result=await Processes.Run("findmnt",["--json","--df","--bytes","--output","SOURCE,TARGET,FSTYPE,SIZE,USED,AVAIL"],20);
            if(result.ExitCode!=0)throw new IOException();
            var filesystems=Filesystems(result.Output);
            var nodes=await Storage.Nodes();
            var disks=nodes.Where(n=>S(n,"type")=="disk" && !S(n,"path").StartsWith("/dev/zram")).Select(n=>new StorageDiskUsage(S(n,"path"),S(n,"model"),N(n,"size"),Storage.Flatten(n).Where(c=>c.GetProperty("mountpoints").EnumerateArray().Any(m=>m.ValueKind==JsonValueKind.String)).Select(c=>S(c,"path")).Distinct().ToArray(),Storage.Flatten(n).SelectMany(c=>c.GetProperty("mountpoints").EnumerateArray().Where(m=>m.ValueKind==JsonValueKind.String).Select(m=>m.GetString()!)).Distinct().ToArray())).ToArray();
            lock(gate)snapshot=snapshot with {Filesystems=filesystems,Disks=disks};
            var categories=new List<StorageCategory>();
            categories.Add(await Measure("Models",["/var/lib/xur/models","/var/lib/xur/model-sets"]));
            categories.Add(await Measure("Workstation streaming",["/var/lib/xur-streaming","/var/lib/xur-streaming-runtime"]));
            categories.Add(await Measure("Containers & engines",["/var/lib/containers/storage"]));
            categories.Add(await Measure("User files",["/var/home"]));
            categories.Add(await Measure("Xur application versions",["/var/lib/xur/app"]));
            categories.Add(await Measure("System logs",["/var/log"]));
            var other=Directory.Exists("/var/lib/xur")?Directory.GetFileSystemEntries("/var/lib/xur").Where(p=>Path.GetFileName(p) is not ("models" or "model-sets" or "app")).ToArray():[];
            categories.Add(await Measure("Xur settings & state",other));
            var volumes=await ContainerLibrary.Volumes();
            lock(gate)snapshot=new(DateTimeOffset.UtcNow,false,filesystems,disks,categories.ToArray(),Volumes:volumes);
        }
        catch {lock(gate)snapshot=snapshot with {CapturedAt=DateTimeOffset.UtcNow,Scanning=false,Error="Storage usage could not be read. Refresh to retry."};}
    }
    static async Task<StorageCategory> Measure(string name,string[] paths)
    {
        var existing=paths.Where(p=>Directory.Exists(p)||File.Exists(p)).ToArray();if(existing.Length==0)return new(name,paths,0);
        try
        {
            // One invocation deduplicates hardlinked model files across both paths.
            var r=await Processes.Run("du",["--summarize","--one-file-system","--block-size=1","--",..existing],120);
            if(r.ExitCode!=0)return new(name,paths,null,"Usage unavailable");
            long total=0;foreach(var line in r.Output.Split('\n',StringSplitOptions.RemoveEmptyEntries)){var field=line.Split('\t',2)[0];if(!long.TryParse(field,out var bytes))return new(name,paths,null,"Usage unavailable");total=checked(total+bytes);}
            return new(name,paths,total);
        }catch{return new(name,paths,null,"Usage unavailable");}
    }
}
