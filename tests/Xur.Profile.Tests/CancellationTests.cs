using Xur.Control;
using Xur.Domain;
static class CancellationTests
{
    public static async Task Run(string root,ChildRuntime runtime,IWorkloadGateway gateway,HttpClient data,Recipe recipe,Action<bool,string> check)
    {
        using var store=new ProfileStore(root+"/cancellation");var manager=new ProfileManager(store,runtime,gateway);
        var keep=new Workload("cancel-keep","Continuing",recipe,[],"cancelkeep");
        var first=new Workload("cancel-a","First added",recipe,[],"cancela");var later=new Workload("cancel-b","Later added",recipe,[],"cancelb");
        await manager.Save(new("base","Base",0,[keep]));await manager.Save(new("next","Next",0,[keep,first,later]));await manager.Save(new("empty","Empty",0,[]));
        async Task Apply(string id){var p=await manager.Preview(id);await manager.Apply(new(p.Id,p.Digest));await manager.Wait();}
        await Apply("base");var identity=(await runtime.Observe()).Instances.Single();
        using(var stream=await data.GetAsync("/cancelkeep/stream",HttpCompletionOption.ResponseHeadersRead))
        {
            using var reader=new StreamReader(await stream.Content.ReadAsStreamAsync());
            check((await reader.ReadLineAsync())!.Contains(identity.Pid.ToString()),"Cancellation test streams from the original real backend");
            runtime.PauseAfterStart=first.Id;runtime.StartReached=new(TaskCreationOptions.RunContinuationsAsynchronously);runtime.ReleaseStart=new(TaskCreationOptions.RunContinuationsAsynchronously);
            var plan=await manager.Preview("next");await manager.Apply(new(plan.Id,plan.Digest));await runtime.StartReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var rejected=false;try{await manager.Cancel("stale-operation");}catch(InvalidOperationException){rejected=true;}
            check(rejected&&(await manager.State()).Operation?.Stage=="Applying","A stale cancel request cannot cancel the current profile change");
            await manager.Cancel(plan.Id).WaitAsync(TimeSpan.FromSeconds(2));await manager.Cancel(plan.Id);
            check((await manager.State()).Operation?.Stage=="Cancelling"&&store.Get<Journal>("journal","current")?.Stage=="Cancelling","Cancel returns promptly and records its request durably while a start is pending");
            rejected=false;try{await manager.Create();}catch(InvalidOperationException){rejected=true;}
            check(rejected,"New profile changes cannot race an action still finishing after cancellation");
            runtime.ReleaseStart.SetResult();await manager.Wait();runtime.PauseAfterStart=null;
            var state=await manager.State();
            check(state.Operation?.Stage=="Cancelled"&&state.Operation.CompletedActions!.Any(s=>s.Kind=="Start"&&s.WorkloadId==first.Id),"Cancellation retains and reports the start completed at the safe boundary");
            check(state.Runtime.Instances.Any(i=>i.Id==first.Id)&&state.Runtime.Instances.Any(i=>i.Id==later.Id),"Cancellation retains independent starts already claimed in parallel");
            check(state.Runtime.Instances.Single(i=>i.Id==keep.Id)==identity,"Cancellation preserves the exact unaffected process, allocation and instance");
            check(state.Active==null,"A cancelled partial change does not claim a complete profile is loaded");
            string? line;do{line=await reader.ReadLineAsync();}while(line=="");
            check(line?.Contains(identity.Pid.ToString())==true,"The active streaming request continues across cancellation");
            check((await data.GetAsync("/cancelkeep/pid")).IsSuccessStatusCode,"Unaffected gateway route stays available after cancellation");
            var count=runtime.Created;await manager.Resume();await manager.Wait();check(runtime.Created==count,"Cancelled work is not silently resumed");
        }
        await Apply("empty");
        // Simulate a process restart after the durable request, before another
        // action can be claimed. Recovery must finalize cancellation, not apply.
        var recoveryPlan=await manager.Preview("next");
        store.Put("journal","current",new Journal(recoveryPlan,await runtime.Observe(),0,"Cancelling",null,DateTimeOffset.UtcNow));
        var recovered=new ProfileManager(store,runtime,gateway);var prior=runtime.Created;
        await recovered.Resume(automatic:true);await recovered.Wait();
        check((await recovered.State()).Operation?.Stage=="Cancelled"&&runtime.Created==prior,"Restart recovery honors durable cancellation without launching pending workloads");
        runtime.PauseAfterStart=first.Id;runtime.StartReached=new(TaskCreationOptions.RunContinuationsAsynchronously);runtime.ReleaseStart=new(TaskCreationOptions.RunContinuationsAsynchronously);runtime.FailAfterStart=first.Id;
        var failing=await manager.Preview("next");await manager.Apply(new(failing.Id,failing.Digest));await runtime.StartReached.Task.WaitAsync(TimeSpan.FromSeconds(10));await manager.Cancel(failing.Id);runtime.ReleaseStart.SetResult();await manager.Wait();runtime.PauseAfterStart=null;
        check((await manager.State()).Operation is {Stage:"Cancelled",Error:not null},"An in-flight failure cannot overwrite a requested cancellation with Failed");
        await Apply("empty");
        var publishing=new PublishingGateway(gateway);var finalManager=new ProfileManager(store,runtime,publishing);
        var finalPlan=await finalManager.Preview("base");await finalManager.Apply(new(finalPlan.Id,finalPlan.Digest));
        await publishing.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));await finalManager.Cancel(finalPlan.Id);publishing.Release.SetResult();await finalManager.Wait();
        check((await finalManager.State()) is {Active.Id:"base",Operation.Stage:"Complete"},"Cancellation racing completed final publication does not misreport a fully applied profile as partial");
        await Apply("empty");
    }
    sealed class PublishingGateway(IWorkloadGateway inner):IWorkloadGateway
    {
        public TaskCompletionSource Reached=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Drain(string id)=>inner.Drain(id);
        public async Task Publish(BackendRoute[] routes){await inner.Publish(routes);Reached.TrySetResult();await Release.Task;}
    }
}
