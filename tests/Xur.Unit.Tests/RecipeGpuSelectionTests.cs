using System.Text.Json;
using Xur.Agent;
using Xur.Control;
using Xur.Domain;

static class RecipeGpuSelectionTests
{
    public static Recipe Llama()=>new("llama-fixture","GGUF fixture",EngineImages.For("llama.cpp","CPU"),
        ["--model","/model/model.gguf","--ctx-size","4096"],8080,"/health","CPU",0,0,"Fixture",
        new("https://example.test/model.gguf",new string('a',64),20L<<30,"MIT","fixture",new string('b',40)));
    public static GpuDevice[] Hardware()=>Enumerable.Range(1,5).Select(i=>new GpuDevice($"0000:0{i}:00.0",i==5?"AMD":"NVIDIA","GPU "+i,
        i==5?"amdgpu":"nvidia","GPU-"+i,16384,["/dev/dri/renderD"+(127+i)],[],["/dev/dri/card"+(i-1)],[],ShortId:"GPU "+i)).ToArray();
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(".build/profile-gpus-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root+"/catalog");
        var llama=Llama();var unsloth=UnslothWorkload.Recipe;var hardware=Hardware();
        foreach(var recipe in new[]{llama,unsloth})File.WriteAllText(root+"/catalog/"+recipe.Id+".json",JsonSerializer.Serialize(recipe));
        var catalog=new RecipeCatalog(root+"/catalog");
        bool Reject(Action action){try{action();return false;}catch(InvalidOperationException){return true;}}
        try
        {
            var split=RecipeGpuSelection.Select(llama,[hardware[1].Pci,hardware[0].Pci],hardware);
            catalog.Verify(split);ProfilePolicy.ValidateRecipe(split);
            check(split.Vendor=="NVIDIA"&&split.GpuCount==2&&split.MemoryMiB==11264&&split.Image==EngineImages.For("llama.cpp","NVIDIA"),"Two GPUs select the CUDA engine and divide the model memory estimate");
            check(split.Command.Contains("--split-mode")&&split.Command[^1]=="1,1"&&split.Command.Contains("999")&&split.Model==llama.Model&&split.Command.Contains("4096"),"llama.cpp splits layers across both GPUs while retaining model and context settings");
            check(Canonical.Hash(RecipeGpuSelection.Apply(llama,"CPU",0))==Canonical.Hash(llama),"Unchanged allocations preserve existing recipe fingerprints");
            var cpu=RecipeGpuSelection.Apply(split,"CPU",0);
            check(cpu.Image==llama.Image&&cpu.MemoryMiB==0&&cpu.Command[^1]=="0"&&!cpu.Command.Contains("--tensor-split"),"Removing all llama.cpp GPUs selects the CPU engine and clears splitting flags");
            foreach(var vendor in new[]{"AMD","Intel"})
            {
                var selected=RecipeGpuSelection.Apply(llama,vendor,2);catalog.Verify(selected);
                check(selected.Image==EngineImages.For("llama.cpp",vendor)&&selected.GpuCount==2,"llama.cpp selects the correct engine for "+vendor+" GPUs");
            }
            var training=RecipeGpuSelection.Apply(unsloth,"NVIDIA",2);catalog.Verify(training);UnslothWorkload.Validate(training);
            check(Canonical.Hash(training.Container!)==Canonical.Hash(unsloth.Container!)&&training.Image==UnslothWorkload.Image,"Multiple Unsloth GPUs preserve latest image and persistent storage");
            foreach(var recipe in new[]{training,split})
            {
                var workload=new Workload("fixture","Fixture",recipe,[hardware[1].Pci,hardware[0].Pci],"fixture");
                ProfilePolicy.Validate(new("fixture","Fixture",1,[workload]),new("test",hardware,[]));
                var args=ModelGpuArguments.For(workload,hardware);
                check(args.Contains("nvidia.com/gpu=GPU-2")&&args.Contains("nvidia.com/gpu=GPU-1")&&args.Contains("CUDA_VISIBLE_DEVICES=GPU-2,GPU-1")&&!args.Contains("nvidia.com/gpu=GPU-3"),recipe.Engine+" exposes only the selected GPUs in selection order");
                check(Reject(()=>catalog.Verify(recipe with{Command=["arbitrary"]}))&&Reject(()=>catalog.Verify(recipe with{Image="mirror.gcr.io/library/alpine:latest"})),recipe.Engine+" GPU variants cannot replace trusted commands or images");
            }
            check(Reject(()=>RecipeGpuSelection.Apply(unsloth,"CPU",0))&&Reject(()=>RecipeGpuSelection.Apply(unsloth,"AMD",1))&&Reject(()=>RecipeGpuSelection.Apply(unsloth,"NVIDIA",17)),"Unsloth requires one to 16 NVIDIA GPUs");
            check(Reject(()=>RecipeGpuSelection.Select(llama,[hardware[0].Pci,hardware[4].Pci],hardware))&&Reject(()=>RecipeGpuSelection.Select(llama,["missing"],hardware)),"Mixed vendors and missing GPUs are rejected");
            check(Reject(()=>catalog.Verify(split with{Model=llama.Model! with{Sha256=new string('c',64)}})),"GPU allocation changes cannot replace model assets");
            var runtime=new Runtime(hardware);string profileId;
            using(var store=new ProfileStore(root+"/state"))
            {
                var manager=new ProfileManager(store,runtime,new Gateway(),catalog);var profile=await manager.Create();profileId=profile.Id;
                profile=await manager.SaveSelection(profile.Id,profile.Revision,[new(null,unsloth.Id,[hardware[1].Pci,hardware[0].Pci],null),new(null,llama.Id,[hardware[3].Pci,hardware[2].Pci],null)]);
                check(profile.Workloads.All(w=>w.Recipe.GpuCount==2),"Profile editor saves multi-GPU Unsloth and llama.cpp together");
                check((await manager.Preview(profile.Id)).Target.Workloads.All(w=>w.Gpus.Length==2),"Load preview retains the complete GPU allocations");
                var rejected=false;
                try{await manager.SaveSelection(profile.Id,profile.Revision,[new(profile.Workloads[0].Id,unsloth.Id,[hardware[0].Pci,hardware[0].Pci],null)]);}catch(InvalidOperationException){rejected=true;}
                check(rejected&&store.Get<Profile>("profile",profile.Id)!.Revision==profile.Revision,"Duplicate GPU assignments fail without changing the saved profile");
                rejected=false;
                try{await manager.SaveSelection(profile.Id,profile.Revision,[new(profile.Workloads[1].Id,llama.Id,[hardware[0].Pci],null)]);}catch(InvalidOperationException){rejected=true;}
                check(rejected,"A model too large for one GPU still fails the capacity check");
            }
            using(var store=new ProfileStore(root+"/state"))
            {
                var manager=new ProfileManager(store,runtime,new Gateway(),catalog);var saved=store.Get<Profile>("profile",profileId)!;
                check(saved.Workloads[0].Gpus.SequenceEqual([hardware[1].Pci,hardware[0].Pci])&&saved.Workloads[1].Gpus.SequenceEqual([hardware[3].Pci,hardware[2].Pci]),"Reopening storage preserves GPU counts and ordering for both workloads");
                foreach(var workload in saved.Workloads)catalog.Verify(workload.Recipe);
                var reduced=await manager.SaveSelection(saved.Id,saved.Revision,[new(saved.Workloads[0].Id,unsloth.Id,[hardware[1].Pci],null),new(saved.Workloads[1].Id,llama.Id,[],null)]);
                check(reduced.Workloads[0].Recipe.GpuCount==1&&reduced.Workloads[1].Recipe.Vendor=="CPU"&&reduced.Workloads[1].Gpus.Length==0,"Remove buttons can save one Unsloth GPU and CPU llama.cpp");
            }
        }
        finally{Directory.Delete(root,true);}
    }
    sealed class Runtime(GpuDevice[] hardware):IWorkloadRuntime
    {
        public Task<RuntimeObservation> Observe()=>Task.FromResult(new RuntimeObservation("test",hardware,[]));
        public Task<RuntimeInstance> Start(Workload w)=>throw new NotSupportedException();
        public Task Stop(RuntimeStop r)=>throw new NotSupportedException();
    }
    sealed class Gateway:IWorkloadGateway
    {
        public Task Drain(string id)=>Task.CompletedTask;
        public Task Publish(BackendRoute[] routes)=>Task.CompletedTask;
    }
}
