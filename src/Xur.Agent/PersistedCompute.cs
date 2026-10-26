using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

public sealed record PersistedService(string Id,Workload Workload,bool Enabled=true,long Revision=1,int Failures=0,DateTimeOffset? RetryAt=null,string? Error=null);

// Shared lifecycle intent; execution still uses the host's one workload runtime
// and allocation authority. Configuration survives stopping/removing a container.
public sealed class PersistedCompute
{
    readonly string path;
    readonly IWorkloadRuntime runtime;
    readonly Func<Workload,CancellationToken,Task<RuntimeInstance>> start;
    readonly TimeProvider clock;
    readonly object sync=new();
    readonly SemaphoreSlim reconcile=new(1,1);
    PersistedService[] services;
    readonly Dictionary<string,CancellationTokenSource> starting=[];
    readonly Dictionary<string,string> verified=[];
    readonly HashSet<string> changing=[];
    static readonly JsonSerializerOptions json=new(JsonSerializerDefaults.Web);
    public PersistedCompute(string directory,IWorkloadRuntime runtime,Func<Workload,CancellationToken,Task<RuntimeInstance>>? start=null,TimeProvider? clock=null)
    {
        path=Path.Combine(directory,"persisted-compute.json");this.runtime=runtime;this.start=start??((w,_)=>runtime.Start(w));this.clock=clock??TimeProvider.System;
        services=File.Exists(path)?JsonSerializer.Deserialize<PersistedService[]>(File.ReadAllText(path),json)??throw new IOException("Invalid Persisted Compute state."):[];
        foreach(var service in services)Validate(service);
        if(services.Select(s=>s.Id).Distinct().Count()!=services.Length||services.Select(s=>s.Workload.Id).Distinct().Count()!=services.Length)
            throw new IOException("Duplicate Persisted Compute identities.");
    }
    static void Validate(PersistedService service)
    {
        if(!ProfilePolicy.Identifier(service.Id)||service.Revision<1||service.Failures<0||service.Failures>5)
            throw new InvalidOperationException("Invalid persisted service.");
        // The initial shared lifecycle supports prepared containers without
        // exclusive allocations. T3's reviewed runtime supplies shared GPU access.
        if(service.Workload.Recipe.Kind!="Container"||service.Workload.Gpus.Length!=0)
            throw new InvalidOperationException("Persisted Compute requires a prepared container without exclusive GPU allocations.");
        ProfilePolicy.Validate(new("persistent","Persisted Compute",1,[service.Workload]),new("",[],[]));
    }
    public PersistedService[] Services(){lock(sync)return services.ToArray();}
    public bool Owns(string id){lock(sync)return services.Any(s=>s.Workload.Id==id);}
    public bool Starting(string id){lock(sync)return changing.Contains(id);}
    void Save(PersistedService[] next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using(var stream=new FileStream(path+".tmp",new FileStreamOptions{Mode=FileMode.Create,Access=FileAccess.Write,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite}))
        {JsonSerializer.Serialize(stream,next,json);stream.Flush(true);}
        File.Move(path+".tmp",path,true);services=next;
    }
    public PersistedService Register(string id,Workload workload)
    {
        lock(sync)
        {
            if(services.SingleOrDefault(s=>s.Id==id) is {} existing)
            {
                if(existing.Workload.Id!=workload.Id||existing.Workload.Fingerprint!=workload.Fingerprint)
                    throw new InvalidOperationException("This service already has a different installation. Its storage and identity were retained.");
                return existing;
            }
            var service=new PersistedService(id,workload);Validate(service);
            if(services.Any(s=>s.Workload.Id==workload.Id))throw new InvalidOperationException("This workload already belongs to a persisted service.");
            Save([..services,service]);return service;
        }
    }
    public PersistedService SetEnabled(string id,bool enabled)
    {
        lock(sync)
        {
            var service=services.SingleOrDefault(s=>s.Id==id)??throw new InvalidOperationException("Install the service first.");
            var next=service with{Enabled=enabled,Revision=service.Revision+1,Failures=0,RetryAt=null,Error=null};
            Save(services.Select(s=>s.Id==id?next:s).ToArray());
            verified.Remove(id);
            if(starting.TryGetValue(id,out var pending))pending.Cancel();
            return next;
        }
    }
    public PersistedService Retry(string id)
    {
        lock(sync)return SetEnabled(id,services.Single(s=>s.Id==id).Enabled);
    }
    void Result(PersistedService expected,string? error)
    {
        lock(sync)
        {
            var current=services.Single(s=>s.Id==expected.Id);
            if(current.Revision!=expected.Revision)return; // Off/re-enable wins over an old start completion.
            var failures=error==null?0:Math.Min(5,current.Failures+1);
            var next=current with{Failures=failures,Error=error,RetryAt=failures is >0 and <5?clock.GetUtcNow().AddSeconds(5*Math.Pow(2,failures-1)):null};
            if(next!=current)Save(services.Select(s=>s.Id==next.Id?next:s).ToArray());
        }
    }
    public async Task Reconcile(CancellationToken cancellationToken=default)
    {
        await reconcile.WaitAsync(cancellationToken);
        try
        {
            foreach(var snapshot in Services())
            {
                cancellationToken.ThrowIfCancellationRequested();
                PersistedService service;CancellationTokenSource operation;
                lock(sync)
                {
                    service=services.Single(s=>s.Id==snapshot.Id);
                    if(service.Failures>=5||service.RetryAt>clock.GetUtcNow())continue;
                    operation=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);starting[service.Id]=operation;
                }
                using(operation)
                try
                {
                    var observed=await runtime.Observe();
                    var instance=observed.Instances.SingleOrDefault(i=>i.Id==service.Workload.Id);
                    operation.Token.ThrowIfCancellationRequested();
                    if(!service.Enabled)
                    {
                        if(instance!=null){lock(sync)changing.Add(service.Id);await runtime.Stop(new(instance.Id,instance.InstanceId,instance.Pid,instance.BootId));}
                        lock(sync)verified.Remove(service.Id);
                    }
                    else
                    {
                        var identity=instance?.InstanceId+":"+instance?.BootId+":"+instance?.Pid;
                        bool healthy;lock(sync)healthy=instance?.State=="running"&&instance.Fingerprint==service.Workload.Fingerprint&&verified.GetValueOrDefault(service.Id)==identity;
                        if(!healthy)
                        {
                            lock(sync)changing.Add(service.Id);
                            if(instance!=null&&instance.Fingerprint!=service.Workload.Fingerprint)
                                await runtime.Stop(new(instance.Id,instance.InstanceId,instance.Pid,instance.BootId));
                            operation.Token.ThrowIfCancellationRequested();
                            var started=await start(service.Workload,operation.Token);
                            lock(sync)verified[service.Id]=started.InstanceId+":"+started.BootId+":"+started.Pid;
                        }
                    }
                    Result(service,null);
                }
                catch(OperationCanceledException) when(operation.IsCancellationRequested){}
                catch(Exception error)
                {Result(service,Redaction.Logs(error.Message));}
                finally{lock(sync){starting.Remove(service.Id);changing.Remove(service.Id);}}
            }
        }
        finally{reconcile.Release();}
    }
}

public sealed class PersistedRuntime(WorkloadRuntime runtime):IWorkloadRuntime
{
    public Task<RuntimeObservation> Observe()=>runtime.ObserveAll();
    public Task<RuntimeInstance> Start(Workload workload)=>runtime.StartPersisted(workload,CancellationToken.None);
    public Task Stop(RuntimeStop request)=>runtime.StopPersisted(request);
}
