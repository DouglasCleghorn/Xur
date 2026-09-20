using System.Collections.Concurrent;
namespace Xur.Agent;

// Several stops share the runtime's mutation gate, excluding starts and
// streaming restarts until all of their identity/release checks have finished.
public sealed class ParallelStopGate(SemaphoreSlim exclusive)
{
    readonly SemaphoreSlim membership=new(1,1);
    readonly ConcurrentDictionary<string,SemaphoreSlim> resources=new();
    int active;
    public async Task<IAsyncDisposable> Enter()
    {
        await membership.WaitAsync();try
        {
            if(active==0)await exclusive.WaitAsync();
            active++;return new Lease(async()=>{
                await membership.WaitAsync();try{if(--active==0)exclusive.Release();}finally{membership.Release();}
            });
        }finally{membership.Release();}
    }
    public async Task<IAsyncDisposable> Resources(IEnumerable<string> keys)
    {
        var held=new List<SemaphoreSlim>();
        try
        {
            foreach(var key in keys.Distinct().Order(StringComparer.Ordinal))
            {var mutex=resources.GetOrAdd(key,_=>new SemaphoreSlim(1,1));await mutex.WaitAsync();held.Add(mutex);}
            return new Lease(()=>{foreach(var mutex in held.AsEnumerable().Reverse())mutex.Release();return Task.CompletedTask;});
        }
        catch{foreach(var mutex in held.AsEnumerable().Reverse())mutex.Release();throw;}
    }
    sealed class Lease(Func<Task> release):IAsyncDisposable
    {
        Func<Task>? action=release;
        public async ValueTask DisposeAsync(){var run=Interlocked.Exchange(ref action,null);if(run!=null)await run();}
    }
}
