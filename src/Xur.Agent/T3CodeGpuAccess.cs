using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xur.Domain;
namespace Xur.Agent;

public static class T3CodeGpuAccess
{
    public const string Label="io.xur.t3-gpu-access";
    const string Policy="all-v1";
    // Keep the saved recipe fingerprint stable so old containers can still be
    // inspected and stopped. Profile planning replaces the old runtime policy.
    public static string Fingerprint(Workload workload,JsonElement labels)=>
        labels.TryGetProperty(Label,out var policy) && policy.GetString()==Policy ? workload.Fingerprint
        : Canonical.Hash(new{Workload=workload.Fingerprint,GpuAccess="legacy"});

    public static async Task<string[]> Arguments(GpuDevice[] hardware,string devRoot="/dev",
        Func<string,string[],int,Task<ProcessResult>>? execute=null)
    {
        var run=execute??((string exe,string[] args,int seconds)=>Processes.Run(exe,args,seconds));
        var args=new List<string>{"--label",Label+"="+Policy};
        var nodes=hardware.SelectMany(StationDeviceAccess.Nodes).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var direct=hardware.Where(g=>g.Vendor!="NVIDIA").SelectMany(StationDeviceAccess.Nodes).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach(var node in direct)args.AddRange(["--device",node]);
        var nvidia=hardware.Any(g=>g.Vendor=="NVIDIA");
        if(nvidia)
        {
            args.AddRange(["--device","nvidia.com/gpu=all"]);
            // CDI injects the driver libraries and device nodes. Carry their
            // host GIDs as well: the image's node user must be able to open them.
            nodes.Add("/dev/nvidiactl");
            foreach(var path in Directory.EnumerateFiles(devRoot,"nvidia*"))
                if(Regex.IsMatch(Path.GetFileName(path),@"^nvidia([0-9]+|ctl|-modeset|-uvm|-uvm-tools)$"))nodes.Add("/dev/"+Path.GetFileName(path));
            var caps=Path.Combine(devRoot,"nvidia-caps");
            if(Directory.Exists(caps))foreach(var path in Directory.EnumerateFiles(caps,"nvidia-cap*"))nodes.Add("/dev/nvidia-caps/"+Path.GetFileName(path));
        }
        if(hardware.Any(g=>g.Vendor=="AMD")){args.AddRange(["--device","/dev/kfd"]);nodes.Add("/dev/kfd");}
        if(nodes.Count==0&&!nvidia)return args.ToArray();
        args.Add("--security-opt=label=disable");
        if(hardware.Any(g=>g.Vendor is "AMD" or "Intel"))args.Add("--security-opt=seccomp=unconfined");
        if(hardware.Any(g=>g.Vendor=="Intel"))args.AddRange(["--env","ZE_ENABLE_PCI_ID_DEVICE_ORDER=1"]);
        var devices=nodes.Order(StringComparer.Ordinal).ToArray();
        var permissions=await run("stat",["--dereference","--format=%a:%g","--",..devices.Select(n=>Path.Combine(devRoot,n[5..]))],10);
        var lines=permissions.Output.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        if(permissions.ExitCode!=0 || lines.Length!=devices.Length)throw new InvalidOperationException("Could not read T3 Code GPU device permissions. Check the host GPU drivers and retry. "+Redaction.Logs(permissions.Output));
        var groups=new SortedSet<uint>();
        foreach(var line in lines)
        {
            var fields=line.Split(':');
            if(fields.Length!=2 || !Regex.IsMatch(fields[0],@"^[0-7]{3,4}$") || !uint.TryParse(fields[1],NumberStyles.None,CultureInfo.InvariantCulture,out var gid))
                throw new InvalidOperationException("Invalid T3 Code GPU device permissions.");
            var mode=Convert.ToInt32(fields[0],8);
            // World-accessible nodes need no extra group, especially not GID 0.
            var groupAccess=(mode>>3)&6;var otherAccess=mode&6;
            if((groupAccess&~otherAccess)!=0)groups.Add(gid);
        }
        foreach(var gid in groups)args.AddRange(["--group-add",gid.ToString(CultureInfo.InvariantCulture)]);
        return args.ToArray();
    }
}
