using System.Collections.Concurrent;
using Xur.Control;
using Xur.Domain;
static class AutomaticModelsTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.Combine(".build","automatic-models-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var store=new ProfileStore(root);var runtime=new Runtime();var gateway=new Gateway();
            var manager=new ProfileManager(store,runtime,gateway);
            var recipe=new Recipe("automatic","Automatic","ghcr.io/ggml-org/llama.cpp:server",[],8080,"/health","CPU",0,0,"");
            var a=new Workload("a","A",recipe,[],"a");var b=a with{Id="b",Name="B",Route="b"};
            await manager.Save(new("loaded","Loaded",0,[a,b]));await manager.Save(new("saved","Saved only",0,[a with{Id="saved",Route="saved"}]));
            await manager.RestoreModels();await manager.Wait();
            check(runtime.Starts==0,"Saving a profile alone cannot automatically start its models");
            var plan=await manager.Preview("loaded");await manager.Apply(new(plan.Id,plan.Digest));await manager.Wait();
            await manager.RestoreModels();await manager.Wait();var calls=runtime.StartCalls;
            await manager.RestoreModels();await manager.Wait();
            check(runtime.Starts==2&&runtime.StartCalls==calls,"Automatic recovery adopts running models without restarting them or repeatedly checking them");
            var kept=runtime.Instances[b.Id];var prior=runtime.Instances[a.Id];
            runtime.Instances[a.Id]=prior with{State="exited",Pid=0};
            runtime.Block=new(TaskCreationOptions.RunContinuationsAsynchronously);runtime.Reached=new(TaskCreationOptions.RunContinuationsAsynchronously);
            await manager.RestoreModels();await runtime.Reached.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(gateway.Routes.All(r=>r.WorkloadId!=a.Id)&&gateway.Routes.Any(r=>r.WorkloadId==b.Id),"Recovery withdraws the exited model's stale endpoint while preserving healthy peers");
            await manager.RestoreModels();
            bool busy=false;try{await manager.PreviewUnload();}catch(InvalidOperationException){busy=true;}
            check(busy&&manager.UpdateBusy,"A concurrent unload cannot race a claimed automatic startup");
            runtime.Block.TrySetResult();await manager.Wait();runtime.Block=null;
            check(runtime.Starts==3&&runtime.Instances[b.Id]==kept&&runtime.Instances[a.Id].InstanceId!=prior.InstanceId,"An exited model automatically restarts exactly once while its peer retains its exact instance");
            check(gateway.Routes.Single(r=>r.WorkloadId==a.Id).Endpoint==runtime.Instances[a.Id].Endpoint,"Recovery publishes the restarted model's new port after it becomes ready");
            runtime.Instances.Clear();manager=new ProfileManager(store,runtime,gateway);
            await manager.RestoreModels();await manager.Wait();
            check(runtime.Starts==5&&runtime.Instances.Keys.Order().SequenceEqual(new[]{"a","b"}),"Boot recovery starts only models from the persisted loaded profile without Load or Resume");
            runtime.Instances[a.Id]=runtime.Instances[a.Id] with{State="exited",Pid=0};runtime.Fail=true;
            var now=DateTimeOffset.UtcNow;
            await manager.RestoreModels(now);await manager.Wait();calls=runtime.StartCalls;
            check(gateway.Routes.All(r=>r.WorkloadId!=a.Id),"A model that failed its startup check is never published as ready");
            await manager.RestoreModels(now.AddSeconds(30));await manager.Wait();
            check(runtime.StartCalls==calls,"Automatic startup failures wait before retrying rather than entering a tight restart loop");
            runtime.Fail=false;await manager.RestoreModels(now.AddSeconds(61));await manager.Wait();
            check(gateway.Routes.Any(r=>r.WorkloadId==a.Id),"A failed automatic start retries without requiring a Resume click");
            var journal=store.Get<Journal>("journal","current")!;
            store.Put("journal","current",journal with{Stage="Failed"});runtime.Instances[a.Id]=runtime.Instances[a.Id] with{State="exited",Pid=0};calls=runtime.StartCalls;
            await manager.RestoreModels(now.AddMinutes(3));await manager.Wait();
            check(runtime.StartCalls==calls,"Automatic recovery cannot interfere with a failed manual profile transition");
            store.Put("journal","current",journal);await manager.RestoreModels(now.AddMinutes(3));await manager.Wait();
            runtime.Instances[a.Id]=runtime.Instances[a.Id] with{State="exited",Pid=0};
            gateway.FailNextReady=true;
            await manager.RestoreModels(now.AddMinutes(4));await manager.Wait();var restoredCalls=runtime.StartCalls;
            check(gateway.Routes.All(r=>r.WorkloadId!=a.Id),"A failed gateway publication leaves the restarted model unpublished");
            await manager.RestoreModels(now.AddMinutes(4).AddSeconds(61));await manager.Wait();
            check(gateway.Routes.Any(r=>r.WorkloadId==a.Id)&&runtime.StartCalls==restoredCalls,"Gateway publication retries restore the new route without restarting an already healthy recovered model");
            var unload=await manager.PreviewUnload();await manager.Apply(new(unload.Id,unload.Digest));await manager.Wait();calls=runtime.StartCalls;
            await manager.RestoreModels(now.AddMinutes(5));await manager.Wait();
            check(runtime.StartCalls==calls&&runtime.Instances.Count==0&&gateway.Routes.Length==0,"An intentional profile unload stays unloaded across automatic recovery checks");
            using var stopping=new CancellationTokenSource();var watch=manager.WatchModels(stopping.Token);stopping.Cancel();await watch.WaitAsync(TimeSpan.FromSeconds(2));
            check(watch.IsCompletedSuccessfully,"The automatic model watcher stops promptly when the control service shuts down");
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    sealed class Runtime:IWorkloadRuntime
    {
        public ConcurrentDictionary<string,RuntimeInstance> Instances=new();public int Starts,StartCalls;public bool Fail;
        public TaskCompletionSource? Block,Reached;
        // Real observations deserialize GPU arrays, so they are new objects.
        public Task<RuntimeObservation> Observe()=>Task.FromResult(new RuntimeObservation("generation",[],Instances.Values.Select(i=>i with{Gpus=i.Gpus.ToArray()}).ToArray()));
        public Task Prepare(Workload[] workloads)=>Task.CompletedTask;
        public async Task<RuntimeInstance> Start(Workload w)
        {
            Interlocked.Increment(ref StartCalls);
            if(Instances.TryGetValue(w.Id,out var current)&&current.State=="running")return current;
            if(Block!=null){Reached!.TrySetResult();await Block.Task;}
            if(w.Id=="a"&&Fail)throw new InvalidOperationException("No engine available");
            var number=Interlocked.Increment(ref Starts);
            var instance=new RuntimeInstance(w.Id,w.Fingerprint,w.Id+"-"+number,number,"boot","http://127.0.0.1:"+(20000+number)+"/","running",w.Gpus);
            Instances[w.Id]=instance;return instance;
        }
        public Task Stop(RuntimeStop stop){Instances.TryRemove(stop.Id,out _);return Task.CompletedTask;}
    }
    sealed class Gateway:IWorkloadGateway
    {
        public BackendRoute[] Routes=[];public bool FailNextReady;
        public Task Drain(string id)=>Task.CompletedTask;
        public Task Publish(BackendRoute[] routes)
        {if(FailNextReady&&routes.Any(r=>r.WorkloadId=="a")){FailNextReady=false;throw new IOException("Gateway unavailable");}Routes=routes;return Task.CompletedTask;}
    }
}
