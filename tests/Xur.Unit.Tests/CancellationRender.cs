using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xur.Control;
using Xur.Domain;
static class CancellationRender
{
    public static async Task Run(string output)
    {
        var root=Path.Combine(Path.GetTempPath(),"xur-cancel-render-"+Guid.NewGuid());Directory.CreateDirectory(root);Directory.CreateDirectory(output);
        var previous=Environment.GetEnvironmentVariable("XUR_RUN");var mode=Environment.GetEnvironmentVariable("XUR_MODE");
        Environment.SetEnvironmentVariable("XUR_RUN",root);Environment.SetEnvironmentVariable("XUR_MODE","Installed");
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.ListenUnixSocket(root+"/agent.sock"));
        await using var agent=builder.Build();agent.MapGet("/station-users",()=>Array.Empty<StationAccount>());
        using var store=new ProfileStore(root+"/state");var manager=new ProfileManager(store,new Observer(),new Gateway());
        var profile=new Profile("1","AI and workstation",1,[]);store.Save(profile);
        var plan=new ProfilePlan("operation-fixture","digest","generation","epoch",profile,[new("Keep","llm","Keep language model and active requests"),new("Start","speech","Start speech service and pass its health check"),new("Publish","","Publish routes atomically")],DateTimeOffset.UtcNow.AddMinutes(5));
        try
        {
            await agent.StartAsync();var services=new ServiceCollection();services.AddLogging();services.AddSingleton(new Appliance());services.AddSingleton(manager);
            await using var provider=services.BuildServiceProvider();await using var renderer=new HtmlRenderer(provider,provider.GetRequiredService<ILoggerFactory>());
            foreach(var stage in new[]{"Applying","Cancelling","Cancelled"})
            {
                store.Put("journal","current",new Journal(plan,new("generation",[],[]),1,stage,null,DateTimeOffset.UtcNow));
                var html=await renderer.Dispatcher.InvokeAsync(async()=> (await renderer.RenderComponentAsync<Xur.Control.Components.Pages.Profiles>(ParameterView.Empty)).ToHtmlString());
                await File.WriteAllTextAsync(Path.Combine(output,stage+".html"),"<!doctype html><html><head><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><link rel=stylesheet href='/setup.css'></head><body><main>"+html+"</main></body></html>");
            }
        }
        finally{await agent.StopAsync();Environment.SetEnvironmentVariable("XUR_RUN",previous);Environment.SetEnvironmentVariable("XUR_MODE",mode);}
        store.Dispose();Directory.Delete(root,true);
    }
    sealed class Observer:IWorkloadRuntime
    {
        public Task<RuntimeObservation> Observe()=>Task.FromResult(new RuntimeObservation("generation",[],[]));
        public Task<RuntimeInstance> Start(Workload w)=>throw new NotSupportedException();
        public Task Stop(RuntimeStop r)=>throw new NotSupportedException();
    }
    sealed class Gateway:IWorkloadGateway
    {
        public Task Drain(string id)=>throw new NotSupportedException();
        public Task Publish(BackendRoute[] routes)=>throw new NotSupportedException();
    }
}
