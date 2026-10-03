using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xur.Agent;
using Xur.Control;
using Xur.Domain;

static class RebootTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));
        await using var agent=builder.Build();bool installing=false,deployment=false,unavailable=false;int schedules=0,exitCode=0;string[] command=[];
        agent.MapPower(()=>installing,()=>unavailable?throw new InvalidOperationException():Task.FromResult(deployment),(exe,args,_)=> {
            check(exe=="systemd-run","Power actions use an acknowledged systemd timer");schedules++;command=args.ToArray();return Task.FromResult(new ProcessResult(exitCode,""));
        });
        await agent.StartAsync();
        using var agentClient=new HttpClient{BaseAddress=new Uri(agent.Urls.Single())};
        var browserBuilder=WebApplication.CreateBuilder();browserBuilder.Logging.ClearProviders();browserBuilder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));
        await using var browser=browserBuilder.Build();
        var directory=Path.GetFullPath(".build/evidence/reboot-"+Guid.NewGuid().ToString("N"));
        browser.UseBrowserErrors(directory);const string boot="11111111-1111-1111-1111-111111111111";
        browser.MapReboot(agentClient,boot);
        try
        {
            await browser.StartAsync();
            using var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=new Uri(browser.Urls.Single())};
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
            foreach(var blocker in new[]{"Installation","OS deployment"})
            {
                installing=blocker=="Installation";deployment=!installing;
                using var response=await client.PostAsync("/power/reboot",null);var html=await response.Content.ReadAsStringAsync();
                check(response.StatusCode==HttpStatusCode.Conflict && response.Headers.Location==null && html.Contains(blocker+" is in progress") && html.Contains("Return home"),"Blocked "+blocker+" shows its reason instead of entering reboot waiting");
                using var api=await client.PostAsync("/api/power/reboot",null);
                check(api.StatusCode==HttpStatusCode.Conflict && (await api.Content.ReadAsStringAsync()).Contains(blocker+" is in progress"),"Reboot API preserves the "+blocker+" reason");
                check(schedules==0,"Blocked reboot never schedules a power action");
            }
            installing=deployment=false;
            using(var success=await client.PostAsync("/power/reboot",null))
                check(success.StatusCode==HttpStatusCode.Redirect && success.Headers.Location?.ToString()=="/reboot?boot="+boot && schedules==1,"Browser enters reboot waiting only after scheduling is accepted");
            check(command.Contains("--on-active=2s") && command.TakeLast(3).SequenceEqual(new[]{"systemctl","reboot","--no-block"}),"Accepted reboot gives the browser time to receive its redirect");
            using(var api=await client.PostAsync("/api/power/reboot",null))check(api.StatusCode==HttpStatusCode.Accepted,"Reboot API retains Accepted for a scheduled reboot");
            using(var shutdown=await agentClient.PostAsync("/power/poweroff",null))check(shutdown.StatusCode==HttpStatusCode.Accepted && command[^2]=="poweroff","Shutdown shares the deployment guard and scheduling flow");
            var before=schedules;
            using(var invalid=await agentClient.PostAsync("/power/invalid",null))check(invalid.StatusCode==HttpStatusCode.BadRequest && schedules==before,"Unknown power actions cannot schedule commands");
            deployment=true;
            using(var shutdown=await agentClient.PostAsync("/power/poweroff",null))check(shutdown.StatusCode==HttpStatusCode.Conflict && schedules==before,"Shutdown cannot interrupt an OS deployment");
            deployment=false;exitCode=1;
            using(var failed=await client.PostAsync("/power/reboot",null))check(failed.StatusCode==HttpStatusCode.ServiceUnavailable && failed.Headers.Location==null,"A scheduling failure never claims the host is rebooting");
            unavailable=true;before=schedules;
            using(var failed=await client.PostAsync("/power/reboot",null))check(failed.StatusCode==HttpStatusCode.ServiceUnavailable && failed.Headers.Location==null && schedules==before,"A deployment-guard failure is reported without scheduling or redirecting");
            await agent.StopAsync();
            using(var offline=await client.PostAsync("/power/reboot",null))check(offline.StatusCode==HttpStatusCode.ServiceUnavailable && offline.Headers.Location==null,"An unreachable agent shows recovery instead of reboot waiting");
        }
        finally{await browser.StopAsync();await agent.StopAsync();Directory.Delete(directory,true);}
    }
}
