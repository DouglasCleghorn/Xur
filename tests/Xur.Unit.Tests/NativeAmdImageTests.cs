using System.Text.Json;
using Xur.Agent;
using Xur.Domain;

static class NativeAmdImageTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(Path.Combine(".build","native-amd-"+Guid.NewGuid().ToString("N")));
        var sys=Path.Combine(root,"sys");var node=Path.Combine(sys,"class/kfd/kfd/topology/nodes/5");Directory.CreateDirectory(node);
        File.WriteAllText(Path.Combine(node,"properties"),"vendor_id 4098\ndomain 0\nlocation_id 25856\ngfx_target_version 110003\n");
        var reference=EngineImages.Image("vllm-rocm-gfx1103");var native="sha256:"+new string('d',64);var stock="sha256:"+new string('e',64);
        var recipe=new Recipe("amd","AMD",EngineImages.For("vLLM","AMD"),["serve","owner/model"],8080,"/health","AMD",1,1,"",Engine:"vLLM");
        var workload=new Workload("amd","AMD",recipe,["0000:65:00.0"],"amd");
        var instance=new RuntimeInstance(workload.Id,workload.Fingerprint,"container-id",0,"boot","","exited",workload.Gpus);
        var calls=new List<string[]>();bool fail=false,cached=false,nativeStopped=false;
        Task<ProcessResult> Run(string exe,string[] args,int timeout)
        {
            calls.Add(args);
            if(args[0]=="build")
            {
                check(args.Contains(reference)&&args.Contains("--iidfile")&&args.Contains("--pull=always")&&timeout==3600,"Native AMD build refreshes its approved base and returns a private image receipt with enough time for HIP compilation");
                var file=File.ReadAllText(args[Array.IndexOf(args,"--file")+1]);
                check(file.Contains("PYTORCH_ROCM_ARCH=gfx1103")&&file.Contains("torch[device-gfx1103]==2.13.0+rocm10.0.0")&&file.Contains("db9527a46873454610df6dbedf79a36d6bf1a7f6"),"Native AMD image aligns PyTorch device kernels with a source-built matching vLLM release");
                if(!fail)File.WriteAllText(args[Array.IndexOf(args,"--iidfile")+1],native);
                return Task.FromResult(new ProcessResult(fail?1:0,fail?"fixture native build failure":"builder output"));
            }
            if(args[0]=="container")return Task.FromResult(new ProcessResult(0,JsonSerializer.Serialize(new[]{new{Id=instance.InstanceId,Image=stock,Config=new{Labels=new Dictionary<string,string>{{"io.xur.fingerprint",workload.Fingerprint}}},State=new{Status="exited",Pid=0}}})));
            if(args[1]=="ls")return Task.FromResult(new ProcessResult(0,stock+" "+recipe.Image+"\n"+(cached?native+" "+reference:"")));
            if(args[0]=="image")return Task.FromResult(new ProcessResult(0,JsonSerializer.Serialize(new[]{new{Id=args[2],Created="2026-10-04T00:00:00Z",Architecture="amd64",Os="linux",Config=new{Labels=new Dictionary<string,string>{{"io.xur.engine-image",nativeStopped?reference:recipe.Image}}}}})));
            if(args[0]=="rm")return Task.FromResult(new ProcessResult(0,"removed"));
            throw new Exception("Unexpected native AMD command");
        }
        try
        {
            var updater=new ModelImageUpdater(Run,Path.Combine(root,"builds"),sys);
            check((await updater.Refresh(workload,null))?.Image==native&&!calls.Any(a=>a[0]=="pull"),"Radeon 780M starts use the native build rather than the incompatible stock image");
            check(Directory.GetDirectories(Path.Combine(root,"builds")).Length==0,"Native AMD build contexts are removed after success");
            fail=true;calls.Clear();bool denied=false;try{await updater.Refresh(workload,instance);}catch(InvalidOperationException e){denied=e.Message.Contains("no cached image");}
            check(denied&&!calls.Any(a=>a[0]=="rm"),"A failed native build cannot fall back to a retained stock ROCm container");
            cached=true;calls.Clear();var offline=await updater.Refresh(workload,instance);
            check(offline?.Image==native&&offline.Recreate&&offline.Warning!=null,"Offline native AMD startup selects its compatible cached build and replaces the stopped stock container");
            cached=false;nativeStopped=true;calls.Clear();offline=await updater.Refresh(workload,instance);
            check(offline?.Image==stock&&!offline.Recreate&&offline.Warning!=null,"A retained native image remains usable after its tag is removed, with verified variant metadata");
            calls.Clear();check(await updater.Refresh(workload,instance with{State="running"})==null&&calls.Count==0,"Running AMD containers are kept without inspecting targets or rebuilding");
            check(workload.Recipe.Image==recipe.Image&&workload.Fingerprint==instance.Fingerprint,"Runtime native image selection preserves saved recipes and fingerprints");
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
