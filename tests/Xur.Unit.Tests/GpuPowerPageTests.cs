using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xur.Control;
using Xur.Domain;

static class GpuPowerPageTests
{
 public static async Task Run(string root,GpuPowerSnapshot snapshot,Action<bool,string> check)
 {
  var previous=Environment.GetEnvironmentVariable("XUR_RUN");var mode=Environment.GetEnvironmentVariable("XUR_MODE");
  Environment.SetEnvironmentVariable("XUR_RUN",root);Environment.SetEnvironmentVariable("XUR_MODE","Installed");
  var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.ListenUnixSocket(root+"/agent.sock"));
  await using var agent=builder.Build();agent.MapGet("/gpu-power",()=>snapshot);
  try
  {
   await agent.StartAsync();var services=new ServiceCollection();services.AddLogging();var appliance=new Appliance();services.AddSingleton(appliance);
   await using var provider=services.BuildServiceProvider();await using var renderer=new HtmlRenderer(provider,provider.GetRequiredService<ILoggerFactory>());
   Task<string> Render()=>renderer.Dispatcher.InvokeAsync(async()=>(await renderer.RenderComponentAsync<Xur.Control.Components.Pages.GpuPower>(ParameterView.Empty)).ToHtmlString());
   var html=await Render();
   check(html.Contains("By GPU type")&&html.Contains("Save for all 3 GPUs")&&html.Contains("name=\"sameType\" value=\"true\""),"Power page renders a single model control for differently labelled cards");
   check(html.Contains("min=\"150\" max=\"350\"")&&html.Contains("placeholder=\"Mixed limits\""),"Group input displays the shared range and leaves mixed limits unselected");
   check(snapshot.Cards.All(c=>html.Contains("data-power-gpu=\""+c.Pci+"\""))&&html.Contains("Use defaults for all"),"Grouped controls preserve all individual cards and offer a group default reset");
   snapshot=new(snapshot.Cards.Select((c,i)=>i==0?c with{MinimumWatts=375}:c).ToArray());
   html=await Render();
   check(html.Contains("no shared supported power range")&&!html.Contains("Save for all 3 GPUs")&&html.Contains("Use defaults for all"),"Disjoint ranges disable a shared wattage but still allow per-card defaults");
   snapshot=new(snapshot.Cards.Where((_,i)=>i>=2).ToArray());html=await Render();
   check(!html.Contains("By GPU type")&&html.Contains("Save limit"),"A single adjustable card per model keeps the individual controls");
   appliance.Agent.Dispose();
  }
  finally{await agent.StopAsync();Environment.SetEnvironmentVariable("XUR_RUN",previous);Environment.SetEnvironmentVariable("XUR_MODE",mode);}
 }
}
