using Xur.Control;
using Xur.Domain;
static class RecipeRepairTests
{
    public static async Task Run(string root,ChildRuntime runtime,IWorkloadGateway gateway,Recipe basis,Action<bool,string> check)
    {
        var old=basis with {Id="old-qwen",Engine="vLLM",Hub=new(ModelLaunchSettings.QwenMtp,new string('a',40),"test"),Command=["serve",ModelLaunchSettings.QwenMtp,"--max-model-len","4096"]};
        var fixedRecipe=ModelLaunchSettings.RepairSaved(old);
        check(fixedRecipe.Command.Contains("--speculative-config") && fixedRecipe.Command.Contains("--max-num-seqs"),"Old Qwen launch settings are automatically repaired");
        check(ModelLaunchSettings.RepairSaved(fixedRecipe)==fixedRecipe && fixedRecipe.Image==old.Image && fixedRecipe.Hub==old.Hub,"Recipe repair is idempotent and retains pinned artifacts");
        var directory=root+"/repair-catalog";Directory.CreateDirectory(directory);
        File.WriteAllText(directory+"/qwen.json",System.Text.Json.JsonSerializer.Serialize(old));
        var catalog=new RecipeCatalog(directory);catalog.Verify(old);catalog.Verify(fixedRecipe);
        var rejected=false;try{catalog.Verify(fixedRecipe with {Image=basis.Image.Replace('a','b')});}catch(InvalidOperationException){rejected=true;}
        check(rejected && catalog.Recipes.Single().Command.SequenceEqual(fixedRecipe.Command),"Catalog exposes repaired defaults without accepting untrusted image changes");
        var workload=new Workload("repair-qwen","Qwen",old,[],"repairqwen");
        using var store=new ProfileStore(root+"/repair-store");
        store.Save(new("repair","Repair",1,[workload]));
        var manager=new ProfileManager(store,runtime,gateway,catalog);
        var preview=await manager.Preview("repair");
        check(preview.Target.Revision==2 && preview.Target.Workloads.Single().Recipe.Command.SequenceEqual(fixedRecipe.Command),"Loading an old saved profile persists its repaired revision before planning");
        check(store.Get<Profile>("revision","repair/1")!.Workloads.Single().Recipe.Command.SequenceEqual(old.Command),"Recipe migration preserves the historical saved revision");
        await manager.Apply(new(preview.Id,preview.Digest));await manager.Wait();
        var unload=await manager.PreviewUnload();await manager.Apply(new(unload.Id,unload.Digest));await manager.Wait();

        // A previous update left a failed Start journal and its real old child.
        var legacy=new Profile("failed-repair","Failed repair",1,[workload]);store.Save(legacy);
        var observed=await runtime.Observe();
        var plan=new ProfilePlan("legacy-plan","",observed.Generation,"empty",legacy,ProfilePolicy.Steps(legacy,observed),DateTimeOffset.UtcNow.AddMinutes(1));
        var before=await runtime.Start(workload);
        store.Put("journal","current",new Journal(plan,observed,0,"Failed","old settings",DateTimeOffset.UtcNow));
        var resumed=new ProfileManager(store,runtime,gateway,catalog);await resumed.Resume();await resumed.Wait();
        var state=await resumed.State();
        check(state.Operation?.Stage=="Complete" && state.Runtime.Instances.Single().Fingerprint!=before.Fingerprint && state.Runtime.Instances.Single().Pid!=before.Pid,"Resume replans and replaces a failed legacy engine with corrected settings");
        check(state.Active?.Workloads.Single().Id==workload.Id && state.Active.Workloads.Single().Recipe.Hub==old.Hub,"Automatic resume repair keeps workload/cache identity and checkpoint revision");
        unload=await resumed.PreviewUnload();await resumed.Apply(new(unload.Id,unload.Digest));await resumed.Wait();
    }
}
