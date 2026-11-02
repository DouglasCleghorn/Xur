using Xur.Domain;
namespace Xur.Control;
public static class NetworkShareEndpoints
{
    public static void MapNetworkShares(this WebApplication app,Appliance appliance)
    {
        async Task<IResult> Relay(string path,HttpContent? content=null)
        {
            if(appliance.Installer)return Results.Conflict(new{error="Network shares are available after installation."});
            using var response=content==null?await appliance.Agent.GetAsync(path):await appliance.Agent.PostAsync(path,content);
            return Results.Content(await response.Content.ReadAsStringAsync(),"application/json",statusCode:(int)response.StatusCode);
        }
        app.MapGet("/api/storage/network",()=>Relay("/storage/network"));
        app.MapPost("/api/storage/network/runtime",()=>Relay("/storage/network/runtime",JsonContent.Create(new{})));
        app.MapPost("/api/storage/network/connections",(SmbConnectionRequest r)=>Relay("/storage/network/connections",JsonContent.Create(r)));
        app.MapPost("/api/storage/network/connections/action",(SmbAction r)=>Relay("/storage/network/connections/action",JsonContent.Create(r)));
        app.MapPost("/api/storage/network/hosted",(SmbHostRequest r)=>Relay("/storage/network/hosted",JsonContent.Create(r)));
        app.MapPost("/api/storage/network/hosted/remove",(SmbAction r)=>Relay("/storage/network/hosted/remove",JsonContent.Create(r)));
        app.MapPost("/api/storage/network/server",(SmbServerSettings r)=>Relay("/storage/network/server",JsonContent.Create(r)));
    }
}
