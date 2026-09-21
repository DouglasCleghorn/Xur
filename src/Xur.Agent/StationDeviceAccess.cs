using System.Text.Json;
using System.Text.RegularExpressions;
using Xur.Domain;
namespace Xur.Agent;

// Device cgroups restrict which card may be opened. Headless sessions also need
// a per-user filesystem ACL; logind's active-seat ACL is not available there.
public sealed class StationDeviceAccess(string root="/var/lib/xur/station-device-access",
    Func<string,string[],int,Task<ProcessResult>>? runner=null,string bootFile="/proc/sys/kernel/random/boot_id")
{
    record Grant(string Device,string Identity,string? Previous,string? PreviousMask=null,string? GrantedMask=null);
    record Receipt(string Boot,int Uid,Grant[] Grants);
    Task<ProcessResult> Run(string exe,string[] args)=>runner!=null?runner(exe,args,10):Processes.Run(exe,args,10);
    async Task<string> Checked(string exe,string[] args)
    {var r=await Run(exe,args);if(r.ExitCode!=0)throw new InvalidOperationException("Workstation device access failed for "+args[^1]+": "+exe+". "+Redaction.Logs(r.Output));return r.Output.Trim();}
    static string[] Entries(string acl)=>acl.Split('\n').Select(l=>l.Split('#',2)[0].Trim()).Where(l=>l.Length>0).ToArray();
    static int Bits(string permissions)=>(permissions.Contains('r')?4:0)|(permissions.Contains('w')?2:0)|(permissions.Contains('x')?1:0);
    static string Permissions(int bits)=>string.Concat((bits&4)!=0?'r':'-',(bits&2)!=0?'w':'-',(bits&1)!=0?'x':'-');
    // The owner and other entries are not masked. Every group entry (including
    // group::) and every other named user must retain its effective permissions.
    static string? BroadenedEntry(string[] entries,int uid,int before,int after)=>entries.FirstOrDefault(entry=>
        (entry.StartsWith("group:")||entry.StartsWith("user:")&&!entry.StartsWith("user::")&&!entry.StartsWith("user:"+uid+":"))
        &&(Bits(entry.Split(':')[^1])&after&~before)!=0);
    string PathFor(string id)
    {if(!ProfilePolicy.EntityIdentifier(id))throw new InvalidOperationException("Invalid workstation ID.");return Path.Combine(root,id+".json");}
    public static string[] Nodes(GpuDevice gpu)=> (gpu.Cards??[]).Concat(gpu.Nodes).Distinct().Select(n=>
        Regex.IsMatch(n,@"^/dev/dri/(card[0-9]+|renderD[0-9]+)$")?n:throw new InvalidOperationException("Invalid workstation DRM node.")).ToArray();
    public Task GrantAccess(string id,int uid,GpuDevice gpu)=>GrantNodes(id,uid,Nodes(gpu));
    public async Task SetNodes(string id,int uid,string[] nodes)
    {
        var path=PathFor(id);
        if(File.Exists(path)) {
            await Revoke(id);
        }
        await GrantNodes(id,uid,nodes);
    }
    public async Task GrantNodes(string id,int uid,string[] nodes)
    {
        if(uid<1000||uid>=65534)throw new InvalidOperationException("Invalid workstation user.");
        var path=PathFor(id);var boot=File.ReadAllText(bootFile).Trim();
        Receipt receipt=File.Exists(path)?JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path))!:new(boot,uid,[]);
        if(receipt.Boot!=boot)receipt=new(boot,uid,[]);
        if(receipt.Uid!=uid)throw new InvalidOperationException("Workstation device access belongs to another user; stop it first.");
        foreach(var device in nodes)
        {
            var identity=await Checked("stat",["--format=%t:%T:%i",device]);
            var acl=await Checked("getfacl",["--numeric","--omit-header",device]);
            var entries=Entries(acl);
            var mask=entries.FirstOrDefault(l=>l.StartsWith("mask::"))?[6..];
            var grantedMask=Permissions(Bits(mask??entries.FirstOrDefault(l=>l.StartsWith("group::"))?[7..]??"---")|6);
            // logind can leave an empty/restricted mask after removing the last
            // session user. Restore rw only if no other entry gains access.
            if(mask!=null && BroadenedEntry(entries,uid,Bits(mask),Bits(grantedMask)) is {} blocked)
                throw new InvalidOperationException($"Cannot grant workstation user {uid} read/write access to {device}: ACL mask {mask} restricts {blocked}. Expanding the mask would change another user or group's permissions. Review this device's ACL in Diagnostics.");
            var old=receipt.Grants.SingleOrDefault(g=>g.Device==device);
            if(old!=null&&old.Identity!=identity)throw new InvalidOperationException("Workstation device "+device+" changed; stop and reload the profile.");
            if(old==null)
            {
                var previous=entries.SingleOrDefault(l=>l.StartsWith("user:"+uid+":"));
                receipt=receipt with{Grants=[..receipt.Grants,new(device,identity,previous,mask,grantedMask)]};
                Directory.CreateDirectory(root);File.SetUnixFileMode(root,(UnixFileMode)448);
                await File.WriteAllTextAsync(path+".tmp",JsonSerializer.Serialize(receipt));File.SetUnixFileMode(path+".tmp",(UnixFileMode)384);File.Move(path+".tmp",path,true);
            }
            await Checked("setfacl",["--no-mask","--modify","user:"+uid+":rw-,mask::"+grantedMask,device]);
        }
    }
    public async Task Revoke(string id)
    {
        var path=PathFor(id);if(!File.Exists(path))return;
        var receipt=JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path))!;
        if(receipt.Boot==File.ReadAllText(bootFile).Trim())foreach(var grant in receipt.Grants)
        {
            var stat=await Run("stat",["--format=%t:%T:%i",grant.Device]);
            if(stat.ExitCode!=0||stat.Output.Trim()!=grant.Identity)continue;
            var entries=Entries(await Checked("getfacl",["--numeric","--omit-header",grant.Device]));
            var mask=entries.FirstOrDefault(l=>l.StartsWith("mask::"))?[6..];
            // Restore our mask change only when it is still ours and doing so
            // won't reduce access subsequently granted by logind/another station.
            var restoreMask=grant.PreviousMask!=null&&mask==grant.GrantedMask&&mask!=grant.PreviousMask
                &&BroadenedEntry(entries,receipt.Uid,Bits(grant.PreviousMask),Bits(mask!))==null;
            var previous=grant.Previous;
            if(previous!=null&&grant.PreviousMask!=null&&!restoreMask&&mask!=grant.PreviousMask)
                previous="user:"+receipt.Uid+":"+Permissions(Bits(previous.Split(':')[^1])&Bits(grant.PreviousMask));
            var args=new List<string>{"--no-mask"};
            if(previous!=null)args.AddRange(["--modify",previous]);
            else if(entries.Any(l=>l.StartsWith("user:"+receipt.Uid+":")))args.AddRange(["--remove","user:"+receipt.Uid]);
            if(restoreMask)args.AddRange(["--modify","mask::"+grant.PreviousMask]);
            if(args.Count>1)await Checked("setfacl",[..args,grant.Device]);
        }
        File.Delete(path);
    }
}
