using System.Text.Json;
using Xur.Domain;
namespace Xur.Gateway;
public sealed class RouteTable
{
    readonly object gate=new();
    BackendRoute[] routes=[];
    readonly Dictionary<string,int> active=new();
    readonly HashSet<string> draining=[];
    readonly string file;
    public RouteTable(string directory)
    {
        Directory.CreateDirectory(directory);file=Path.Combine(directory,"gateway-routes.json");
        if(File.Exists(file))routes=JsonSerializer.Deserialize<BackendRoute[]>(File.ReadAllText(file))!;
    }
    public Lease? Acquire(string name)
    {
        lock(gate)
        {
            var route=routes.SingleOrDefault(r=>r.Name==name);
            if(route==null || draining.Contains(route.WorkloadId))return null;
            active[route.WorkloadId]=active.GetValueOrDefault(route.WorkloadId)+1;
            return new(route,()=> {lock(gate)active[route.WorkloadId]--;});
        }
    }
    public BackendRoute[] Snapshot() {lock(gate)return routes.ToArray();}
    public void Publish(BackendRoute[] next)
    {
        if(next.Select(r=>r.Name).Distinct().Count()!=next.Length || next.Any(r=>!ProfilePolicy.Identifier(r.Name) || !ProfilePolicy.EntityIdentifier(r.WorkloadId) ||
            !Uri.TryCreate(r.Endpoint,UriKind.Absolute,out var uri) || uri.Scheme!="http" || uri.Host!="127.0.0.1" || uri.Port<1024 || uri.AbsolutePath!="/" || uri.UserInfo!="" || uri.Query!="" || uri.Fragment!=""))
            throw new InvalidOperationException("Invalid backend route");
        lock(gate)
        {
            using(var f=new FileStream(file+".tmp",FileMode.Create,FileAccess.Write)) {JsonSerializer.Serialize(f,next);f.Flush(true);}
            File.Move(file+".tmp",file,true);
            routes=next.ToArray();draining.Clear();
        }
    }
    public async Task<bool> Drain(string id,TimeSpan timeout,CancellationToken cancellation=default)
    {
        lock(gate)draining.Add(id);
        var end=DateTimeOffset.UtcNow+timeout;
        do {lock(gate)if(active.GetValueOrDefault(id)==0)return true;await Task.Delay(100,cancellation);}while(DateTimeOffset.UtcNow<end);
        return false;
    }
}
public sealed class Lease(BackendRoute route,Action release):IDisposable
{ public BackendRoute Route {get;}=route;Action? done=release;public void Dispose()=>Interlocked.Exchange(ref done,null)?.Invoke(); }
