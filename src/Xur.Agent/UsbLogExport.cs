using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

// Writes only new report files to existing USB filesystems. Never unlocks,
// formats, repartitions, or remounts a read-only filesystem.
public sealed class UsbLogExport(string run,
    Func<string,string[],Task<ProcessResult>>? runner=null,Func<Task<JsonElement[]>>? observer=null,string? commandLine=null)
{
    readonly SemaphoreSlim gate=new(1,1);
    sealed record Candidate(UsbLogVolume Volume,string Fs,string Mount);
    Task<ProcessResult> Run(string exe,string[] args)=>runner?.Invoke(exe,args)??Processes.Run(exe,args,15);
    static string S(JsonElement node,string key)=>node.TryGetProperty(key,out var v)&&v.ValueKind!=JsonValueKind.Null?v.ToString().Trim():"";
    static bool ReadOnly(JsonElement node)=>S(node,"ro") is "True" or "true" or "1";
    async Task<JsonElement[]> Nodes()
    {
        if(observer!=null)return await observer();
        var result=await Run("lsblk",["--json","--bytes","--paths","--output","NAME,PATH,TYPE,SIZE,MODEL,SERIAL,TRAN,RO,FSTYPE,MOUNTPOINTS,UUID,PARTUUID,LABEL"]);
        if(result.ExitCode!=0)throw new IOException("Could not read USB drives.");
        using var doc=JsonDocument.Parse(result.Output);
        return doc.RootElement.GetProperty("blockdevices").EnumerateArray().Select(n=>n.Clone()).ToArray();
    }
    async Task<Candidate[]> Candidates()
    {
        var nodes=await Nodes();var output=new List<Candidate>();
        var approved=Path.Combine(run,"approved.ks");
        var selected=File.Exists(approved)?File.ReadLines(approved).Where(l=>l.StartsWith("ignoredisk --only-use=",StringComparison.Ordinal)).Select(l=>"/dev/"+l[22..].Trim()).ToHashSet():[];
        // Reject duplicate identities, including UUID copies on internal disks.
        var all=nodes.SelectMany(Storage.Flatten).DistinctBy(n=>S(n,"path")).ToArray();
        var cmdline=commandLine??await File.ReadAllTextAsync("/proc/cmdline");
        foreach(var disk in nodes.Where(n=>S(n,"type")=="disk"&&S(n,"tran")=="usb"&&!ReadOnly(n)&&S(n,"fstype") is not ("iso9660" or "udf")))
        {
            var serial=S(disk,"serial");var diskPath=S(disk,"path");
            if(serial.Length==0 || selected.Contains(diskPath) || nodes.Count(n=>S(n,"serial")==serial)!=1)continue;
            foreach(var node in Storage.Flatten(disk).Where(n=>S(n,"type") is "disk" or "part"))
            {
                var uuid=S(node,"uuid");var fs=S(node,"fstype");var path=S(node,"path");
                if(ReadOnly(node)||uuid.Length==0||all.Count(n=>S(n,"uuid")==uuid)!=1||fs is not ("vfat" or "exfat" or "ntfs" or "ntfs3"))continue;
                if(node.TryGetProperty("children",out var children)&&children.GetArrayLength()>0)continue;
                var mount=node.TryGetProperty("mountpoints",out var mounts)?mounts.EnumerateArray().Where(m=>m.ValueKind==JsonValueKind.String).Select(m=>m.GetString()).FirstOrDefault():null;
                if(!string.IsNullOrEmpty(mount))
                {
                    var observed=await Run("findmnt",["--noheadings","--output","OPTIONS","--mountpoint",mount]);
                    if(observed.ExitCode!=0||!observed.Output.Trim().Split(',').Contains("rw"))continue;
                }
                var id=Canonical.Hash(new{diskPath,serial,Size=S(disk,"size"),path,uuid,fs,Part=S(node,"partuuid")});
                output.Add(new(new(id,path,S(node,"label"),S(disk,"model"),Storage.IsBootMedia(disk,cmdline)),fs,mount??""));
            }
        }
        return output.ToArray();
    }
    public async Task<UsbLogVolume[]> List()=>(await Candidates()).Select(c=>c.Volume).ToArray();
    public async Task<UsbLogReceipt> Save(string id,string report)
    {
        await gate.WaitAsync();
        try
        {
            var candidate=(await Candidates()).SingleOrDefault(c=>c.Volume.Id==id)??throw new InvalidOperationException("USB drive changed or is read-only. Refresh the drive list.");
            var owned=candidate.Mount.Length==0;
            var mount=owned?Path.Combine(run,"log-export-"+Guid.NewGuid().ToString("N")):candidate.Mount;
            var mounted=false;
            try
            {
                if(owned)
                {
                    Directory.CreateDirectory(mount);
                    var result=await Run("mount",["-t",candidate.Fs=="ntfs"?"ntfs3":candidate.Fs,"-o","rw,nosuid,nodev,noexec,umask=077",candidate.Volume.Path,mount]);
                    if(result.ExitCode!=0)throw new IOException("USB mount failed. Use a writable FAT32 or exFAT flash drive.");
                    mounted=true;
                }
                // Re-observe after mounting as a removed drive can reuse the same /dev name.
                if(!(await Candidates()).Any(c=>c.Volume.Id==id))throw new InvalidOperationException("USB identity changed before export.");
                var name="xur-diagnostics-"+DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8]+".txt";
                var file=Path.Combine(mount,name);
                await using(var stream=new FileStream(file,new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Options=FileOptions.WriteThrough}))
                {
                    var bytes=System.Text.Encoding.UTF8.GetBytes(Redaction.Logs(report));
                    await stream.WriteAsync(bytes);stream.Flush(true);
                }
                if((await Run("sync",["-f",file])).ExitCode!=0)throw new IOException("USB report was written but flushing failed. Keep the drive connected and retry.");
                return new("Saved "+name+" on "+candidate.Volume.Path+".");
            }
            finally
            {
                if(mounted)
                {
                    if((await Run("umount",[mount])).ExitCode!=0)throw new IOException("USB report may be written, but unmount failed. Keep the drive connected until shutdown.");
                    Directory.Delete(mount);
                }
                else if(owned&&Directory.Exists(mount))Directory.Delete(mount);
            }
        }
        finally{gate.Release();}
    }
}
