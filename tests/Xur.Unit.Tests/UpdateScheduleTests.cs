using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xur.Control;
using Xur.Domain;

static class UpdateScheduleTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var calls=new List<(string Exe,string[] Args)>();
        var updater=new Xur.Agent.OsUpdates((exe,args,_)=>{calls.Add((exe,args.ToArray()));return Task.FromResult(new ProcessResult(0,""));});
        await updater.Start(new OsUpdateAction("schedule",new("22:30",[0,4],30)));
        var request=JsonSerializer.Deserialize<OsUpdateAction>(calls.Single().Args[1],new JsonSerializerOptions(JsonSerializerDefaults.Web));
        check(calls[0].Exe.EndsWith("/os-update") && calls[0].Args[0]=="schedule" && request is {Schedule.Time:"22:30",Schedule.WarningMinutes:30} && request.Schedule.Days.SequenceEqual([0,4]),"Agent forwards validated schedule JSON directly without an arbitrary systemd command");
        await updater.Start(new OsUpdateAction("skip",WindowId:"123"));
        check(calls.Last().Args[0]=="skip" && calls.Count==2,"Agent handles skips synchronously without waiting on update jobs");
        foreach(var invalid in new[]{new OsUpdateAction("schedule",new("25:00",[0])),new("schedule",new("03:00",[])),new("schedule",new("03:00",[7])),new("schedule",new("03:00",[0],0)),new("skip",WindowId:"123;reboot")})
        {
            var rejected=false;try{await updater.Start(invalid);}catch(InvalidOperationException){rejected=true;}
            check(rejected && calls.Count==2,"Invalid update schedules and skip identities never reach the host script");
        }

        var directory=Path.GetFullPath(Path.Combine(".build","update-api-"+Guid.NewGuid().ToString("N")[..8]));Directory.CreateDirectory(directory);
        var previous=Environment.GetEnvironmentVariable("XUR_RUN");var mode=Environment.GetEnvironmentVariable("XUR_MODE");
        Environment.SetEnvironmentVariable("XUR_RUN",directory);Environment.SetEnvironmentVariable("XUR_MODE","Installed");
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.ListenUnixSocket(directory+"/agent.sock"));
        await using var agent=builder.Build();OsUpdateAction? received=null;bool fail=false;
        agent.MapPost("/updates",IResult(OsUpdateAction value)=>{received=value;return fail?Results.Conflict(new{error="This window has ended"}):Results.Accepted();});
        var controlBuilder=WebApplication.CreateBuilder();controlBuilder.Logging.ClearProviders();controlBuilder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));
        await using var control=controlBuilder.Build();var device=new Appliance();using var applianceAgent=device.Agent;
        control.MapUpdates(device);
        try
        {
            await agent.StartAsync();await control.StartAsync();using var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=new Uri(control.Urls.Single())};
            using var api=await client.PostAsJsonAsync("/api/updates",new OsUpdateAction("schedule",new("22:30",[0,4],30)));
            check(api.StatusCode==HttpStatusCode.Accepted && received is {Schedule.Time:"22:30",Schedule.WarningMinutes:30},"Authenticated update API preserves the full typed schedule request");
            var form=new[]{new KeyValuePair<string,string>("action","schedule"),new("time","21:00"),new("days","1"),new("days","5"),new("warningMinutes","45")};
            using var saved=await client.PostAsync("/updates/action",new FormUrlEncodedContent(form));
            check(saved.StatusCode==HttpStatusCode.Redirect && received is {Schedule.Time:"21:00",Schedule.WarningMinutes:45} && received.Schedule.Days.SequenceEqual([1,5]),"Browser schedule form preserves multiple selected days and returns to Updates");
            using var skipped=await client.PostAsJsonAsync("/local/updates/skip",new OsUpdateAction("stage",WindowId:"123"));
            check(skipped.StatusCode==HttpStatusCode.Accepted && received is {Action:"skip",WindowId:"123"},"Private console skip route preserves the window ID and fixes the action to its route");
            fail=true;using var stale=await client.PostAsJsonAsync("/api/updates",new OsUpdateAction("skip",WindowId:"123"));
            check(stale.StatusCode==HttpStatusCode.Conflict && (await stale.Content.ReadAsStringAsync()).Contains("window has ended"),"Stale skip requests preserve the host's conflict response");
            var count=received;
            using var bad=await client.PostAsync("/updates/action",new FormUrlEncodedContent([new("action","schedule"),new("days","Monday"),new("warningMinutes","15")]));
            check(bad.StatusCode==HttpStatusCode.BadRequest && received==count,"Malformed schedule form values are rejected before forwarding");
        }
        finally{await control.StopAsync();await agent.StopAsync();Environment.SetEnvironmentVariable("XUR_RUN",previous);Environment.SetEnvironmentVariable("XUR_MODE",mode);Directory.Delete(directory,true);}
    }
}
