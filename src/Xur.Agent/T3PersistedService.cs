using Xur.Domain;
namespace Xur.Agent;

// T3 is one consumer of the shared Persisted Compute lifecycle, not a profile.
public sealed class T3PersistedService(PersistedCompute compute,RecipeCatalog catalog,WorkloadRuntime runtime,ContainerLibrary containers)
{
    public const string Id="t3-code";
    public string? Error {get;private set;}
    public void Discover()
    {
        if(compute.Services().Any(s=>s.Id==Id))return;
        try
        {
            var existing=runtime.SavedWorkloads().SingleOrDefault(w=>T3CodeWorkload.Is(w.Recipe));
            if(existing!=null){compute.Register(Id,existing);Error=null;return;}
            var recipes=catalog.Recipes.Where(T3CodeWorkload.Is).ToArray();
            if(recipes.Length==0)return;
            if(recipes.Select(r=>r.Container!.Mounts.Single().Volume).Distinct().Count()!=1)
                throw new InvalidOperationException("Multiple T3 installations were found. Select the existing installation through Persisted Compute before enabling it.");
            var latest=containers.Jobs().FirstOrDefault(j=>j.Stage=="Ready"&&recipes.Any(r=>r.Id==j.RecipeId));
            var recipe=recipes.FirstOrDefault(r=>r.Id==latest?.RecipeId)??recipes.OrderBy(r=>r.Id,StringComparer.Ordinal).First();
            compute.Register(Id,new("persisted-t3-code","T3 Code",recipe,[],"t3-code"));Error=null;
        }
        catch(Exception error) when(error is InvalidOperationException or IOException or System.Text.Json.JsonException or UnauthorizedAccessException){Error=Redaction.Logs(error.Message);}
    }
    public async Task<T3CodeStatus> Status()
    {
        var service=compute.Services().SingleOrDefault(s=>s.Id==Id);
        var actual=await runtime.T3CodeStatus();
        if(service==null)return actual with{Installed=catalog.Recipes.Any(T3CodeWorkload.Is),Error=Error};
        var state=service.Failures>=5?"Failed":service.Error!=null?"Retrying":compute.Starting(Id)?service.Enabled?"Starting":"Stopping":!service.Enabled?actual.State=="Running"?"Stopping":"Disabled":actual.State=="Running"?"Running":"Starting";
        return new(service.Workload.Id,state,true,service.Enabled,service.Error);
    }
    public PersistedService Enable(bool enabled)=>compute.SetEnabled(Id,enabled);
}
