using System.Text.Json;
using Xur.Agent;
using Xur.Domain;

static class OmniXpuImageTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(Path.Combine(".build","omni-xpu-"+Guid.NewGuid().ToString("N")));
        var image="sha256:"+new string('d',64);
        var recipe=new Recipe("xpu","XPU",EngineImages.Image("omni-xpu"),["serve","owner/model","--omni"],8080,"/health","Intel",1,1,"",Engine:"vLLM-Omni");
        var workload=new Workload("xpu","XPU",recipe,["0000:01:00.0"],"xpu");
        var calls=new List<string[]>();bool fail=false,cached=false;
        Task<ProcessResult> Run(string exe,string[] args,int timeout)
        {
            calls.Add(args);
            if(args[0]=="build")
            {
                var file=File.ReadAllText(args[Array.IndexOf(args,"--file")+1]);
                check(file.Contains("FROM mirror.gcr.io/vllm/vllm-openai-xpu:v0.30.0")&&file.Contains("a8576ccb725c4e21cd13c3eb5f9a546b21149d2b"),"Intel Omni build uses the mirrored XPU base and pinned matching upstream source");
                check(file.Contains("VLLM_OMNI_VERSION_OVERRIDE=0.30.0+xpu")&&file.Contains("--constraint /tmp/xpu-constraints.txt")&&file.Contains("torch.xpu._is_compiled()"),"Intel Omni build retains XPU dependencies and checks backend/release compatibility");
                check(args.Contains("--pull=always")&&args.Contains("--arch=amd64")&&args.Contains(recipe.Image)&&timeout==1800,"Intel Omni build refreshes the approved base with bounded execution");
                if(!fail)File.WriteAllText(args[Array.IndexOf(args,"--iidfile")+1],image);
                return Task.FromResult(new ProcessResult(fail?1:0,fail?"mirror cache miss":"Build output is not an image identity"));
            }
            if(args[1]=="ls")return Task.FromResult(new ProcessResult(0,cached?image+" "+recipe.Image:""));
            return Task.FromResult(new ProcessResult(cached?0:1,cached?JsonSerializer.Serialize(new[]{new{Id=image,Created="2026-10-04T00:00:00Z",Architecture="amd64",Os="linux"}}):""));
        }
        try
        {
            var updater=new ModelImageUpdater(Run,root);
            check((await updater.Refresh(workload,null))?.Image==image,"Intel Omni startup uses its private build receipt rather than shared tags or console output");
            check(Directory.GetDirectories(root).Length==0,"Intel Omni temporary build context is removed");
            fail=true;cached=true;calls.Clear();
            var offline=await updater.Refresh(workload,null);
            check(offline?.Image==image&&offline.Warning?.Contains("mirror cache miss")==true,"Failed Intel Omni build reuses its compatible local image and reports the failure");
            cached=false;bool denied=false;
            try{await updater.Refresh(workload,null);}catch(InvalidOperationException e){denied=e.Message.Contains("no cached image");}
            check(denied&&!calls.Any(c=>c[0]=="pull"),"A first Intel Omni build failure cannot fall back to Docker Hub or a CUDA engine");
            calls.Clear();var running=new RuntimeInstance(workload.Id,workload.Fingerprint,"container",123,"boot","","running",workload.Gpus);
            check(await updater.Refresh(workload,running)==null&&calls.Count==0,"Running Intel Omni instances are kept intact without rebuilding");
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
