using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xur.Control;
using Xur.Domain;

static class RebootAvailabilityTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(".build/evidence/reboot-ui-"+Guid.NewGuid().ToString("N")[..8]);Directory.CreateDirectory(root);
        var previous=Environment.GetEnvironmentVariable("XUR_RUN");var mode=Environment.GetEnvironmentVariable("XUR_MODE");
        Environment.SetEnvironmentVariable("XUR_RUN",root);Environment.SetEnvironmentVariable("XUR_MODE","Installed");
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.ListenUnixSocket(root+"/agent.sock"));
        await using var agent=builder.Build();bool unavailable=false,busy=false;
        agent.MapGet("/update-all",()=>unavailable?Results.StatusCode(503):Results.Json(new UpdateAllStatus(busy,null)));
        agent.MapGet("/updates",()=>new OsUpdateStatus(null,new("new","digest","image",false),null,null,false,true,true,null,""));
        using var store=new ProfileStore(root+"/state");var profile=new Profile("1","Recovery",1,[]);
        var plan=new ProfilePlan("plan","digest","generation","epoch",profile,[],DateTimeOffset.UtcNow.AddMinutes(5));store.Save(profile);
        var appliance=new Appliance();using var device=appliance.Agent;
        try
        {
            await agent.StartAsync();var services=new ServiceCollection();services.AddLogging();
            services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor{HttpContext=new DefaultHttpContext()});services.AddSingleton(appliance);
            services.AddSingleton(new ProfileManager(store,new Observer(),new Gateway()));
            await using var provider=services.BuildServiceProvider();await using var renderer=new HtmlRenderer(provider,provider.GetRequiredService<ILoggerFactory>());
            foreach(var stage in new[]{"Complete","Applying","Cancelling","Failed"})
            foreach(var update in new[]{"idle","busy","unavailable"})
            {
                busy=update=="busy";unavailable=update=="unavailable";
                store.Put("journal","current",new Journal(plan,new("generation",[],[]),0,stage,null,DateTimeOffset.UtcNow));
                var html=await renderer.Dispatcher.InvokeAsync(async()=> (await renderer.RenderComponentAsync<Xur.Control.Components.ControlPanel>()).ToHtmlString());
                var button=Regex.Match(html,"<button[^>]*id=\"home-reboot\"[^>]*>").Value;
                check(button.Length>0 && !button.Contains("disabled") && html.Contains("id=\"home-reboot-dialog\"") && html.Contains("Running workloads and connected workstations will be interrupted."),"Reboot keeps confirmation available with "+stage+" profile and "+update+" updates");
                if(stage!="Complete" || update!="idle")check(Regex.IsMatch(html,"action=\"/updates/all\"[^<]*>.*?<button disabled",RegexOptions.Singleline),"Update All retains its guard with "+stage+" profile and "+update+" updates");
            }
        }
        finally{await agent.StopAsync();Environment.SetEnvironmentVariable("XUR_RUN",previous);Environment.SetEnvironmentVariable("XUR_MODE",mode);store.Dispose();Directory.Delete(root,true);}
    }
    sealed class Observer:IWorkloadRuntime
    {
        public Task<RuntimeObservation> Observe()=>Task.FromResult(new RuntimeObservation("generation",[],[]));
        public Task<RuntimeInstance> Start(Workload workload)=>throw new NotSupportedException();
        public Task Stop(RuntimeStop request)=>throw new NotSupportedException();
    }
    sealed class Gateway:IWorkloadGateway
    {
        public Task Drain(string id)=>throw new NotSupportedException();
        public Task Publish(BackendRoute[] routes)=>throw new NotSupportedException();
    }
}
