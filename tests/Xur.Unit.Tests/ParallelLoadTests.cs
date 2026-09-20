using Xur.Control;
using Xur.Domain;
static class ParallelLoadTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"xur-parallel-load-"+Guid.NewGuid());
        try
        {
            using var store=new ProfileStore(root);var runtime=new Runtime();var gateway=new Gateway();var manager=new ProfileManager(store,runtime,gateway);
            var recipe=new Recipe("test","Test","example.invalid/engine@sha256:"+new string('a',64),[],8080,"/health","CPU",0,0,"");
            Workload W(string id)=>new(id,id,recipe,[],id);
            await manager.Save(new("p","Parallel",0,[W("good"),W("bad")]));
            var plan=await manager.Preview("p");await manager.Apply(new(plan.Id,plan.Digest));
            await runtime.Both.Task.WaitAsync(TimeSpan.FromSeconds(3));
            check(runtime.MaxConcurrent==2,"Independent workload health checks overlap");
            runtime.Release.TrySetResult();await manager.Wait();
            var state=await manager.State();
            check(state.Active==null&&state.Operation?.Stage=="Failed"&&state.Operation.Error!.Contains("bad")&&gateway.Routes.Any(r=>r.WorkloadId=="good"),"A failed start is reported while the successful workload route remains available");
            check(store.Get<Journal>("journal","current")!.StepErrors!.Count==1,"Startup errors are retained per action");
            runtime.Fail=false;await manager.Resume();await manager.Wait();
            check((await manager.State()).Operation?.Stage=="Complete"&&runtime.Attempts["good"]==1&&runtime.Attempts["bad"]==2,"Resume retries only the failed workload and preserves a successful instance");
            check(gateway.Routes.Length==2,"Recovery publishes both healthy routes");
            // A failed release blocks reuse of its GPU but not a separate CPU job.
            var gpu=new GpuDevice("0000:01:00.0","NVIDIA","GPU","nvidia","",20000,[],[]);
            runtime.Gpus=[gpu];runtime.Instances.Clear();runtime.Instances["old"]=new("old","old-fingerprint","old-instance",10,"boot","http://old/","running",[gpu.Pci]);runtime.FailStop=true;
            var gpuRecipe=recipe with{Vendor="NVIDIA",GpuCount=1};
            await manager.Save(new("q","Handoff",0,[new("replacement","replacement",gpuRecipe,[gpu.Pci],"replacement"),W("other")]));
            plan=await manager.Preview("q");await manager.Apply(new(plan.Id,plan.Digest));await manager.Wait();
            check(!runtime.Attempts.ContainsKey("replacement")&&runtime.Attempts.ContainsKey("other"),"Failed GPU release blocks its dependent start while an independent workload starts");
            check((await manager.State()).Operation?.Stage=="Failed"&&gateway.Routes.Any(r=>r.WorkloadId=="other"),"Independent success remains published after a handoff failure");
            await manager.Cancel();runtime.Instances.Clear();runtime.Gpus=[];runtime.FailStop=false;
            runtime.Both=new(TaskCreationOptions.RunContinuationsAsynchronously);runtime.Release=new(TaskCreationOptions.RunContinuationsAsynchronously);runtime.ReachCount=4;
            await manager.Save(new("bounded","Bounded",0,Enumerable.Range(0,7).Select(n=>W("queued"+n)).ToArray()));
            plan=await manager.Preview("bounded");await manager.Apply(new(plan.Id,plan.Digest));await runtime.Both.Task.WaitAsync(TimeSpan.FromSeconds(3));
            check(runtime.Attempts.Keys.Count(k=>k.StartsWith("queued"))==4,"Profile startup limits concurrent pipelines to four");
            await manager.Cancel(plan.Id).WaitAsync(TimeSpan.FromSeconds(2));runtime.Release.TrySetResult();await manager.Wait();
            check((await manager.State()).Operation?.Stage=="Cancelled"&&runtime.Attempts.Keys.Count(k=>k.StartsWith("queued"))==4,"Cancellation preserves in-flight starts without dispatching queued workloads");
        }finally{Directory.Delete(root,true);}
        await Stations(check);
    }
    static async Task Stations(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"xur-parallel-stations-"+Guid.NewGuid());
        try
        {
            using var store=new ProfileStore(root);var runtime=new Runtime{Fail=false};runtime.Release.TrySetResult();
            runtime.Gpus=Enumerable.Range(1,2).Select(n=>new GpuDevice($"0000:0{n}:00.0","NVIDIA","GPU","nvidia","",24576,[],[],[$"/dev/dri/card{n}"])).ToArray();
            var manager=new ProfileManager(store,runtime,new Gateway());
            var recipe=new Recipe("gaming-workstation","Desktop","host:plasma",[],0,"","Display",1,0,"",Kind:"Workstation",Engine:"Plasma");
            var a=new Workload("desk-a","Desk A",recipe,[runtime.Gpus[0].Pci],"desk-a",new("alex",1001),new(true));
            var b=new Workload("desk-b","Desk B",recipe,[runtime.Gpus[1].Pci],"desk-b",new("sam",1002));
            await manager.Save(new("two","Two desks",0,[a,b]));
            var plan=await manager.Preview("two");await manager.Apply(new(plan.Id,plan.Digest));await manager.Wait();
            check((await manager.State()).Operation?.Stage=="Complete"&&runtime.Instances.Count==2,"A profile accepts and starts two distinct workstation users and GPUs");
            await manager.Save(new("one","One desk",0,[a]));plan=await manager.Preview("one");
            check(plan.Steps.Any(s=>s.WorkloadId==a.Id&&s.Kind=="Keep")&&!plan.Steps.Any(s=>s.WorkloadId==a.Id&&s.Kind=="Stop"),"Removing a secondary desk without transferring peripherals preserves the primary desktop");
            var transfer=await manager.Save(new("transfer","Move hub",0,[a with{Devices=new(false)},b with{Devices=new(true)}]));
            plan=await manager.Preview(transfer.Id);runtime.FailStop=true;var preparations=runtime.Preparations;
            await manager.Apply(new(plan.Id,plan.Digest));await manager.Wait();
            check(runtime.Preparations==preparations&&(await manager.State()).Operation?.Stage=="Failed","Failed seat teardown cannot replace peripheral intent or start a conflicting desktop");
        }finally{Directory.Delete(root,true);}
    }
    sealed class Runtime:IWorkloadRuntime
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string,RuntimeInstance> Instances=new();
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string,int> Attempts=new();
        public GpuDevice[] Gpus=[];public bool Fail=true,FailStop;public int ReachCount=2,Preparations;int concurrent;public int MaxConcurrent;
        public TaskCompletionSource Both=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<RuntimeObservation> Observe()=>Task.FromResult(new RuntimeObservation("generation",Gpus,Instances.Values.OrderBy(i=>i.Id).ToArray()));
        public async Task<RuntimeInstance> Start(Workload w)
        {
            Attempts.AddOrUpdate(w.Id,1,(_,n)=>n+1);var active=Interlocked.Increment(ref concurrent);MaxConcurrent=Math.Max(MaxConcurrent,active);if(active==ReachCount)Both.TrySetResult();
            try{await Release.Task;if(w.Id=="bad"&&Fail)throw new InvalidOperationException("Deliberate startup failure");var i=new RuntimeInstance(w.Id,w.Fingerprint,w.Id+"-instance",100,"boot",w.Recipe.Kind=="Workstation"?"":"http://"+w.Id+"/","running",w.Gpus);Instances[w.Id]=i;return i;}finally{Interlocked.Decrement(ref concurrent);}
        }
        public Task Prepare(Workload[] workloads){Preparations++;return Task.CompletedTask;}
        public Task Stop(RuntimeStop stop){if(FailStop)throw new InvalidOperationException("Device still busy");Instances.TryRemove(stop.Id,out _);return Task.CompletedTask;}
    }
    sealed class Gateway:IWorkloadGateway
    {public BackendRoute[] Routes=[];public Task Drain(string id)=>Task.CompletedTask;public Task Publish(BackendRoute[] routes){Routes=routes;return Task.CompletedTask;}}
}
