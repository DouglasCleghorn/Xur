using Xur.Domain;
namespace Xur.Control;

public sealed partial class ProfileManager
{
    readonly Dictionary<string,RuntimeInstance> readyModels=new();
    readonly Dictionary<string,(string Fingerprint,DateTimeOffset After)> modelRetries=new();
    string? modelRoutes;DateTimeOffset publishAfter;

    public async Task WatchModels(CancellationToken stopping)
    {
        while(!stopping.IsCancellationRequested)
        {
            try {if(!File.Exists(ApplicationMaintenance.Marker))await RestoreModels();}
            catch(Exception e){Console.Error.WriteLine("Automatic model recovery: "+Redaction.Logs(e.Message));}
            try{await Task.Delay(TimeSpan.FromSeconds(5),stopping);}catch(OperationCanceledException){break;}
        }
    }

    // Only the committed loaded profile grants automatic startup. Saved profiles,
    // failed transitions and intentional unloads cannot start background models.
    public async Task RestoreModels(DateTimeOffset? time=null)
    {
        await gate.WaitAsync();try
        {
            if(worker is {IsCompleted:false} || store.Get<Journal>("journal","current") is {Stage:not ("Complete" or "Cancelled")})return;
            var profile=store.Get<Profile>("active","current");if(profile==null)return;
            var models=profile.Workloads.Where(w=>w.Recipe.Kind=="Model").ToArray();if(models.Length==0)return;
            var observed=await runtime.Observe();var now=time??DateTimeOffset.UtcNow;
            var recover=models.Where(w=>
            {
                var current=observed.Instances.SingleOrDefault(i=>i.Id==w.Id&&i.Fingerprint==w.Fingerprint&&i.State=="running");
                return !(current!=null && readyModels.TryGetValue(w.Id,out var ready) && SameInstance(ready,current)) &&
                    !(modelRetries.TryGetValue(w.Id,out var retry) && retry.Fingerprint==w.Fingerprint && retry.After>now);
            }).ToArray();
            var routes=Routes(profile,observed,readyModels.Values.ToArray());
            if(recover.Length==0 && (modelRoutes==Canonical.Hash(routes) || publishAfter>now))return;
            var epoch=Epoch;
            worker=Task.Run(async()=>
            {
                try
                {
                    // Remove stale endpoints before downloads; keep healthy peers
                    // available throughout recovery. Publish the new port only
                    // after Start has completed its health check.
                    if(recover.Any(w=>!observed.Instances.Any(i=>i.Id==w.Id&&i.Fingerprint==w.Fingerprint&&i.State=="running")))
                        await PublishModels(Routes(profile,observed));
                    using var slots=new SemaphoreSlim(4,4);
                    var results=await Task.WhenAll(recover.Select(async w=>
                    {
                        await slots.WaitAsync();try
                        {
                            try{return (Workload:w,Instance:(RuntimeInstance?)await runtime.Start(w),Error:(string?)null);}
                            catch(Exception e){return (Workload:w,Instance:(RuntimeInstance?)null,Error:Redaction.Logs(e.Message));}
                        }finally{slots.Release();}
                    }));
                    await gate.WaitAsync();try
                    {
                        if(Epoch!=epoch)return;
                        foreach(var result in results)
                        {
                            if(result.Instance!=null){readyModels[result.Workload.Id]=result.Instance;modelRetries.Remove(result.Workload.Id);}
                            else
                            {
                                readyModels.Remove(result.Workload.Id);
                                modelRetries[result.Workload.Id]=(result.Workload.Fingerprint,(time??DateTimeOffset.UtcNow).AddMinutes(1));
                                Console.Error.WriteLine("Automatic model startup failed for "+result.Workload.Id+": "+result.Error);
                            }
                        }
                        var current=await runtime.Observe();
                        await PublishModels(Routes(profile,current,readyModels.Values.ToArray()));
                    }finally{gate.Release();}
                }
                catch(Exception e)
                {
                    await gate.WaitAsync();try
                    {publishAfter=(time??DateTimeOffset.UtcNow).AddMinutes(1);foreach(var w in recover)modelRetries[w.Id]=(w.Fingerprint,(time??DateTimeOffset.UtcNow).AddMinutes(1));}
                    finally{gate.Release();}
                    Console.Error.WriteLine("Automatic model recovery failed: "+Redaction.Logs(e.Message));
                }
            });
        }finally{gate.Release();}
    }
    async Task PublishModels(BackendRoute[] routes)
    {await gateway.Publish(routes);modelRoutes=Canonical.Hash(routes);publishAfter=default;}
    static bool SameInstance(RuntimeInstance first,RuntimeInstance second)=>first.Id==second.Id && first.Fingerprint==second.Fingerprint && first.InstanceId==second.InstanceId && first.Pid==second.Pid && first.BootId==second.BootId && first.Endpoint==second.Endpoint;
    static BackendRoute[] Routes(Profile profile,RuntimeObservation observed,RuntimeInstance[]? healthy=null)=>profile.Workloads
        .Where(w=>w.Recipe.Kind!="Workstation"&&w.Recipe.Port>0)
        .Select(w=>(Workload:w,Instance:observed.Instances.SingleOrDefault(i=>i.Id==w.Id&&i.Fingerprint==w.Fingerprint&&i.State=="running")))
        .Where(x=>x.Instance!=null && (x.Workload.Recipe.Kind!="Model" || healthy==null || healthy.Any(i=>SameInstance(i,x.Instance))))
        .Select(x=>new BackendRoute(x.Workload.Route,x.Workload.Id,x.Instance!.Endpoint)).ToArray();
}
