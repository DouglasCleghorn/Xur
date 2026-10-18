using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

// A cached metadata scan. Never mount devices, traverse remote filesystems, or
// block the HTTP host while a large model or home directory is being measured.
public sealed class StorageUsage(VictoriaMetrics? metrics=null,Func<Task<(StorageFilesystem[] Filesystems,StorageDiskUsage[] Disks)>>? diskObserver=null)
{
    readonly object gate=new();
    readonly SemaphoreSlim diskScan=new(1);
    readonly TelemetryBuffer buffer=new(metrics??VictoriaMetrics.Local(),"storage");
    DateTimeOffset lastDiskScan;
    static readonly TimeSpan Interval=TimeSpan.FromMinutes(5);
    StorageUsageSnapshot snapshot=new(null,false,[],[],[]);
    Task? worker;
    public StorageUsageSnapshot Current {get{lock(gate)return snapshot;}}
    public StorageUsageSnapshot Status(bool refresh=false)
    {
        lock(gate)
        {
            if(worker is not {IsCompleted:false} && (refresh || snapshot.CapturedAt==null || DateTimeOffset.UtcNow-snapshot.CapturedAt>TimeSpan.FromMinutes(5)))
            {snapshot=snapshot with {Scanning=true,Error=null};worker=Task.Run(()=>Collect(refresh));}
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
    public async Task Run(CancellationToken stop)
    {
        while(!stop.IsCancellationRequested)
        {
            var delay=Interval;
            try
            {
                await RefreshDisks(false,stop);
                lock(gate)delay=lastDiskScan+Interval-DateTimeOffset.UtcNow;
                if(delay<TimeSpan.FromSeconds(1))delay=TimeSpan.FromSeconds(1);
            }
            catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
            catch{ /* The snapshot exposes sampling failures; retry after the normal interval. */ }
            try{await Task.Delay(delay,stop);}catch(OperationCanceledException){break;}
        }
    }
    public async Task RefreshDisks(bool refresh=false,CancellationToken stop=default)
    {
        await diskScan.WaitAsync(stop);
        try
        {
            // Share one metadata/SMART scan between the background collector and
            // page requests. Folder traversal is deliberately not in this path.
            if(!refresh && DateTimeOffset.UtcNow-lastDiskScan<Interval)return;
            var (filesystems,disks)=await (diskObserver!=null?diskObserver():ReadDisks());
            var at=DateTimeOffset.UtcNow;
            StorageUsageSnapshot current;
            lock(gate){snapshot=snapshot with{Filesystems=filesystems,Disks=disks};current=snapshot;}
            await buffer.Append(VictoriaMetrics.Storage(current,at),stop);
            lock(gate){snapshot=snapshot with{TelemetryError=buffer.Error};lastDiskScan=at;}
        }
        catch
        {
            lock(gate)snapshot=snapshot with{TelemetryError="Disk telemetry could not be sampled. "+buffer.Error};
            throw;
        }
        finally{diskScan.Release();}
    }
    static async Task<(StorageFilesystem[] Filesystems,StorageDiskUsage[] Disks)> ReadDisks()
    {
        var result=await Processes.Run("findmnt",["--json","--df","--bytes","--output","SOURCE,TARGET,FSTYPE,SIZE,USED,AVAIL"],20);
        if(result.ExitCode!=0)throw new IOException();
        var filesystems=Filesystems(result.Output);
        var nodes=await Storage.Nodes();
        var disks=nodes.Where(n=>S(n,"type")=="disk" && !S(n,"path").StartsWith("/dev/zram")).Select(n=>new StorageDiskUsage(S(n,"path"),S(n,"model"),N(n,"size"),Storage.Flatten(n).Where(c=>c.GetProperty("mountpoints").EnumerateArray().Any(m=>m.ValueKind==JsonValueKind.String)).Select(c=>S(c,"path")).Distinct().ToArray(),Storage.Flatten(n).SelectMany(c=>c.GetProperty("mountpoints").EnumerateArray().Where(m=>m.ValueKind==JsonValueKind.String).Select(m=>m.GetString()!)).Distinct().ToArray())).ToArray();
        for(var i=0;i<disks.Length;i++)
        {
            var node=nodes.First(n=>S(n,"path")==disks[i].Path);
            var rotational=S(node,"rota").ToLowerInvariant() switch {"true" or "1"=>true,"false" or "0"=>false,_=>(bool?)null};
            disks[i]=disks[i] with {Endurance=await StorageEnduranceReader.Read(disks[i].Path,rotational)};
        }
        return (filesystems,disks);
    }
    async Task Collect(bool refresh)
    {
        try
        {
            await RefreshDisks(refresh);
            var categories=new List<StorageCategory>();
            var caches=ModelCaches("/var/lib/containers/storage/volumes");
            categories.Add(await Measure("Models",["/var/lib/xur/models","/var/lib/xur/model-sets",..caches]));
            categories.Add(await Measure("Workstation streaming",["/var/lib/xur-streaming","/var/lib/xur-streaming-runtime"]));
            categories.Add(await Measure("Containers & engines",["/var/lib/containers/storage"],caches));
            categories.Add(await Measure("User files",["/var/home"]));
            categories.Add(await Measure("Xur application versions",["/var/lib/xur/app"]));
            categories.Add(await Measure("System logs",["/var/log"]));
            var other=Directory.Exists("/var/lib/xur")?Directory.GetFileSystemEntries("/var/lib/xur").Where(p=>Path.GetFileName(p) is not ("models" or "model-sets" or "app")).ToArray():[];
            categories.Add(await Measure("Xur settings & state",other));
            var volumes=await ContainerLibrary.Volumes();
            lock(gate)snapshot=snapshot with{CapturedAt=DateTimeOffset.UtcNow,Scanning=false,Categories=categories.ToArray(),Volumes=volumes};
        }
        catch {lock(gate)snapshot=snapshot with {CapturedAt=DateTimeOffset.UtcNow,Scanning=false,Error="Storage usage could not be read. Refresh to retry."};}
    }
    public static string[] ModelCaches(string volumes)=>Directory.Exists(volumes)
        ? Directory.GetDirectories(volumes,"xur-cache-*").Where(p=>new DirectoryInfo(p).LinkTarget==null).Select(p=>p+"/_data").Where(p=>Directory.Exists(p)&&new DirectoryInfo(p).LinkTarget==null).ToArray():[];
    static string EscapePattern(string path)=>path.Replace("\\","\\\\").Replace("*","\\*").Replace("?","\\?").Replace("[","\\[");
    public static async Task<StorageCategory> Measure(string name,string[] paths,string[]? excluded=null)
    {
        var existing=paths.Where(p=>Directory.Exists(p)||File.Exists(p)).ToArray();if(existing.Length==0)return new(name,paths,0);
        try
        {
            // One invocation deduplicates hardlinked model files across both paths.
            var r=await Processes.Run("du",["--summarize","--one-file-system","--block-size=1",..(excluded??[]).Select(p=>"--exclude="+EscapePattern(p)),"--",..existing],120);
            if(r.ExitCode!=0)return new(name,paths,null,"Usage unavailable");
            long total=0;foreach(var line in r.Output.Split('\n',StringSplitOptions.RemoveEmptyEntries)){var field=line.Split('\t',2)[0];if(!long.TryParse(field,out var bytes))return new(name,paths,null,"Usage unavailable");total=checked(total+bytes);}
            return new(name,paths,total);
        }catch{return new(name,paths,null,"Usage unavailable");}
    }
}
