using Xur.Domain;
namespace Xur.Control;
public sealed partial class ProfileManager
{
    async Task RunUnload()
    {
        Journal initial;
        await gate.WaitAsync();try{initial=store.Get<Journal>("journal","current")!;}finally{gate.Release();}
        // Each pipeline drains its own requests before stopping its instance.
        // Limit concurrent pipelines without making one failure cancel siblings.
        using var slots=new SemaphoreSlim(4,4);
        var pipelines=initial.Plan.Steps.Select((step,index)=>(step,index))
            .Where(s=>s.step.Kind!="Publish").GroupBy(s=>s.step.WorkloadId).ToArray();
        await Task.WhenAll(pipelines.Select(async pipeline=> {
            await slots.WaitAsync();try
            {
                foreach(var action in pipeline)
                    if(!await UnloadAction(initial,action.index))break;
            }finally{slots.Release();}
        }));
        bool publish;
        await gate.WaitAsync();try
        {
            var current=store.Get<Journal>("journal","current")!;
            publish=current.Stage=="Applying" && Enumerable.Range(0,current.Plan.Steps.Length)
                .Where(i=>current.Plan.Steps[i].Kind!="Publish").All(i=>current.Done().Contains(i));
        }finally{gate.Release();}
        if(publish)
            foreach(var action in initial.Plan.Steps.Select((s,i)=>(s,i)).Where(x=>x.s.Kind=="Publish"))
                if(!await UnloadAction(initial,action.i))break;
        await gate.WaitAsync();try
        {
            var current=store.Get<Journal>("journal","current")!;
            if(current.Done().Length==current.Plan.Steps.Length)
                store.Commit(null,current with {Stage="Complete",Error=null,RunningSteps=[],Updated=DateTimeOffset.UtcNow});
            else if(current.Stage is "Cancelling" or "Cancelled")
                store.Cancel(current with {Stage="Cancelled",RunningSteps=[],Updated=DateTimeOffset.UtcNow});
            else
                store.Put("journal","current",current with {Stage="Failed",RunningSteps=[],Updated=DateTimeOffset.UtcNow});
        }finally{gate.Release();}
    }
    async Task<bool> UnloadAction(Journal initial,int index)
    {
        var step=initial.Plan.Steps[index];
        await gate.WaitAsync();try
        {
            var current=store.Get<Journal>("journal","current")!;
            if(current.Done().Contains(index))return true;
            if(current.Stage!="Applying")return false;
            store.Put("journal","current",current with {RunningSteps=[..current.RunningSteps??[],index],Updated=DateTimeOffset.UtcNow});
        }finally{gate.Release();}
        string? error=null;
        try
        {
            switch(step.Kind)
            {
                case "Drain":await gateway.Drain(step.WorkloadId);break;
                case "Stop":
                    var old=initial.Source.Instances.Single(i=>i.Id==step.WorkloadId);
                    await runtime.Stop(new(old.Id,old.InstanceId,old.Pid,old.BootId));break;
                case "Publish":await runtime.Prepare([]);await gateway.Publish([]);break;
                default:throw new InvalidOperationException("Unexpected unload action.");
            }
        }
        catch(Exception e){error=Redaction.Logs(e is InvalidOperationException?e.Message:"Runtime operation failed. Check the workload logs.");}
        await gate.WaitAsync();try
        {
            var current=store.Get<Journal>("journal","current")!;
            var done=current.Done().ToHashSet();var errors=new Dictionary<int,string>(current.StepErrors??[]);
            if(error==null){done.Add(index);errors.Remove(index);}else errors[index]=step.WorkloadId+" ("+step.Kind+"): "+error;
            var prefix=0;while(done.Contains(prefix))prefix++;
            store.Put("journal","current",current with {Completed=prefix,CompletedSteps=done.Order().ToArray(),StepErrors=errors,
                RunningSteps=(current.RunningSteps??[]).Where(i=>i!=index).ToArray(),
                Error=errors.Count==0?null:string.Join("; ",errors.OrderBy(p=>p.Key).Select(p=>p.Value)),Updated=DateTimeOffset.UtcNow});
        }finally{gate.Release();}
        return error==null;
    }
}
