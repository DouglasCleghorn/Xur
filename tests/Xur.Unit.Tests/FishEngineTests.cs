using System.Text.Json;
using Xur.Agent;
using Xur.Domain;
static class FishEngineTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var state=Path.Combine(".build","xur-fish-"+Guid.NewGuid().ToString("N"));
        try
        {
            bool cached=false,fail=false;var calls=new List<string[]>();
            var image="sha256:"+new string('a',64);
            var upstream="sha256:"+new string('b',64);
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
            check(await engine.Prepare(upstream)==image,"Fish recipes use the prepared runtime from the freshly downloaded upstream image");
            check(calls.Any(a=>a[0]=="build")&&calls.Any(a=>a[0]=="run"&&a.Contains("--network=none")&&a.Contains(image)),"Fresh Fish runtime is built and checked without network or GPU access");
            cached=true;calls.Clear();
            check(await engine.Prepare(upstream)==image&&!calls.Any(a=>a[0]=="build"),"Cached Fish runtime skips rebuilding and uses inspected immutable ID");
            cached=false;calls.Clear();
            await engine.Prepare(upstream);
            var firstTag=calls.Single(a=>a[0]=="build")[Array.IndexOf(calls.Single(a=>a[0]=="build"),"--tag")+1];
            calls.Clear();var updated="sha256:"+new string('c',64);
            await engine.Prepare(updated);
            var build=calls.Single(a=>a[0]=="build");
            var secondTag=build[Array.IndexOf(build,"--tag")+1];
            var file=await File.ReadAllTextAsync(build[Array.IndexOf(build,"--file")+1]);
            check(firstTag!=secondTag&&file.StartsWith("FROM "+updated)&&build.Contains("--pull=never"),"A new upstream Omni image rebuilds Fish's cached layer against that exact downloaded image");
            calls.Clear();bool rejected=false;
            try{await engine.Prepare(FishEngine.BaseImage);}catch(InvalidOperationException){rejected=true;}
            check(rejected&&calls.Count==0,"Fish preparation cannot accidentally build from an unresolved moving tag");
            cached=false;fail=true;calls.Clear();rejected=false;
            try{await engine.Prepare(upstream);}catch(InvalidOperationException e){rejected=e.Message.Contains("dependency build output");}
            check(rejected&&!calls.Any(a=>a[0]=="run"),"Fish build failure remains actionable and does not start an engine");
        }
        finally{if(Directory.Exists(state))Directory.Delete(state,true);}
    }
}
