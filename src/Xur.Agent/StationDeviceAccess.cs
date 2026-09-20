using System.Text.Json;
using System.Text.RegularExpressions;
using Xur.Domain;
namespace Xur.Agent;

// Device cgroups restrict which card may be opened. Headless sessions also need
// a per-user filesystem ACL; logind's active-seat ACL is not available there.
public sealed class StationDeviceAccess(string root="/var/lib/xur/station-device-access",
    Func<string,string[],int,Task<ProcessResult>>? runner=null,string bootFile="/proc/sys/kernel/random/boot_id")
{
    record Grant(string Device,string Identity,string? Previous);
    record Receipt(string Boot,int Uid,Grant[] Grants);
    Task<ProcessResult> Run(string exe,string[] args)=>runner!=null?runner(exe,args,10):Processes.Run(exe,args,10);
    async Task<string> Checked(string exe,string[] args)
    {var r=await Run(exe,args);if(r.ExitCode!=0)throw new InvalidOperationException("Workstation device access failed: "+exe+". "+Redaction.Logs(r.Output));return r.Output.Trim();}
    string PathFor(string id)
    {if(!ProfilePolicy.EntityIdentifier(id))throw new InvalidOperationException("Invalid workstation ID.");return Path.Combine(root,id+".json");}
    public static string[] Nodes(GpuDevice gpu)=> (gpu.Cards??[]).Concat(gpu.Nodes).Distinct().Select(n=>
        Regex.IsMatch(n,@"^/dev/dri/(card[0-9]+|renderD[0-9]+)$")?n:throw new InvalidOperationException("Invalid workstation DRM node.")).ToArray();
    public async Task GrantAccess(string id,int uid,GpuDevice gpu)
    {
        if(uid<1000||uid>=65534)throw new InvalidOperationException("Invalid workstation user.");
        var path=PathFor(id);var boot=File.ReadAllText(bootFile).Trim();
        Receipt receipt=File.Exists(path)?JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path))!:new(boot,uid,[]);
        if(receipt.Boot!=boot)receipt=new(boot,uid,[]);
        if(receipt.Uid!=uid)throw new InvalidOperationException("Workstation device access belongs to another user; stop it first.");
        foreach(var device in Nodes(gpu))
        {
            var identity=await Checked("stat",["--format=%t:%T",device]);
            var acl=await Checked("getfacl",["--numeric","--omit-header",device]);
            var mask=acl.Split('\n').FirstOrDefault(l=>l.StartsWith("mask::"))?[6..].Trim();
            // Widening an existing restrictive mask would also grant access to
            // unrelated users/groups. Require explicit resolution instead.
            if(mask!=null&&(!mask.Contains('r')||!mask.Contains('w')))
                throw new InvalidOperationException("The selected DRM device has a restrictive ACL mask. Device permissions need review before starting the workstation.");
            var old=receipt.Grants.SingleOrDefault(g=>g.Device==device);
            if(old!=null&&old.Identity!=identity)throw new InvalidOperationException("Workstation DRM device changed; stop and reload the profile.");
            if(old==null)
            {
                var previous=acl.Split('\n').Select(l=>l.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()??"").SingleOrDefault(l=>l.StartsWith("user:"+uid+":"));
                receipt=receipt with{Grants=[..receipt.Grants,new(device,identity,previous)]};
                Directory.CreateDirectory(root);File.SetUnixFileMode(root,(UnixFileMode)448);
                await File.WriteAllTextAsync(path+".tmp",JsonSerializer.Serialize(receipt));File.SetUnixFileMode(path+".tmp",(UnixFileMode)384);File.Move(path+".tmp",path,true);
            }
            await Checked("setfacl",["--no-mask","--modify","user:"+uid+":rw-,mask::"+(mask??"rw-"),device]);
        }
    }
    public async Task Revoke(string id)
    {
        var path=PathFor(id);if(!File.Exists(path))return;
        var receipt=JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path))!;
        if(receipt.Boot==File.ReadAllText(bootFile).Trim())foreach(var grant in receipt.Grants)
        {
            var stat=await Run("stat",["--format=%t:%T",grant.Device]);
            if(stat.ExitCode!=0||stat.Output.Trim()!=grant.Identity)continue;
            // Restore only our user's entry; preserve concurrent logind ACLs.
            await Checked("setfacl",grant.Previous!=null?["--no-mask","--modify",grant.Previous,grant.Device]:["--no-mask","--remove","user:"+receipt.Uid,grant.Device]);
        }
        File.Delete(path);
    }
}
