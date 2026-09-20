using System.Collections.Concurrent;
using Xur.Control;
using Xur.Domain;
static class ParallelUnloadTests
{
    static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static async Task Run(string root,ChildRuntime real,IWorkloadGateway gateway,HttpClient data,Recipe recipe,Action<bool,string> check)
    {
        using var store=new ProfileStore(root+"/parallel-unload");
        var runtime=new Runtime(real);var manager=new ProfileManager(store,runtime,gateway);
        var workloads=Enumerable.Range(0,6).Select(i=>new Workload("parallel-"+i,"Parallel "+i,recipe,[],"parallel-"+i)).ToArray();
        await manager.Save(new("parallel","Parallel",0,workloads));
        async Task Load(){var p=await manager.Preview("parallel");await manager.Apply(new(p.Id,p.Digest));await manager.Wait();}
        async Task Unload(){var p=await manager.PreviewUnload();await manager.Apply(new(p.Id,p.Digest));}
        await Load();
        using(var response=await data.GetAsync("/parallel-0/stream",HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            runtime.Stopped=Signal();runtime.Notify="parallel-5";await Unload();
            await runtime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Parallel-5 may finish before any earlier sibling. Wait for all
            // five stop receipts rather than treating its signal as a barrier.
            await Until(async()=>(await manager.State()).Operation!.Completed==10);
            check((await real.Observe()).Instances.Select(i=>i.Id).SequenceEqual(["parallel-0"]),"A real active stream drains independently while later workloads stop");
            var state=await manager.State();
            check(state.Operation!.Completed==10 && state.Operation.CompletedActions!.All(a=>a.WorkloadId!="parallel-0"),"Progress reports exact out-of-order actions rather than a completed prefix");
        }
        await manager.Wait();check((await manager.State()).Operation?.Stage=="Complete","Parallel unload completes after the final active stream drains");

        await Load();runtime.Fail="parallel-0";runtime.Calls.Clear();await Unload();await manager.Wait();
        var failed=await manager.State();
        check(failed.Operation is {Stage:"Failed",Completed:11} && failed.Runtime.Instances.Single().Id=="parallel-0","A failed stop preserves that instance and does not prevent five independent stops");
        check(failed.Operation!.Error!.Contains("parallel-0 (Stop)"),"Parallel failures identify their workload and action");
        var journal=store.Get<Journal>("journal","current")!;
        check(journal.Completed==1 && journal.Done().Length==11,"SQLite stores exact unload receipts while retaining a compatible contiguous prefix");
        manager=new ProfileManager(store,runtime,gateway);runtime.Fail=null;
        await manager.Resume();await manager.Wait();
        check(runtime.Calls["parallel-0"]==2 && workloads.Skip(1).All(w=>runtime.Calls[w.Id]==1),"A new manager resumes only the failed stop, without repeating successful sibling stops");
        check((await manager.State()) is {Active:null,Operation.Stage:"Complete"},"Recovered parallel unload clears the active profile");

        await Load();runtime.Calls.Clear();runtime.Hold=true;runtime.Four=Signal();runtime.Release=Signal();
        await Unload();await runtime.Four.Task.WaitAsync(TimeSpan.FromSeconds(10));
        check(runtime.Calls.Count==4,"Unload bounds simultaneous workload pipelines to four");
        await manager.Cancel();runtime.Release.SetResult();await manager.Wait();runtime.Hold=false;
        check(runtime.Calls.Count==4 && (await manager.State()).Operation?.Stage=="Cancelled","Cancel waits for in-flight stops and prevents queued pipelines from starting");
        check((await real.Observe()).Instances.Length==2,"Cancellation leaves unclaimed workloads running");
        await Unload();await manager.Wait();

        await Load();
        var blocked=new Gateway(gateway);manager=new ProfileManager(store,runtime,blocked);
        await Unload();await blocked.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Until(async()=>(await real.Observe()).Instances.Length==1);
        await manager.Cancel();blocked.Release.SetResult();await manager.Wait();
        check((await real.Observe()).Instances.Single().Id=="parallel-0","Cancellation during a drain never dispatches its subsequent stop");
        manager=new ProfileManager(store,runtime,gateway);await Unload();await manager.Wait();
    }
    static async Task Until(Func<Task<bool>> predicate)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(!await predicate())await Task.Delay(10,deadline.Token);
    }
    sealed class Runtime(ChildRuntime inner):IWorkloadRuntime
    {
        public ConcurrentDictionary<string,int> Calls=new();
        public string? Fail,Notify;
        public bool Hold;
        public TaskCompletionSource Stopped=Signal(),Four=Signal(),Release=Signal();
        public Task<RuntimeObservation> Observe()=>inner.Observe();
        public Task<RuntimeInstance> Start(Workload w)=>inner.Start(w);
        public async Task Stop(RuntimeStop stop)
        {
            Calls.AddOrUpdate(stop.Id,1,(_,n)=>n+1);
            if(Hold){if(Calls.Count==4)Four.TrySetResult();await Release.Task;}
            if(stop.Id==Fail)throw new InvalidOperationException("Injected release failure");
            await inner.Stop(stop);if(stop.Id==Notify)Stopped.TrySetResult();
        }
    }
    sealed class Gateway(IWorkloadGateway inner):IWorkloadGateway
    {
        public TaskCompletionSource Reached=Signal(),Release=Signal();
        public async Task Drain(string id){await inner.Drain(id);if(id=="parallel-0"){Reached.TrySetResult();await Release.Task;}}
        public Task Publish(BackendRoute[] routes)=>inner.Publish(routes);
    }
}
