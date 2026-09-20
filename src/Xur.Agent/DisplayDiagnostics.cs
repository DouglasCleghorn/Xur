using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xur.Domain;

namespace Xur.Agent;

public static class DisplayDiagnostics
{
    // Fixed read-only probes. No shell, caller-supplied paths, environment dump,
    // kernel command line, user files, network state or general journal export.
    public static async Task<JsonObject> Collect(string sysRoot="/sys",string devRoot="/dev",string procRoot="/proc",bool runCommands=true)
    {
        var errors=new List<string>();
        string Read(string path)
        {
            try {return File.Exists(path)?new string(File.ReadAllText(path).Take(4096).ToArray()).Trim():"";}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException){errors.Add(path+": "+e.GetType().Name);return "";}
        }
        string Link(string path)
        {
            try{return Directory.Exists(path)?SysfsPaths.Resolve(path):"";}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException){errors.Add(path+": "+e.GetType().Name);return "";}
        }
        string[] Dirs(string path)
        {
            try{return Directory.Exists(path)?Directory.GetDirectories(path).Order().Take(256).ToArray():[];}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException){errors.Add(path+": "+e.GetType().Name);return [];}
        }
        object Device(string path)=>new {name=Path.GetFileName(path),path=Link(path),device=Link(path+"/device"),driver=Link(path+"/device/driver"),subsystem=Link(path+"/device/subsystem"),status=Read(path+"/status"),enabled=Read(path+"/enabled"),modes=Read(path+"/modes"),nodeExists=File.Exists(devRoot+"/dri/"+Path.GetFileName(path))};
        GpuDevice[] gpus=[];
        try{gpus=await GpuInventory.Observe(sysRoot,devRoot);}catch(Exception e){errors.Add("GPU inventory: "+e.GetType().Name);}
        var inventory=gpus.Select(g=>new {gpu=g,workstationEligible=g.Cards is {Length:>0},excludedBecause=(g.Cards is {Length:>0}?Array.Empty<string>():["No DRM card node"]).ToArray()}).ToArray();
        var drm=Dirs(sysRoot+"/class/drm").Select(Device).ToArray();
        var framebuffer=Dirs(sysRoot+"/class/graphics").Where(p=>Regex.IsMatch(Path.GetFileName(p),@"^fb[0-9]+$")).Select(p=>new {name=Path.GetFileName(p),device=Link(p+"/device"),driver=Link(p+"/device/driver"),description=Read(p+"/name"),modes=Read(p+"/modes"),virtualSize=Read(p+"/virtual_size"),nodeExists=File.Exists(devRoot+"/"+Path.GetFileName(p))}).ToArray();
        var pci=Dirs(sysRoot+"/bus/pci/devices").Where(p=>Read(p+"/class").StartsWith("0x03")).Select(p=>new {address=Path.GetFileName(p),vendor=Read(p+"/vendor"),device=Read(p+"/device"),driver=Link(p+"/driver"),drmEntries=Dirs(p+"/drm").Select(Path.GetFileName).ToArray()}).ToArray();
        var vmbus=Dirs(sysRoot+"/bus/vmbus/devices").Select(p=>new {id=Path.GetFileName(p),classId=Read(p+"/class_id"),deviceId=Read(p+"/device_id"),driver=Link(p+"/driver"),drmEntries=Dirs(p+"/drm").Select(Path.GetFileName).ToArray()}).ToArray();
        var moduleNames=new[]{"hyperv_drm","hyperv_fb","hv_vmbus","simpledrm","simplefb","bochs","virtio_gpu","vmwgfx","i915","xe","amdgpu","nvidia","nouveau"};
        var modules=moduleNames.Select(n=>new {name=n,present=Directory.Exists(sysRoot+"/module/"+n)}).ToArray();
        var commands=new Dictionary<string,object>();
        if(runCommands)
        {
            foreach(var probe in new[]{("virtualization","systemd-detect-virt",Array.Empty<string>()),("hypervDrmModule","modinfo",new[]{"-F","filename","hyperv_drm"}),("hypervFramebufferModule","modinfo",new[]{"-F","filename","hyperv_fb"}),
                ("nvidiaGpuMapping","nvidia-smi",new[]{"--query-gpu=index,pci.bus_id","--format=csv,noheader,nounits"}),
                ("nvidiaTopology","nvidia-smi",new[]{"topo","-m"}),
                ("nvidiaNvlinkStatus","nvidia-smi",new[]{"nvlink","--status"}),
                ("nvidiaDriver","nvidia-smi",new[]{"--query-gpu=index,pci.bus_id,uuid,driver_version","--format=csv,noheader,nounits"})})
                try{var r=await Processes.Run(probe.Item2,probe.Item3,5);commands[probe.Item1]=new {command=probe.Item2,arguments=probe.Item3,exitCode=r.ExitCode,output=r.Output.Length>16384?r.Output[..16384]+"\n[truncated]":r.Output};}
                catch(Exception e){commands[probe.Item1]=new {command=probe.Item2,arguments=probe.Item3,exitCode=-1,error=e.GetType().Name};}
        }
        var nvidiaDevices=gpus.Where(g=>g.Vendor=="NVIDIA"&&Regex.IsMatch(g.Pci,@"^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$")).Select(g=>new {pci=g.Pci,uuid=g.RuntimeId,driverInformation=Read(procRoot+"/driver/nvidia/gpus/"+g.Pci+"/information")}).ToArray();
        return System.Text.Json.JsonSerializer.SerializeToNode(new {
            schema=1,capturedAt=DateTimeOffset.UtcNow,agentBundle=ApplicationIdentity.Id,
            kernel=Read(procRoot+"/sys/kernel/osrelease"),osRelease=Read("/etc/os-release"),
            summary=inventory.Any(g=>g.workstationEligible)?"Display adapters are available for a workstation.":framebuffer.Length>0&&!drm.Any()?"A framebuffer is present, but no DRM device was found.":"No display adapter currently passes the workstation picker checks.",
            pickerRequirements=new[]{"DRM card node exists"},inventory,drm,framebuffer,pci,vmbus,modules,commands,nvidiaDevices,errors
        },new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.AsObject();
    }
}
