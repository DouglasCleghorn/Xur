using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;
public static class StorageMounts
{
    public static async Task<StorageMount[]> Read(Func<string,string[],int,Task<ProcessResult>>? runner=null)
    {
        var run=runner??((exe,args,timeout)=>Processes.Run(exe,args,timeout));
        var mounts=await run("findmnt",["--json","--list","--df","--bytes","--output","SOURCE,TARGET,FSTYPE,SIZE,USED,AVAIL,OPTIONS,MAJ:MIN"],15);
        var disks=await run("lsblk",["--json","--list","--bytes","--output","PATH,UUID,ROTA,DISC-MAX,MAJ:MIN"],15);
        if(mounts.ExitCode!=0||disks.ExitCode!=0)throw new InvalidOperationException("Could not read mounted storage devices.");
        return Parse(mounts.Output,disks.Output);
    }
    static string S(JsonElement e,string key)=>e.TryGetProperty(key,out var v)?v.ToString():"";
    static long N(JsonElement e,string key)=>long.TryParse(S(e,key),out var n)?n:0;
    public static StorageMount[] Parse(string mounts,string disks)
    {
        using var fs=JsonDocument.Parse(mounts);using var dev=JsonDocument.Parse(disks);
        var nodes=dev.RootElement.GetProperty("blockdevices").EnumerateArray().ToArray();
        return fs.RootElement.GetProperty("filesystems").EnumerateArray().Where(e=>S(e,"source").StartsWith("/dev/")&&S(e,"target").StartsWith('/')&&N(e,"size")>0).Select(e=>{
            var node=nodes.FirstOrDefault(n=>S(n,"maj:min")==S(e,"maj:min"));
            var known=node.ValueKind==JsonValueKind.Object;var uuid=known?S(node,"uuid"):"";
            var source=S(e,"source");var path=S(e,"target");var type=S(e,"fstype");var opts=S(e,"options").Split(',');
            var ssd=known&&S(node,"rota") is "False" or "false" or "0";
            return new StorageMount(Canonical.Hash(new{source,path,uuid}),path,source,type,N(e,"size"),N(e,"used"),N(e,"avail"),ssd,
                ssd&&N(node,"disc-max")>0&&type is "ext4" or "xfs" or "btrfs" or "f2fs"&&!opts.Contains("ro")&&!opts.Contains("X-fstrim.notrim"),opts.Contains("ro"),uuid);
        }).DistinctBy(m=>m.Path).OrderBy(m=>m.Path,StringComparer.Ordinal).ToArray();
    }
}
