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
        WifiConnectRequest? wifi=null;WifiScanRequest? scan=null;var enabled=false;var wifiFailure=false;
        agent.MapGet("/network/wifi",()=>new WifiStatus(enabled,true,[new("wlan0","02:00:00:00:00:30","disconnected",null,"/device/1")]));
        agent.MapPost("/network/wifi/scan",(WifiScanRequest request)=>{scan=request;return new[]{new WifiNetwork("Home","02:00:00:00:00:20","WPA2",80,"wpa-psk")};});
        agent.MapPost("/network/wifi/enable",()=>{enabled=true;return Results.Ok();});
        agent.MapPost("/network/wifi/connect",IResult(WifiConnectRequest request)=>{wifi=request;return wifiFailure?Results.BadRequest(new{error="Could not connect to Wi-Fi. Check the password and signal, then retry."}):Results.Ok(new{stage="Kept",addresses=new[]{"192.0.2.30/24"}});});
        agent.MapGet("/computer-name",()=>new ComputerNameStatus("xur-test",false));
        agent.MapPost("/computer-name",(ComputerNameRequest request)=>new ComputerNameStatus(request.Name,true));
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
            using var wifiResponse=await client.PostAsJsonAsync("/local/network/wifi/connect",new WifiConnectRequest("wlan0","02:00:00:00:00:10","Home","02:00:00:00:00:20","wpa-psk","  fixture password "));
            check(wifiResponse.IsSuccessStatusCode&&wifi?.Password=="  fixture password ","Private console Wi-Fi route preserves password whitespace through the agent socket");
            var wifiStatus=await client.GetFromJsonAsync<WifiStatus>("/api/network/wifi");
            check(wifiStatus is {Enabled:false,Adapters.Length:1} && wifiStatus.Adapters[0].Interface=="wlan0","Browser Wi-Fi inventory exposes adapters and radio status");
            using var enableResponse=await client.PostAsync("/settings/network/wifi/enable",new FormUrlEncodedContent([]));
            check(enabled && enableResponse.StatusCode==HttpStatusCode.Redirect && enableResponse.Headers.Location?.OriginalString=="/settings/network","Browser Enable Wi-Fi form enables the radio and returns to network settings");
            using var enabledApi=await client.PostAsJsonAsync("/api/network/wifi/enable",new{});
            check(enabledApi.IsSuccessStatusCode,"Wi-Fi radio can also be enabled through the authenticated API");
            using var scanned=await client.PostAsJsonAsync("/api/network/wifi/scan",new WifiScanRequest("wlan0","02:00:00:00:00:30"));
            check(scan is {Interface:"wlan0",MacAddress:"02:00:00:00:00:30"} && (await scanned.Content.ReadFromJsonAsync<WifiNetwork[]>()) is [{Ssid:"Home",NeedsPassword:true}],"Browser Wi-Fi scan uses the selected adapter and preserves security details");
            using var connected=await client.PostAsJsonAsync("/api/network/wifi/connect",new WifiConnectRequest("wlan0","02:00:00:00:00:30","Home","02:00:00:00:00:20","wpa-psk","  fixture password "));
            check(connected.IsSuccessStatusCode && wifi is {Interface:"wlan0",MacAddress:"02:00:00:00:00:30",Password:"  fixture password "} && (await connected.Content.ReadAsStringAsync()).Contains("192.0.2.30/24"),"Browser Wi-Fi connection preserves credentials and returns the saved connection address");
            wifiFailure=true;
            using var connectionError=await client.PostAsJsonAsync("/api/network/wifi/connect",wifi);
            var wifiError=await connectionError.Content.ReadAsStringAsync();
            check(connectionError.StatusCode==HttpStatusCode.BadRequest && wifiError.Contains("Check the password") && !wifiError.Contains("fixture password"),"Browser Wi-Fi failures preserve the backend error without returning credentials");
            var name=await client.GetFromJsonAsync<ComputerNameStatus>("/local/computer-name");
            using var named=await client.PostAsJsonAsync("/local/computer-name",new ComputerNameRequest("living-room"));
            check(name is {Configured:false} && (await named.Content.ReadFromJsonAsync<ComputerNameStatus>()) is {Name:"living-room",Configured:true},"Private console routes expose the initial server-name prompt and save the name");
            foreach(var action in new[]{"keep","revert"})
            {
                using var finished=await client.PostAsync("/settings/network/"+action,new FormUrlEncodedContent(new Dictionary<string,string>{{"id","pending"}}));
                check(finished.StatusCode==HttpStatusCode.Redirect && finish==action+":pending","Network "+action+" form reaches the agent and returns to status");
            }
        }
        finally{await control.StopAsync();await agent.StopAsync();Environment.SetEnvironmentVariable("XUR_RUN",previous);Directory.Delete(directory,true);}
    }
}
