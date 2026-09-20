using Xur.Domain;
namespace Xur.Control;
public sealed partial class ProfileManager
{
    async Task RunLoad()
    {
        Journal initial;
        await gate.WaitAsync();try{initial=store.Get<Journal>("journal","current")!;}finally{gate.Release();}
        using var slots=new SemaphoreSlim(4,4);
        using var publication=new SemaphoreSlim(1,1);
        var steps=initial.Plan.Steps;
        var stops=steps.Select((s,i)=>(s,i)).Where(x=>x.s.Kind is "Drain" or "Stop").GroupBy(x=>x.s.WorkloadId)
            .ToDictionary(g=>g.Key,g=>StopPipeline(g.ToArray()));
        async Task StopPipeline((TransitionStep s,int i)[] pipeline)
        {
            await slots.WaitAsync();try{foreach(var (_,i) in pipeline)if(!await LoadAction(initial,i))break;}finally{slots.Release();}
        }
        async Task PublishReady()
        {
            await publication.WaitAsync();try
            {
                Journal current;await gate.WaitAsync();try{current=store.Get<Journal>("journal","current")!;}finally{gate.Release();}
                var done=current.Done().ToHashSet();var observed=await runtime.Observe();
                bool Ready(Workload w)
                {
                    if(!observed.Instances.Any(i=>i.Id==w.Id&&i.Fingerprint==w.Fingerprint&&i.State=="running"))return false;
                    var action=Array.FindIndex(steps,s=>s.WorkloadId==w.Id&&s.Kind is "Start" or "Keep");
                    if(action<0)return false;
                    if(steps[action].Kind=="Start")return done.Contains(action);
                    // Keep routes available throughout sibling starts, but never
                    // publish a replacement that happens to share its workload ID.
                    var old=initial.Source.Instances.Single(i=>i.Id==w.Id);
                    return observed.Instances.Any(i=>i.Id==old.Id&&i.InstanceId==old.InstanceId&&i.Pid==old.Pid&&i.BootId==old.BootId);
                }
                var ready=initial.Plan.Target.Workloads.Where(Ready).ToArray();
                await gateway.Publish(ready.Where(w=>w.Recipe.Kind!="Workstation"&&w.Recipe.Port>0).Select(w=>new BackendRoute(w.Route,w.Id,observed.Instances.Single(i=>i.Id==w.Id).Endpoint)).ToArray());
            }finally{publication.Release();}
        }
        async Task PrepareStations()
        {
            await Task.WhenAll(stops.Values);
            await gate.WaitAsync();try
            {
                var current=store.Get<Journal>("journal","current")!;
                if(current.Stage!="Applying")return;
                // Changing a seat's intent cannot revoke open device handles.
                // Keep its existing assignments until all prior seats release them.
                if(steps.Select((s,i)=>(s,i)).Any(x=>x.s.Kind=="Stop"&&!current.Done().Contains(x.i)&&initial.Source.Instances.Any(old=>old.Id==x.s.WorkloadId&&old.Endpoint.Length==0)))
                    throw new InvalidOperationException("A previous workstation has not released its devices.");
            }finally{gate.Release();}
            // Preparation is now claimed. Cancellation remains responsive while
            // this device reconciliation finishes at its safe boundary.
            await runtime.Prepare(initial.Plan.Target.Workloads);
        }
        var preparation=PrepareStations();
        var starts=steps.Select((s,i)=>(s,i)).Where(x=>x.s.Kind is "Keep" or "Start").Select(async action=>
        {
            var target=initial.Plan.Target.Workloads.Single(w=>w.Id==action.s.WorkloadId);
            // Handoffs on a GPU/user or physical seat must finish before its next owner starts.
            var dependencies=initial.Source.Instances.Where(old=>stops.ContainsKey(old.Id)&&
                (old.Id==target.Id||old.Gpus.Intersect(target.Gpus).Any()||target.Recipe.Kind=="Workstation"&&old.Endpoint.Length==0)).Select(old=>old.Id).ToArray();
            await Task.WhenAll(dependencies.Select(id=>stops[id]));
            if(target.Recipe.Kind=="Workstation"&&action.s.Kind=="Start")try{await preparation;}catch(Exception e){await RecordLoadResult(action.i,SafeLoadError(e));return;}
            await slots.WaitAsync();try
            {
                await gate.WaitAsync();bool blocked;try{var j=store.Get<Journal>("journal","current")!;blocked=steps.Select((s,i)=>(s,i)).Any(x=>x.s.Kind=="Stop"&&dependencies.Contains(x.s.WorkloadId)&&!j.Done().Contains(x.i));}finally{gate.Release();}
                if(blocked){await RecordLoadResult(action.i,"Waiting for the previous workload to release its devices.");return;}
                if(await LoadAction(initial,action.i))
                {
                    bool partial;await gate.WaitAsync();try{var j=store.Get<Journal>("journal","current")!;partial=steps.Select((s,i)=>(s,i)).Any(x=>x.s.Kind!="Publish"&&!j.Done().Contains(x.i));}finally{gate.Release();}
                    if(partial)try{await PublishReady();}catch(Exception e){await RecordLoadResult(Array.FindIndex(steps,s=>s.Kind=="Publish"),SafeLoadError(e));}
                }
            }finally{slots.Release();}
        }).ToArray();
        await Task.WhenAll(stops.Values.Concat(starts));
        string? preparationError=null;
        try{await preparation;}catch(Exception e){preparationError=SafeLoadError(e);}
        var publish=Array.FindIndex(steps,s=>s.Kind=="Publish");
        bool finish;await gate.WaitAsync();try{finish=store.Get<Journal>("journal","current")!.Stage=="Applying";}finally{gate.Release();}
        try{await PublishReady();if(finish&&publish>=0)await RecordLoadResult(publish,preparationError);}catch(Exception e){if(publish>=0)await RecordLoadResult(publish,SafeLoadError(e));}
        await gate.WaitAsync();try
        {
            var current=store.Get<Journal>("journal","current")!;
            if(current.Done().Length==steps.Length)store.Commit(initial.Plan.Target,current with{Stage="Complete",Error=null,RunningSteps=[],Updated=DateTimeOffset.UtcNow});
            else if(current.Stage is "Cancelling" or "Cancelled")store.Cancel(current with{Stage="Cancelled",RunningSteps=[],Updated=DateTimeOffset.UtcNow});
            else store.Commit(null,current with{Stage="Failed",RunningSteps=[],Updated=DateTimeOffset.UtcNow});
        }finally{gate.Release();}
    }
    static string SafeLoadError(Exception e)=>Redaction.Logs(e is InvalidOperationException?e.Message:"Runtime operation failed. Check the workload logs.");
    async Task<bool> LoadAction(Journal initial,int index)
    {
        var step=initial.Plan.Steps[index];bool complete;
        await gate.WaitAsync();try
        {
            var current=store.Get<Journal>("journal","current")!;complete=current.Done().Contains(index);
            if(current.Stage!="Applying")return false;
            if(complete&&step.Kind is not ("Start" or "Keep"))return true;
            store.Put("journal","current",current with{RunningSteps=[..current.RunningSteps??[],index],Updated=DateTimeOffset.UtcNow});
        }finally{gate.Release();}
        string? error=null;
        try
        {
            switch(step.Kind)
            {
                case "Drain":await gateway.Drain(step.WorkloadId);break;
                case "Stop":var old=initial.Source.Instances.Single(i=>i.Id==step.WorkloadId);await runtime.Stop(new(old.Id,old.InstanceId,old.Pid,old.BootId));break;
                case "Keep":
                    var previous=initial.Source.Instances.Single(i=>i.Id==step.WorkloadId);
                    if(!(await runtime.Observe()).Instances.Any(i=>i.Id==previous.Id&&i.InstanceId==previous.InstanceId&&i.Pid==previous.Pid&&i.BootId==previous.BootId&&i.State=="running"))throw new InvalidOperationException("An unchanged workload stopped or changed outside this operation.");
                    break;
                case "Start":
                    var target=initial.Plan.Target.Workloads.Single(w=>w.Id==step.WorkloadId);var observed=await runtime.Observe();
                    if(complete&&observed.Instances.Any(i=>i.Id==target.Id&&i.Fingerprint==target.Fingerprint&&i.State=="running"))break;
                    if(Canonical.Hash(observed.Gpus)!=Canonical.Hash(initial.Source.Gpus))throw new InvalidOperationException("GPU inventory changed during the operation.");
                    await runtime.Start(target);break;
                default:throw new InvalidOperationException("Unexpected profile action.");
            }
        }catch(Exception e){error=SafeLoadError(e);}
        await RecordLoadResult(index,error);return error==null;
    }
    async Task RecordLoadResult(int index,string? error)
    {
        if(index<0)return;
        await gate.WaitAsync();try
        {
            var current=store.Get<Journal>("journal","current")!;var done=current.Done().ToHashSet();var errors=new Dictionary<int,string>(current.StepErrors??[]);
            if(error==null){done.Add(index);errors.Remove(index);}else{done.Remove(index);var step=current.Plan.Steps[index];errors[index]=(current.Plan.Target.Workloads.FirstOrDefault(w=>w.Id==step.WorkloadId)?.Name??step.WorkloadId)+" ("+step.Kind+"): "+error;}
            var prefix=0;while(done.Contains(prefix))prefix++;
            store.Put("journal","current",current with{Completed=prefix,CompletedSteps=done.Order().ToArray(),StepErrors=errors,RunningSteps=(current.RunningSteps??[]).Where(i=>i!=index).ToArray(),Error=errors.Count==0?null:string.Join("; ",errors.OrderBy(p=>p.Key).Select(p=>p.Value)),Updated=DateTimeOffset.UtcNow});
        }finally{gate.Release();}
    }
}
