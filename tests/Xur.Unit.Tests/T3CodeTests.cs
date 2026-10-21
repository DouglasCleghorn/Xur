using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xur.Control;
using Xur.Domain;

static class T3CodeTests
{
    public static Recipe Recipe()
    {
        var preset=T3CodeWorkload.Prepare();
        return new("container-t3","T3 Code","sha256:"+new string('a',64),preset.Command!,preset.Port,preset.HealthPath,"CPU",0,0,"",
            Kind:"Container",Engine:T3CodeWorkload.Engine,Container:new([new("xur-volume-"+new string('b',32),T3CodeWorkload.Home)],preset.Environment!));
    }
    public static async Task Run(Action<bool,string> check)
    {
        var recipe=Recipe();ProfilePolicy.ValidateRecipe(recipe);
        var workload=new Workload("t3","T3 Code",recipe,[],"coding");
        var observed=new RuntimeObservation("g",[],[new(workload.Id,workload.Fingerprint,"instance",1,"boot","http://127.0.0.1:3773/","running",[])]);
        var renamed=workload with{Name="Coding",Route="t3-code"};
        check(ProfilePolicy.Steps(new("p","Coding",1,[renamed]),observed).Any(s=>s.Kind=="Keep"),"T3 Code survives profile and route changes with the same persistent storage");
        var reuse=T3CodeWorkload.Prepare(recipe.Container!.Mounts[0].Volume);
        check(reuse.Volumes![0].Volume==recipe.Container.Mounts[0].Volume,"Preparing a replacement T3 Code image can reuse its home volume");
        foreach(var invalid in new[]{recipe with{Port=8080},recipe with{Command=[]},recipe with{Engine="Unknown"},recipe with{Container=recipe.Container with{Mounts=[]}},recipe with{Container=recipe.Container with{Mounts=[recipe.Container.Mounts[0] with{ReadOnly=true}]}}})
        {bool rejected=false;try{ProfilePolicy.ValidateRecipe(invalid);}catch(InvalidOperationException){rejected=true;}check(rejected,"Invalid T3 Code runtime contract is rejected");}
        bool duplicate=false;try{ProfilePolicy.Validate(new("p","Two servers",1,[workload,workload with{Id="t3-other",Route="other"}]),observed);}catch(InvalidOperationException){duplicate=true;}
        check(duplicate,"Only one T3 Code server can own the connection address");
        check(!ApiKeys.Allows("automation","POST","/api/t3-code/pair"),"Automation keys cannot mint T3 Code device credentials");
        check(T3CodeEndpoints.Origin(new HostString("[fd00::1]:8443"))=="http://[fd00::1]:3773","T3 Code links use its direct port and preserve IPv6 hosts");
        check(T3CodeEndpoints.Origin(new HostString("coding.test:8443"))=="http://coding.test:3773","T3 Code links target the server hostname rather than the browsing device");
        await Endpoints(check);
        await T3ModelTests.Run(check);
    }
    static async Task Endpoints(Action<bool,string> check)
    {
        var root=Path.GetFullPath(".build/evidence/t3-api-"+Guid.NewGuid().ToString("N")[..12]);Directory.CreateDirectory(root);
        var oldRun=Environment.GetEnvironmentVariable("XUR_RUN");var oldMode=Environment.GetEnvironmentVariable("XUR_MODE");
        Environment.SetEnvironmentVariable("XUR_RUN",root);Environment.SetEnvironmentVariable("XUR_MODE","Installed");
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.ListenUnixSocket(root+"/agent.sock"));
        await using var agent=builder.Build();var available=true;string? requestedVolume=null;
        var expires=DateTimeOffset.UtcNow.AddMinutes(5);
        agent.MapGet("/t3-code/status",()=>new T3CodeStatus("t3","Running"));
        agent.MapPost("/t3-code/pair",IResult()=>available?Results.Json(new T3CodePairing("test/credential+only",expires)):Results.Conflict(new{error="Load a profile with T3 Code first."}));
        agent.MapPost("/t3-code/prepare",(T3CodePrepareRequest request)=>{requestedVolume=request.Volume;return Results.Json(new{stage="Preparing"});});
        var frontBuilder=WebApplication.CreateBuilder();frontBuilder.Logging.ClearProviders();frontBuilder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));
        await using var front=frontBuilder.Build();var appliance=new Appliance();T3CodeEndpoints.MapEndpoints(front,appliance);
        try
        {
            await agent.StartAsync();await front.StartAsync();using var client=new HttpClient{BaseAddress=new Uri(front.Urls.Single())};client.DefaultRequestHeaders.Host="coding.test:8443";
            var status=await client.GetFromJsonAsync<T3CodeStatus>("/api/t3-code/status");
            check(status is {State:"Running",WorkloadId:"t3"},"Connection status reports the selected running T3 workload");
            using var response=await client.PostAsJsonAsync("/api/t3-code/pair",new{});var pairing=await response.Content.ReadFromJsonAsync<JsonElement>();
            check(pairing.GetProperty("url").GetString()=="http://coding.test:3773/pair#token=test%2Fcredential%2Bonly" && pairing.GetProperty("expiresAt").GetDateTimeOffset()==expires,"Pairing uses the native T3 fragment and its expiry on the direct server port");
            check(response.Headers.CacheControl?.NoStore==true && pairing.GetProperty("qr").GetString()!.Contains("<svg"),"Pairing returns a local QR code with caching disabled");
            available=false;using var stopped=await client.PostAsJsonAsync("/api/t3-code/pair",new{});
            check(stopped.StatusCode==HttpStatusCode.Conflict && (await stopped.Content.ReadAsStringAsync()).Contains("Load a profile"),"Pairing preserves actionable errors from a stopped workload");
            var volume="xur-volume-"+new string('b',32);using var preparation=await client.PostAsJsonAsync("/api/t3-code/prepare",new T3CodePrepareRequest(volume));
            check(preparation.IsSuccessStatusCode && requestedVolume==volume,"Preparation forwards the selected persistent storage to the agent");
        }
        finally
        {
            await front.StopAsync();await agent.StopAsync();appliance.Agent.Dispose();
            Environment.SetEnvironmentVariable("XUR_RUN",oldRun);Environment.SetEnvironmentVariable("XUR_MODE",oldMode);Directory.Delete(root,true);
        }
    }
}
