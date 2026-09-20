using System.Text.Json;
using Xur.Control;
using Xur.Domain;
static class StationIdentityTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"xur-station-identity-"+Guid.NewGuid());Directory.CreateDirectory(root);
        var recipe=new Recipe("gaming-workstation","Desktop","host:plasma",[],0,"","Display",1,0,"",Kind:"Workstation",Engine:"Plasma");
        Directory.CreateDirectory(root+"/catalog");File.WriteAllText(root+"/catalog/desktop.json",JsonSerializer.Serialize(recipe));
        var catalog=new RecipeCatalog(root+"/catalog");var runtime=new Runtime();var gateway=new Gateway();
        var user=new StationUser("doug",1000);string identity;
        try {
            using(var store=new ProfileStore(root+"/state")) {
                var manager=new ProfileManager(store,runtime,gateway,catalog);var first=await manager.Create();
                first=await manager.SaveSelection(first.Id,first.Revision,[new(null,recipe.Id,["0000:01:00.0"],user,"","Gaming desktop")]);
                identity=first.Workloads[0].Id;
                var second=await manager.Create();second=await manager.SaveSelection(second.Id,second.Revision,[new(null,recipe.Id,["0000:02:00.0"],null,identity,"Gaming desktop")]);
                check(first.Workloads[0].Id==second.Workloads[0].Id && first.Workloads[0].Fingerprint!=second.Workloads[0].Fingerprint,"Changing GPUs across profiles preserves station pairing identity but restarts the desktop");
                check(second.Workloads[0].User==user,"Reusing a station retains its workstation account");
                var renamed=await manager.SaveSelection(second.Id,second.Revision,[new(identity,recipe.Id,["0000:02:00.0"],null,identity,"Doug’s desktop")]);
                check((await manager.State()).Profiles.All(p=>p.Workloads[0].Name=="Doug’s desktop"),"Station name is shared across profiles");
                check((await manager.Preview(first.Id)).Target.Workloads[0].Name=="Doug’s desktop","Launch preview resolves the latest workstation name");
                var view=WorkstationView.Build(await manager.State(),[]);
                check(view.Length==1 && view[0].Profiles.Length==2,"Workstations lists one reusable identity with both GPU profiles");
                var third=await manager.Create();third=await manager.SaveSelection(third.Id,third.Revision,[new(null,recipe.Id,["0000:01:00.0"],user,"","Separate desktop")]);
                check(third.Workloads[0].Id!=identity,"An explicitly new workstation never inherits another pairing");
                bool refused=false;try{await manager.Save(renamed with {Workloads=[renamed.Workloads[0] with {User=new("someone",1001)}]});}catch(InvalidOperationException){refused=true;}
                check(refused && store.Get<Profile>("profile",second.Id)!.Workloads[0].User==user,"Changing user behind a paired identity is rejected transactionally");
                await manager.Delete(first.Id,first.Revision);await manager.Delete(second.Id,renamed.Revision);
                check((await manager.Stations()).Any(s=>s.Id==identity),"Deleting all referencing profiles retains the reusable workstation identity");
                // Simulate a pre-identity database; migration must retain the exact old runtime key.
                store.Remove("station",third.Workloads[0].Id);
            }
            using(var store=new ProfileStore(root+"/state")) {
                var manager=new ProfileManager(store,runtime,gateway,catalog);var station=(await manager.Stations()).Single(s=>s.Name=="Separate desktop");
                check(station.Id==store.List<Profile>("profile").Single().Workloads[0].Id,"Existing workstation keys migrate without changing Moonlight identity");
                check((await manager.Stations()).Any(s=>s.Id==identity),"Unreferenced named workstations survive reopening the database");
            }
        } finally {Directory.Delete(root,true);}
    }
    sealed class Runtime:IWorkloadRuntime {
        public Task<RuntimeObservation> Observe()=>Task.FromResult(new RuntimeObservation("fixed",new[]{"0000:01:00.0","0000:02:00.0"}.Select(p=>new GpuDevice(p,"NVIDIA","RTX 3090","nvidia","",24576,[],[],["/dev/dri/card0"],[])).ToArray(),[]));
        public Task<RuntimeInstance> Start(Workload w)=>throw new NotSupportedException();public Task Stop(RuntimeStop r)=>throw new NotSupportedException();
    }
    sealed class Gateway:IWorkloadGateway {public Task Drain(string id)=>Task.CompletedTask;public Task Publish(BackendRoute[] routes)=>Task.CompletedTask;}
}
