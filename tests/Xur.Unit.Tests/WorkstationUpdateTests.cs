using Xur.Agent;
using Xur.Control;
using Xur.Domain;
public static class WorkstationUpdateTests
{
    public static void Run(Action<bool,string> check)
    {
        var account=new ManagerAccountStore(null);account.Create("Doug+Xur@example.com","test password for email");
        check(account.Verify("doug+xur@EXAMPLE.COM","test password for email"),"Email usernames support case-insensitive login and plus addressing");
        check(!ManagerAccountStore.ValidUsername("Doug <doug@example.com>")&&!ManagerAccountStore.ValidUsername("a@b\n"),"Display names and control characters are rejected as usernames");
        var mapping="3, 00000000:41:00.0\n0, 00000000:01:00.0\n2, 00000000:81:00.0";
        var matrix="        GPU3 GPU0 GPU2 CPU Affinity\nGPU3 X NV2 SYS 0-15\nGPU0 NV2 X SYS 0-15\nGPU2 SYS SYS X 16-31";
        var topology=NvLinkTopology.Parse(mapping,matrix,new(){["0000:41:00.0"]="Link 0: 14.062 GB/s\nLink 1: 14.062 GB/s",["0000:01:00.0"]="Link 0: 14.062 GB/s\nLink 1: 14.062 GB/s"});
        check(topology.Single(l=>l.Connection=="NVLink") is {From:"0000:01:00.0",To:"0000:41:00.0",State:"Active",LinkCount:2},"NVLink follows observed PCI identities despite nonsequential GPU indices");
        check(NvLinkTopology.Parse(mapping,matrix,[]).Single(l=>l.Connection=="NVLink").State=="Unknown","Missing NVLink status never becomes active");
        var gpu=new GpuDevice("0000:01:00.0","NVIDIA","RTX 3090","nvidia","GPU-test",24576,["/dev/dri/renderD131"],[],["/dev/dri/card4"],[]);
        var recipe=new Recipe("gaming-workstation","Workstation","host:plasma",[],0,"","Display",1,0,"",Kind:"Workstation",Engine:"Plasma");
        var workload=new Workload("1","Workstation",recipe,[gpu.Pci],"station");var profile=new Profile("1","Profile",1,[workload]);
        ProfilePolicy.Validate(profile,new("1",[gpu],[]));check(true,"Disconnected display-capable GPUs can be saved in workstation profiles");
        var conf=StationStreaming.Configuration(workload,gpu,"/var/lib/xur-streaming/1");check(conf.Contains("lan_encryption_mode = 2")&&conf.Contains("wan_encryption_mode = 2")&&conf.Contains("origin_web_ui_allowed = pc")&&conf.Contains("encoder = nvenc")&&conf.Contains("renderD131"),"Streaming requires encryption and binds the selected NVIDIA encoder device");
        var stations=WorkstationView.Build(new([profile,profile with{Id="2"}],null,new("1",[gpu],[]),null),[]);
        check(stations.Length==1&&stations[0].Profiles.Length==2&&stations[0].State=="Stopped","Workstations page retains disconnected stopped stations and deduplicates shared definitions");
        check(NetworkUsage.Rates(200,400,100,200,2)==(50d,100d)&&NetworkUsage.Rates(1,2,100,200,1)==(null,null),"Network rates use elapsed time and counter resets produce gaps");
        bool rejected=false;try{StationLauncher.Create("host;rm -rf /","linux");}catch(InvalidOperationException){rejected=true;}
        check(rejected&&StationLauncher.Create("fd71::2","linux").Text.Contains("[fd71::2]"),"Launch shortcuts reject shell syntax and support IPv6");
        var root=Path.Combine(Path.GetTempPath(),"xur-model-fixture-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try
        {
            var proc=root+"/proc/42";Directory.CreateDirectory(proc+"/fd");File.CreateSymbolicLink(proc+"/fd/3","/dev/nvidia0");File.CreateSymbolicLink(proc+"/exe","/usr/bin/nvidia-persistenced");
            File.WriteAllText(proc+"/comm","nvidia-persistenced");File.WriteAllText(proc+"/status","Uid:\t0\t0\t0\t0\n");File.WriteAllText(proc+"/cgroup","0::/system.slice/unrelated.service");
            check(GpuOwnership.ObserveProcesses(["/dev/nvidia0"],procRoot:root+"/proc").Single().Blocking,"A process name cannot bypass GPU ownership checks");
            File.WriteAllText(proc+"/cgroup","0::/system.slice/nvidia-persistenced.service");
            check(!GpuOwnership.ObserveProcesses(["/dev/nvidia0"],procRoot:root+"/proc").Single().Blocking&&GpuOwnership.ObserveProcesses(["/dev/nvidia0"],[42],root+"/proc").Single().Blocking,"Vendor persistence handles are distinguished from actual compute contexts");
            Directory.Delete(root+"/proc",true);
            File.WriteAllBytes(root+"/model.gguf","GGUFtest"u8.ToArray());File.WriteAllText(root+"/config.gguf","{}");File.WriteAllBytes(root+"/model.safetensors",new byte[16]);Directory.CreateSymbolicLink(root+"/cycle",root);
            var found=new Dictionary<string,ModelFolder>();var count=0;ModelLibrary.Scan(root,"test",found,[],[],ref count,DateTimeOffset.UtcNow.AddMinutes(1));
            check(found.Count==2&&found[root+"/model.gguf"].Bytes==8&&found[root].Format=="Safetensors","Model scan finds weights, ignores JSON disguised as GGUF and avoids symlink cycles");
        }finally{Directory.Delete(root,true);}
    }
}
