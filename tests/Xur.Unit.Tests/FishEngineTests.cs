using System.Text.Json;
using Xur.Agent;
using Xur.Domain;
static class FishEngineTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var state=Path.Combine(Path.GetTempPath(),"xur-fish-"+Guid.NewGuid().ToString("N"));
        try
        {
            bool cached=false,fail=false;var calls=new List<string[]>();
            var image="sha256:"+new string('a',64);
            Task<ProcessResult> Run(string exe,string[] args,int seconds)
            {
                calls.Add(args);
                return Task.FromResult(args[0] switch {
                    "image" when args[1]=="exists"=>new ProcessResult(cached?0:1,""),
                    "image"=>new ProcessResult(0,JsonSerializer.Serialize(new[]{new{Id=image}})),
                    "build"=>new ProcessResult(fail?1:0,"dependency build output"),
                    "run"=>new ProcessResult(0,""),_=>throw new Exception("Unexpected Fish command")});
            }
            var engine=new FishEngine(state,Run);
            check(await engine.Prepare(FishEngine.BaseImage.Replace("mirror.gcr.io/","docker.io/"))==image,"Legacy saved Fish recipes use the same pinned derived runtime");
            check(calls.Any(a=>a[0]=="build")&&calls.Any(a=>a[0]=="run"&&a.Contains("--network=none")&&a.Contains(image)),"Fresh Fish runtime is built and checked without network or GPU access");
            cached=true;calls.Clear();
            check(await engine.Prepare(FishEngine.BaseImage)==image&&!calls.Any(a=>a[0]=="build"),"Cached Fish runtime skips rebuilding and uses inspected immutable ID");
            calls.Clear();bool rejected=false;
            try{await engine.Prepare("mirror.gcr.io/vllm/vllm-omni@sha256:"+new string('b',64));}catch(InvalidOperationException){rejected=true;}
            check(rejected&&calls.Count==0,"Fish dependency layer never silently follows an untested base image");
            cached=false;fail=true;calls.Clear();rejected=false;
            try{await engine.Prepare(FishEngine.BaseImage);}catch(InvalidOperationException e){rejected=e.Message.Contains("dependency build output");}
            check(rejected&&!calls.Any(a=>a[0]=="run"),"Fish build failure remains actionable and does not start an engine");
        }
        finally{if(Directory.Exists(state))Directory.Delete(state,true);}
    }
}
