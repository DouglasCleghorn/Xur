using System.Text.Json;
using Xur.Agent;
using Xur.Domain;

static class T3CodeGpuTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(".build/evidence/t3-gpus-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var nodes=new Dictionary<string,string>{
            ["nvidia0"]="660:989",["nvidia1"]="666:0",["nvidiactl"]="666:0",["nvidia-uvm"]="666:0",
            ["nvidia-caps/nvidia-cap1"]="440:989",["kfd"]="660:109",
            ["dri/card0"]="660:44",["dri/renderD128"]="660:109",
            ["dri/card1"]="660:44",["dri/renderD129"]="660:109",
            ["dri/card2"]="660:44",["dri/renderD130"]="660:109",
            ["dri/card3"]="660:44",["dri/renderD131"]="660:109"};
        foreach(var node in nodes.Keys){var path=Path.Combine(root,node);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,"");}
        var calls=new List<string[]>();bool fail=false,invalid=false;
        Task<ProcessResult> Stat(string exe,string[] args,int seconds)
        {
            if(exe!="stat" || !args.Take(3).SequenceEqual(new[]{"--dereference","--format=%a:%g","--"}))throw new Exception("Unexpected GPU permission probe");
            calls.Add(args);
            return Task.FromResult(new ProcessResult(fail?1:0,fail?"Fixture device disappeared":string.Join('\n',args.Skip(3).Select(p=>invalid?"660:invalid":nodes[Path.GetRelativePath(root,p)]))));
        }
        GpuDevice Gpu(string vendor,int index)=>new($"0000:0{index+1}:00.0",vendor,vendor+" fixture",vendor=="NVIDIA"?"nvidia":vendor=="AMD"?"amdgpu":"xe","GPU-"+index,0,
            ["/dev/dri/renderD"+(128+index)],["Dedicated GPU memory has not been observed"],["/dev/dri/card"+index]);
        var hardware=new[]{Gpu("NVIDIA",0),Gpu("NVIDIA",1),Gpu("AMD",2),Gpu("Intel",3)};
        string[] Values(string[] args,string option)=>args.Select((arg,index)=>(arg,index)).Where(v=>v.arg==option).Select(v=>args[v.index+1]).ToArray();
        try
        {
            var empty=await T3CodeGpuAccess.Arguments([],root,Stat);
            check(calls.Count==0 && empty.SequenceEqual(new[]{"--label",T3CodeGpuAccess.Label+"=all-v1"}),"GPU-free T3 hosts need no GPU devices, groups or security exceptions");
            var nvidia=await T3CodeGpuAccess.Arguments(hardware[..2],root,Stat);
            check(Values(nvidia,"--device").SequenceEqual(new[]{"nvidia.com/gpu=all"})&&!nvidia.Any(a=>a.StartsWith("CUDA_VISIBLE_DEVICES=")),"T3 exposes every NVIDIA GPU through CDI without a CUDA device subset");
            check(!nvidia.Contains("--security-opt=seccomp=unconfined")&&!nvidia.Contains("/dev/kfd"),"NVIDIA-only T3 containers keep the default syscall filter and omit AMD devices");
            var mixed=await T3CodeGpuAccess.Arguments(hardware,root,Stat);
            var devices=Values(mixed,"--device");
            check(devices.Contains("nvidia.com/gpu=all")&&devices.Contains("/dev/kfd")&&devices.Contains("/dev/dri/renderD130")&&devices.Contains("/dev/dri/renderD131")&&devices.Contains("/dev/dri/card2")&&devices.Contains("/dev/dri/card3"),"Mixed-vendor T3 containers expose all GPU devices, including integrated GPUs without dedicated VRAM");
            check(devices.Distinct().Count()==devices.Length&&Values(mixed,"--group-add").SequenceEqual(new[]{"44","109","989"}),"T3 receives deduplicated numeric host GPU groups while world-accessible NVIDIA devices do not grant group zero");
            check(mixed.Contains("--security-opt=label=disable")&&mixed.Contains("--security-opt=seccomp=unconfined")&&mixed.Contains("ZE_ENABLE_PCI_ID_DEVICE_ORDER=1")&&!mixed.Contains("--privileged"),"AMD and Intel T3 access includes compute runtime permissions without a privileged container");
            check(calls.Last().Skip(3).Contains(Path.Combine(root,"nvidia-caps/nvidia-cap1")),"NVIDIA capability-node permissions are included for the non-root T3 user");
            var intel=await T3CodeGpuAccess.Arguments([hardware[3]],root,Stat);
            check(!intel.Contains("nvidia.com/gpu=all")&&!intel.Contains("/dev/kfd")&&Values(intel,"--device").Length==2,"Intel-only hosts expose their own DRM devices without other vendors' runtime requirements");
            var amd=await T3CodeGpuAccess.Arguments([hardware[2]],root,Stat);
            check(amd.Contains("/dev/kfd")&&!amd.Contains("nvidia.com/gpu=all"),"AMD-only hosts receive KFD and DRM access without requiring NVIDIA CDI");
            async Task<bool> Rejected(){try{await T3CodeGpuAccess.Arguments(hardware,root,Stat);return false;}catch(InvalidOperationException){return true;}}
            fail=true;check(await Rejected(),"Missing GPU permission data fails visibly instead of silently dropping GPU access");
            fail=false;invalid=true;check(await Rejected(),"Invalid device group data cannot become a container argument");
            foreach(var node in new[]{"dri/card3","dri/renderD131"})File.SetUnixFileMode(Path.Combine(root,node),UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.GroupRead|UnixFileMode.GroupWrite);
            var real=await T3CodeGpuAccess.Arguments([hardware[3]],root);
            check(Values(real,"--group-add").Length==1&&uint.TryParse(Values(real,"--group-add")[0],out _),"The real stat probe resolves numeric host groups for group-only device access");

            var workload=new Workload("t3","T3 Code",T3CodeTests.Recipe(),[],"coding");
            var model=new Workload("model","Model",workload.Recipe with{Engine="Podman",Vendor="NVIDIA",GpuCount=1},[hardware[0].Pci],"model");
            var healthy=hardware.Select(g=>g with{MemoryMiB=16384,Problems=[]}).ToArray();
            ProfilePolicy.Validate(new("coding","Coding",1,[workload,model]),new("g",healthy,[]));
            check(workload.Gpus.Length==0,"T3 shares GPU access without consuming exclusive profile assignments");
            check(ModelGpuArguments.For(model,healthy).Contains("nvidia.com/gpu=GPU-0")&&!ModelGpuArguments.For(model,healthy).Contains("nvidia.com/gpu=all"),"Other containers retain their selected-GPU isolation");
            using var oldLabels=JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string,string>{{"io.xur.fingerprint",workload.Fingerprint}}));
            using var newLabels=JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string,string>{{T3CodeGpuAccess.Label,"all-v1"}}));
            var old=new RuntimeInstance(workload.Id,T3CodeGpuAccess.Fingerprint(workload,oldLabels.RootElement),"old-container",123,"boot","http://localhost:3773/","running",[]);
            var profile=new Profile("coding","Coding",1,[workload]);
            var replacement=ProfilePolicy.Steps(profile,new("g",healthy,[old]));
            check(replacement.Any(s=>s.Kind=="Stop")&&replacement.Any(s=>s.Kind=="Start")&&!replacement.Any(s=>s.Kind=="Keep"),"Loading an existing T3 profile replaces the legacy container so shared GPU access takes effect");
            var current=old with{Fingerprint=T3CodeGpuAccess.Fingerprint(workload,newLabels.RootElement)};
            check(ProfilePolicy.Steps(profile,new("g",healthy,[current])).Any(s=>s.Kind=="Keep")&&current.Fingerprint==workload.Fingerprint,"T3 containers with current GPU access survive ordinary profile reloads");
            check(profile.Workloads.Single().Recipe.Container!.Mounts.Single().Volume==T3CodeTests.Recipe().Container!.Mounts.Single().Volume,"GPU policy migration retains the existing recipe and persistent home volume");
        }
        finally{Directory.Delete(root,true);}
    }
}
