using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xur.Control;
using Xur.Domain;

static class NetworkEndpointTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var directory=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../.build/evidence/network-api-"+Guid.NewGuid().ToString("N")[..8]));Directory.CreateDirectory(directory);
        var previous=Environment.GetEnvironmentVariable("XUR_RUN");Environment.SetEnvironmentVariable("XUR_RUN",directory);
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.ListenUnixSocket(directory+"/agent.sock"));
        await using var agent=builder.Build();NetworkConfiguration? received=null;string? finish=null;bool fail=false;
        agent.MapGet("/network/settings",()=>new NetworkSettingsStatus([],null));
        agent.MapPost("/network/settings",IResult(NetworkConfiguration config)=>{received=config;return fail?Results.BadRequest(new{error="Invalid gateway"}):Results.Accepted(value:new{id="pending"});});
        agent.MapPost("/network/{action}",IResult(string action,NetworkChangeRequest request)=>{finish=action+":"+request.Id;return Results.Ok();});
        var controlBuilder=WebApplication.CreateBuilder();controlBuilder.Logging.ClearProviders();controlBuilder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));
        await using var control=controlBuilder.Build();var device=new Appliance();using var applianceAgent=device.Agent;
        // Appliance resolves the root-private socket from the temporary run directory.
        control.MapNetworkSettings(device);
        try
        {
            await agent.StartAsync();await control.StartAsync();using var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=new Uri(control.Urls.Single())};
            check((await client.GetAsync("/api/network/settings")).IsSuccessStatusCode,"Network status API forwards the agent response");
            var form=new Dictionary<string,string>{{"interface","eno1"},{"macAddress","02:00:00:00:00:10"},{"ipv4.method","manual"},{"ipv4.addresses","192.0.2.10/24\n192.0.2.11/24"},{"ipv4.gateway","192.0.2.1"},{"ipv4.dns","192.0.2.53,192.0.2.54"},{"ipv6.method","auto"}};
            using var applied=await client.PostAsync("/settings/network/apply",new FormUrlEncodedContent(form));
            check(applied.StatusCode==HttpStatusCode.Redirect && applied.Headers.Location?.OriginalString=="/settings/network" && received?.Ipv4?.Addresses?.Length==2 && received.Ipv4.Dns?.Length==2,"Browser network apply forwards typed settings and redirects instead of returning a blank page");
            fail=true;using var rejected=await client.PostAsync("/settings/network/apply",new FormUrlEncodedContent(form));
            check(rejected.Headers.Location?.OriginalString.Contains("Invalid%20gateway")==true,"Browser network errors return to the editor with the agent message");
            using var api=await client.PostAsJsonAsync("/api/network/settings",received);
            check(api.StatusCode==HttpStatusCode.BadRequest && (await api.Content.ReadAsStringAsync()).Contains("Invalid gateway"),"JSON network API preserves agent errors and status codes");
            foreach(var action in new[]{"keep","revert"})
            {
                using var finished=await client.PostAsync("/settings/network/"+action,new FormUrlEncodedContent(new Dictionary<string,string>{{"id","pending"}}));
                check(finished.StatusCode==HttpStatusCode.Redirect && finish==action+":pending","Network "+action+" form reaches the agent and returns to status");
            }
        }
        finally{await control.StopAsync();await agent.StopAsync();Environment.SetEnvironmentVariable("XUR_RUN",previous);Directory.Delete(directory,true);}
    }
}
