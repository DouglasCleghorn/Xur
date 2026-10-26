using System.Text.Json;
using Xur.Agent;
using Xur.Control;
using Xur.Domain;

static class PersistedComputeTests
{
    sealed class Clock:TimeProvider
    {
        public DateTimeOffset Now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
    sealed class Runtime:IWorkloadRuntime
    {
        public readonly Dictionary<string,RuntimeInstance> Instances=[];
        public int Starts,Stops;
        public bool Fail;
        public Task<RuntimeObservation> Observe()=>Task.FromResult(new RuntimeObservation("g",[],Instances.Values.ToArray()));
        public Task<RuntimeInstance> Start(Workload workload)
        {
            Starts++;
            if(Fail)throw new InvalidOperationException("Fixture startup failure");
            var instance=new RuntimeInstance(workload.Id,workload.Fingerprint,"instance-"+workload.Id,123,"boot","http://localhost/","running",[]);
            Instances[workload.Id]=instance;return Task.FromResult(instance);
        }
        public Task Stop(RuntimeStop request){Stops++;Instances.Remove(request.Id);return Task.CompletedTask;}
    }
    sealed class Gateway:IWorkloadGateway
    {
        public Task Drain(string id)=>Task.CompletedTask;
        public Task Publish(BackendRoute[] routes)=>Task.CompletedTask;
    }
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(".build/evidence/persisted-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var workload=new Workload("saved-t3","T3 Code",T3CodeTests.Recipe(),[],"coding");
        try
        {
            var runtime=new Runtime();var clock=new Clock();var compute=new PersistedCompute(root,runtime,clock:clock);
            var service=compute.Register("t3-code",workload);
            check(service.Enabled&&service.Workload==workload,"First persisted registration enables auto-run and preserves workload identity and storage");
            await compute.Reconcile();check(runtime.Starts==1&&runtime.Instances.Count==1,"Installing a persisted service starts it without loading a profile");
            await compute.Reconcile();check(runtime.Starts==1,"Reconciliation leaves a healthy persisted instance running");
            compute.SetEnabled("t3-code",false);await compute.Reconcile();
            check(runtime.Stops==1&&runtime.Instances.Count==0,"Disabling persisted compute stops its instance");
            compute=new PersistedCompute(root,runtime,clock:clock);compute.Register("t3-code",workload);await compute.Reconcile();
            check(!compute.Services().Single().Enabled&&runtime.Starts==1,"Disabled intent survives agent restart and rediscovery without restarting");
            compute.SetEnabled("t3-code",true);await compute.Reconcile();
            check(runtime.Starts==2&&Canonical.Hash(compute.Services().Single().Workload.Recipe.Container!)==Canonical.Hash(workload.Recipe.Container!),"Re-enabling uses the same installed recipe and persistent volume");
            runtime.Instances.Clear();compute=new PersistedCompute(root,runtime,clock:clock);await compute.Reconcile();
            check(runtime.Starts==3,"Enabled persisted services return after a simulated host reboot");
            runtime.Instances.Clear();runtime.Fail=true;
            for(int attempt=1;attempt<=5;attempt++)
            {
                await compute.Reconcile();check(compute.Services().Single().Failures==attempt,"Failed persisted starts consume a durable bounded retry attempt");
                var attempts=runtime.Starts;await compute.Reconcile();check(runtime.Starts==attempts,"Backoff prevents immediate retries");
                clock.Now+=TimeSpan.FromMinutes(2);
                compute=new PersistedCompute(root,runtime,clock:clock);
            }
            var exhausted=runtime.Starts;await compute.Reconcile();
            check(runtime.Starts==exhausted&&compute.Services().Single().Error=="Fixture startup failure","Five failed starts remain failed across restarts until explicit retry");
            runtime.Fail=false;compute.SetEnabled("t3-code",true);await compute.Reconcile();
            check(runtime.Starts==exhausted+1&&compute.Services().Single().Failures==0,"Explicit retry clears failed state and starts the saved service");
            runtime.Instances.Clear();runtime.Fail=true;await compute.Reconcile();
            compute.SetEnabled("t3-code",false);clock.Now+=TimeSpan.FromHours(1);var before=runtime.Starts;await compute.Reconcile();
            check(runtime.Starts==before&&!compute.Services().Single().Enabled,"Off cancels scheduled recovery");
            compute.Retry("t3-code");await compute.Reconcile();
            check(!compute.Services().Single().Enabled&&runtime.Starts==before,"Refreshing persisted settings preserves disabled intent");

            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            compute=new PersistedCompute(root,runtime,async(_,token)=>{
                entered.SetResult();try{await Task.Delay(Timeout.Infinite,token);}catch(OperationCanceledException){cancelled.SetResult();throw;}
                throw new Exception("Unreachable");
            },clock);
            compute.SetEnabled("t3-code",true);var pending=compute.Reconcile();await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            compute.SetEnabled("t3-code",false);await pending.WaitAsync(TimeSpan.FromSeconds(5));
            check(cancelled.Task.IsCompleted&&!compute.Services().Single().Enabled&&compute.Services().Single().Failures==0,"Off cancels an in-flight startup without stale completion overwriting intent");

            var sharedRuntime=new Runtime();var shared=new PersistedCompute(root+"/shared",sharedRuntime);
            shared.Register("first",workload);shared.Register("second",workload with{Id="another-container",Recipe=workload.Recipe with{Engine="Podman"}});
            await shared.Reconcile();shared.SetEnabled("first",false);await shared.Reconcile();
            check(sharedRuntime.Instances.Keys.SequenceEqual(["another-container"]),"The common lifecycle manages independent container services");

            Directory.CreateDirectory(root+"/catalog");Directory.CreateDirectory(root+"/receipts");
            await File.WriteAllTextAsync(root+"/catalog/t3.json",JsonSerializer.Serialize(workload.Recipe));
            await File.WriteAllTextAsync(root+"/receipts/saved-t3.json",JsonSerializer.Serialize(workload));
            var catalog=new RecipeCatalog(root+"/catalog");var actual=new WorkloadRuntime(root+"/receipts",catalog){IndependentT3=true};
            var adopted=new PersistedCompute(root+"/adopted",new Runtime());var t3=new T3PersistedService(adopted,catalog,actual,new ContainerLibrary(root+"/adopted"));
            t3.Discover();check(Canonical.Hash(adopted.Services().Single().Workload)==Canonical.Hash(workload),"T3 migration adopts the existing receipt without a duplicate identity or fresh volume");
            adopted.SetEnabled("t3-code",false);t3.Discover();check(!adopted.Services().Single().Enabled,"Discovery never re-enables a disabled T3 installation");
            await actual.Stop(new(workload.Id,"stale-profile-instance"));
            check(File.Exists(root+"/receipts/saved-t3.json"),"An old profile stop cannot remove an independently managed T3 receipt");
            bool rejected=false;try{await actual.Start(workload);}catch(InvalidOperationException){rejected=true;}
            check(rejected,"A profile cannot start an independently managed T3 server");

            var fresh=new PersistedCompute(root+"/fresh",new Runtime());
            var freshRuntime=new WorkloadRuntime(root+"/empty-receipts",catalog){IndependentT3=true};
            var freshT3=new T3PersistedService(fresh,catalog,freshRuntime,new ContainerLibrary(root+"/fresh"));
            freshT3.Discover();
            check(fresh.Services().Single() is {Enabled:true,Workload.Id:"persisted-t3-code"},"A newly prepared T3 installation registers auto-run without a browser or profile");
            var alternate=workload.Recipe with{Id="container-other",Container=workload.Recipe.Container! with{Mounts=[new("xur-volume-"+new string('c',32),T3CodeWorkload.Home)]}};
            await File.WriteAllTextAsync(root+"/catalog/other.json",JsonSerializer.Serialize(alternate));
            var ambiguous=new PersistedCompute(root+"/ambiguous",new Runtime());
            var ambiguousT3=new T3PersistedService(ambiguous,catalog,freshRuntime,new ContainerLibrary(root+"/ambiguous"));
            ambiguousT3.Discover();
            check(ambiguous.Services().Length==0&&ambiguousT3.Error!=null,"Multiple unrelated legacy T3 volumes require selection instead of silently creating or choosing an environment");

            using var store=new ProfileStore(root+"/profiles");store.Save(new Profile("coding","Coding",1,[workload]));
            var profiles=new ProfileManager(store,new Runtime(),new Gateway(),catalog);
            var state=await profiles.State();var plan=await profiles.Preview("coding");
            check(state.Profiles.Single().Workloads.Length==0&&plan.Target.Workloads.Length==0&&!plan.Steps.Any(s=>s.WorkloadId==workload.Id),"Legacy T3 profile references no longer participate in profile plans");
            check(store.List<Profile>("revision").Any(p=>p.Workloads.Any(w=>w.Id==workload.Id)),"Profile migration retains the original T3 reference in revision history");
            var selected=await profiles.Save(new Profile("new-profile","New profile",0,[workload]));
            check(selected.Workloads.Length==0,"Saving a profile cannot restore T3 lifecycle ownership");

            await File.WriteAllTextAsync(root+"/persisted-compute.json","corrupt");
            rejected=false;try{_ =new PersistedCompute(root,runtime);}catch(JsonException){rejected=true;}
            check(rejected&&await File.ReadAllTextAsync(root+"/persisted-compute.json")=="corrupt","Corrupt lifecycle state is retained and never silently reset to enabled");
        }
        finally{Directory.Delete(root,true);}
    }
}
