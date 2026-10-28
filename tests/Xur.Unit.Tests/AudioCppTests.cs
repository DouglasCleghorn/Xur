using Xur.Agent;
using Xur.Control;
using Xur.Domain;

static class AudioCppTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var recipes=new RecipeCatalog("catalog").Recipes.Where(r=>r.Engine=="audio.cpp").ToArray();
        check(recipes.Length==4,"audio.cpp offers CPU, NVIDIA, AMD and Intel presets");
        foreach(var recipe in recipes)
        {
            ProfilePolicy.ValidateRecipe(recipe);
            check(recipe.Image==EngineImages.For(recipe)&&recipe.Command[0]=="server"&&recipe.Command.Contains("--ui-management"),"audio.cpp launches upstream model management on "+recipe.Vendor);
            var gpu=new GpuDevice("0000:01:00.0",recipe.Vendor,"GPU","driver","GPU-1",8192,["/dev/dri/renderD128"],[]);
            var workload=new Workload("audio-test","Audio",recipe,recipe.GpuCount==0?[]:[gpu.Pci],"audio");
            var args=ModelGpuArguments.For(workload,[gpu]);
            check(!args.Contains("/dev/kfd")&&!args.Contains("--privileged"),"audio.cpp Vulkan does not require ROCm or privileged access");
            if(recipe.Vendor is "AMD" or "Intel")check(args.Contains(gpu.Nodes[0])&&recipe.Command[^1]=="vulkan","audio.cpp exposes the selected Vulkan render node");
            if(recipe.Vendor=="NVIDIA")check(args.Contains("nvidia.com/gpu=GPU-1")&&recipe.Command[^1]=="cuda","audio.cpp uses NVIDIA CDI and CUDA");
            var calls=new List<string[]>();
            var image=await new ModelImageUpdater((exe,a,seconds)=>{calls.Add(a);return Task.FromResult(new ProcessResult(0,"sha256:"+new string('a',64)));}).Refresh(workload,null);
            check(image!=null&&calls.Single()[^1]==recipe.Image&&calls.Single().Contains("--policy=always"),"audio.cpp resolves its rolling channel at startup");
            check(!T3Models.IsTextModel(workload),"Audio workloads stay out of text-model clients");
        }
        var root=Path.GetFullPath(".build/audio-test-"+Guid.NewGuid().ToString("N"));
        try
        {
            var args=await AudioCppRuntime.Arguments(root,"audio-test",["/dev/dri/renderD128"],(exe,a,seconds)=>Task.FromResult(new ProcessResult(0,"109\n")));
            var path=Path.Combine(root,"audio.cpp","audio-test");File.WriteAllText(Path.Combine(path,"package.gguf"),"fixture");
            check(args.Contains(path+":/app/models:rw,Z,U")&&args.Contains("109"),"Audio model storage is writable by the upstream user and GPU groups are numeric");
            await AudioCppRuntime.Arguments(root,"audio-test",[]);
            check(File.ReadAllText(Path.Combine(path,"package.gguf"))=="fixture","Audio packages survive repeated container preparation");
            var failed=false;try{await AudioCppRuntime.Arguments(root,"audio-test",["/missing"],(exe,a,seconds)=>Task.FromResult(new ProcessResult(1,"")));}catch(InvalidOperationException){failed=true;}
            check(failed,"Missing GPU permissions fail explicitly");
        }
        finally{Directory.Delete(root,true);}
        var html=AudioCppProxy.PrepareHtml("<html><head><script type=\"module\">app()</script></head></html>","token<&", "nonce");
        var embedded="<head><script>const template='<head><script src=example>';</script></head>";
        var prepared=AudioCppProxy.PrepareHtml(embedded,"token","nonce");
        check(prepared.Contains("const template='<head><script src=example>';"),"Audio UI rewriting preserves HTML literals inside bundled JavaScript");
        check(html.Contains("token&lt;&amp;")&&html.Contains("nonce=\"nonce\" type=\"module\"")&&html.IndexOf("/audio-cpp.js",StringComparison.Ordinal)<html.IndexOf("app()",StringComparison.Ordinal),"Audio UI installs its CSRF bridge before upstream scripts and escapes the token");
    }
}
