using System.Text.Json;
using Xur.Domain;
namespace Xur.Control;
public static class NetworkEndpoints
{
    public static void MapNetworkSettings(this WebApplication app,Appliance device)
    {
        async Task<IResult> Read()
        {
            using var result=await device.Agent.GetAsync("/network/settings");
            return Results.Content(await result.Content.ReadAsStringAsync(),"application/json",statusCode:(int)result.StatusCode);
        }
        async Task<IResult> Send(string path,object request,bool browser=false)
        {
            using var result=await device.Agent.PostAsJsonAsync(path,request);
            var body=await result.Content.ReadAsStringAsync();
            if(!browser)return Results.Content(body,"application/json",statusCode:(int)result.StatusCode);
            var error="Could not change network settings. Refresh and try again.";
            if(!result.IsSuccessStatusCode)try{using var json=JsonDocument.Parse(body);if(json.RootElement.ValueKind==JsonValueKind.Object && json.RootElement.TryGetProperty("error",out var message) && message.ValueKind==JsonValueKind.String)error=message.GetString()??error;}catch(JsonException){}
            return Results.Redirect("/settings/network"+(result.IsSuccessStatusCode?"":"?error="+Uri.EscapeDataString(error)));
        }
        foreach(var prefix in new[]{"/api","/local"})
        {
            app.MapGet(prefix+"/network/settings",Read);
            app.MapPost(prefix+"/network/settings",(NetworkConfiguration request)=>Send("/network/settings",request));
            foreach(var action in new[]{"keep","revert"})app.MapPost(prefix+"/network/"+action,(NetworkChangeRequest request)=>Send("/network/"+action,request));
        }
        app.MapPost("/settings/network/apply",async(HttpContext context)=>
        {
            var form=await context.Request.ReadFormAsync();
            string[] Values(string name)=>form[name].ToString().Split(['\n','\r',',',' '],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
            IpConfiguration Family(string p)=>new(form[p+".method"].ToString(),Values(p+".addresses"),form[p+".gateway"].ToString(),Values(p+".dns"));
            return await Send("/network/settings",new NetworkConfiguration(form["interface"].ToString(),form["macAddress"].ToString(),Family("ipv4"),Family("ipv6")),true);
        });
        foreach(var action in new[]{"keep","revert"})app.MapPost("/settings/network/"+action,(Func<HttpContext,Task<IResult>>)(async context=>await SendResult(await context.Request.ReadFormAsync(),action)));
        Task<IResult> SendResult(IFormCollection form,string action)=>Send("/network/"+action,new NetworkChangeRequest(form["id"].ToString()),true);
    }
}
