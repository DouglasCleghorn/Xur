using System.Globalization;
using Xur.Domain;
namespace Xur.Agent;
public static class GpuInventory
{
    static string[] Directories(string path,string pattern="*")=>Directory.Exists(path)?Directory.GetDirectories(path,pattern):[];
    static string Link(string path)=>Directory.Exists(path)?SysfsPaths.Resolve(path):"";
    static string Read(string p)=>File.Exists(p) ? File.ReadAllText(p).Trim() : "";
    public static async Task<GpuDevice[]> Observe(string sysRoot="/sys",string devRoot="/dev",bool probeRuntime=true)
    {
        var output=new List<GpuDevice>();
        foreach(var dir in Directories(sysRoot+"/bus/pci/devices").Order(StringComparer.Ordinal))
        {
            if(!Read(dir+"/class").StartsWith("0x03"))continue;
            var pci=Path.GetFileName(dir);var vendor=Read(dir+"/vendor") switch {"0x10de"=>"NVIDIA","0x1002"=>"AMD","0x8086"=>"Intel",_=>"Unknown"};
            var driver=Path.GetFileName(Link(dir+"/driver"));
            var nodes=Directory.Exists(dir+"/drm") ? Directory.GetDirectories(dir+"/drm").Select(Path.GetFileName).Where(n=>n!.StartsWith("renderD")).Select(n=>"/dev/dri/"+n).Where(n=>File.Exists(devRoot+n[4..])).Order().ToArray() : [];
            var cards=Directory.Exists(dir+"/drm") ? Directory.GetDirectories(dir+"/drm").Select(Path.GetFileName).Where(n=>System.Text.RegularExpressions.Regex.IsMatch(n!,@"^card[0-9]+$")).Select(n=>"/dev/dri/"+n).Where(n=>File.Exists(devRoot+n[4..])).Order().ToArray() : [];
            var displays=cards.SelectMany(card=>Directories(sysRoot+"/class/drm",Path.GetFileName(card)+"-*")).Where(c=>Read(c+"/status")=="connected").Select(Path.GetFileName).Select(n=>n!).Order().ToArray();
            var problems=new List<string>();long memory=0;var id="";var name=Read(dir+"/vendor")=="0x1a03" && driver=="ast" ? "ASPEED onboard graphics" : $"{vendor} {Read(dir+"/device")}";
            if(vendor=="NVIDIA")
            {
                if(probeRuntime)try
                {
                    var r=await Processes.Run("nvidia-smi",["--id="+pci,"--query-gpu=uuid,name,memory.total","--format=csv,noheader,nounits"],10);
                    var columns=r.Output.Trim().Split(',',StringSplitOptions.TrimEntries);
                    if(r.ExitCode!=0 || columns.Length!=3 || !columns[0].StartsWith("GPU-"))throw new IOException();
                    id=columns[0];name=columns[1];memory=long.Parse(columns[2],CultureInfo.InvariantCulture);
                    var cdi=await Processes.Run("nvidia-ctk",["cdi","list"],10);
                    if(cdi.ExitCode!=0 || !cdi.Output.Split('\n').Any(l=>l.Trim()=="nvidia.com/gpu="+id))problems.Add("NVIDIA CDI device is missing");
                }catch {problems.Add("NVIDIA driver/runtime is unavailable");}
                if(driver!="nvidia")problems.Add("NVIDIA driver is not bound");
            }
            else if(vendor is "AMD" or "Intel")
            {
                if(nodes.Length==0)problems.Add("No render node");
                if(vendor=="AMD" && (driver!="amdgpu" || !File.Exists(devRoot+"/kfd")))problems.Add("ROCm kernel device is unavailable");
                if(vendor=="Intel" && driver is not ("i915" or "xe"))problems.Add("Intel graphics driver is unavailable");
                if(long.TryParse(Read(dir+"/mem_info_vram_total"),out var bytes))memory=bytes/1048576;
                // Unknown memory cannot satisfy recipes with a minimum. Never infer it from system RAM.
                if(memory==0)problems.Add("Dedicated GPU memory has not been observed");
                id=pci;
            }
            else problems.Add("No supported runtime backend");
            output.Add(new(pci,vendor,name,driver,id,memory,nodes,problems.ToArray(),cards,displays));
        }
        // DRM devices need not be PCI devices (Hyper-V uses VMBus). Keep the
        // persistent bus identity and resolve transient card/render numbers here.
        var drm=Directories(sysRoot+"/class/drm");
        foreach(var group in drm.Where(d=>System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(d),@"^card[0-9]+$") && File.Exists(devRoot+"/dri/"+Path.GetFileName(d)))
            .Select(d=>new{Card=d,Device=Link(d+"/device")}).Where(d=>d.Device.Length>0).GroupBy(d=>d.Device))
        {
            var device=group.Key;
            var cards=group.Select(d=>"/dev/dri/"+Path.GetFileName(d.Card)).Order().ToArray();
            var nodes=drm.Where(d=>System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(d),@"^renderD[0-9]+$") && Link(d+"/device")==device && File.Exists(devRoot+"/dri/"+Path.GetFileName(d))).Select(d=>"/dev/dri/"+Path.GetFileName(d)).Order().ToArray();
            var displays=group.SelectMany(d=>Directories(sysRoot+"/class/drm",Path.GetFileName(d.Card)+"-*")).Where(d=>Read(d+"/status")=="connected").Select(d=>Path.GetFileName(d)).Order().ToArray();
            var existing=output.FindIndex(g=>g.Pci==Path.GetFileName(device));
            if(existing>=0)
            {
                output[existing]=output[existing] with {Cards=cards,Nodes=nodes,Displays=displays};
                continue;
            }
            var driver=Path.GetFileName(Link(device+"/driver"));
            var bus=Path.GetFileName(Link(device+"/subsystem"));
            var relative=Path.GetRelativePath(sysRoot+"/devices",device);
            if(relative.StartsWith("..") || Path.IsPathRooted(relative))continue;
            var id=bus=="vmbus" ? "vmbus:"+Path.GetFileName(device) : "sysfs:"+relative;
            var name=driver switch {"hyperv_drm"=>"Hyper-V virtual display","simple-framebuffer" or "simpledrm"=>"Firmware display",_=>string.IsNullOrEmpty(driver)?"Display adapter":driver+" display"};
            output.Add(new(id,driver=="hyperv_drm"?"Microsoft":"Display",name,driver,"",0,nodes,["Display adapter; no compute runtime"],cards,displays));
        }
        var devices=output.OrderBy(g=>g.Pci,StringComparer.Ordinal).ToArray();
        return sysRoot=="/sys"&&devRoot=="/dev"?new GpuLabels().Assign(devices):devices;
    }
    public static async Task VerifyReleased(GpuDevice[] gpus)
    {
        foreach(var g in gpus)
        {
            GpuOwner[] owners=[];
            for(var attempt=0;attempt<3;attempt++)
            {
                owners=await GpuOwnership.Observe(g);
                if(!owners.Any(o=>o.Blocking))break;
                if(attempt<2)await Task.Delay(500);
            }
            var blockers=owners.Where(o=>o.Blocking).ToArray();
            if(blockers.Length>0)throw new InvalidOperationException($"{g.Name} ({g.Pci}) is in use: "+string.Join("; ",blockers.Select(o=>$"{o.Name} (PID {o.Pid}, {o.Service ?? "outside a managed service"}) holds {string.Join(", ",o.Devices)}"))+". Stop the conflicting workload, then resume.");
        }
    }
}
