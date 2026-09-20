using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xur.Control;
using Xur.Domain;

static class UpdatesRender
{
    public static async Task Run(string output)
    {
        var root=Path.Combine(Path.GetTempPath(),"xur-updates-render-"+Guid.NewGuid());Directory.CreateDirectory(root);
        var previous=Environment.GetEnvironmentVariable("XUR_RUN");
        var mode=Environment.GetEnvironmentVariable("XUR_MODE");
        Environment.SetEnvironmentVariable("XUR_RUN",root);Environment.SetEnvironmentVariable("XUR_MODE","Installed");
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k=>k.ListenUnixSocket(Path.Combine(root,"agent.sock")));
        await using var agent=builder.Build();
        agent.MapGet("/application-updates",()=>new ApplicationUpdateStatus("test.invalid:8088",new("current","1"),null,new("next","2"),null,false));
        agent.MapGet("/updates",()=>new {current=new{version="44",digest="old",image="upstream"},available=new{version="45",digest="new",image="upstream"},automatic=true,busy=false});
        agent.MapGet("/tool-updates",async()=>await new Xur.Agent.ToolUpdateInventory(AppContext.BaseDirectory,(exe,args,timeout)=>Task.FromResult(new ProcessResult(exe=="podman"?1:0,"test-version"))).Read());
        try
        {
            await agent.StartAsync();
            var services=new ServiceCollection();services.AddLogging();services.AddSingleton(new Appliance());
            await using var provider=services.BuildServiceProvider();
            await using var renderer=new HtmlRenderer(provider,provider.GetRequiredService<ILoggerFactory>());
            var html=await renderer.Dispatcher.InvokeAsync(async()=> (await renderer.RenderComponentAsync<Xur.Control.Components.Pages.Updates>(ParameterView.Empty)).ToHtmlString());
            await File.WriteAllTextAsync(output,"<!doctype html><html><head><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><link rel=stylesheet href='/setup.css'></head><body><main>"+html+"</main></body></html>");
        }
        finally {await agent.StopAsync();Environment.SetEnvironmentVariable("XUR_RUN",previous);Environment.SetEnvironmentVariable("XUR_MODE",mode);Directory.Delete(root,true);}
    }
}
