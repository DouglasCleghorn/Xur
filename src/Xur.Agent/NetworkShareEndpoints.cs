using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;
public static class NetworkShareEndpoints
{
    public static void MapNetworkShares(this WebApplication app,WorkloadRuntime workloads,bool installer)
    {
        var shares=workloads.NetworkShares;
        async Task<IResult> Safe(Func<Task<object>> action,bool change=true)
        {
            if(installer)return Results.Conflict(new{error="Network shares are available after installation."});
            try{return Results.Json(change?await workloads.ChangeNetworkShares(action):await action());}
            catch(Exception e) when(e is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException)
            {return Results.Conflict(new{error=e is InvalidOperationException?e.Message:"Network share operation failed. Check prerequisites and the system journal."});}
        }
        app.MapGet("/storage/network",()=>Safe(async()=>await shares.Status(),false));
        app.MapPost("/storage/network/runtime",()=>Safe(async()=>{await shares.InstallRuntime();return new{accepted=true};},false));
        app.MapPost("/storage/network/connections",(SmbConnectionRequest r)=>Safe(async()=>await shares.AddConnection(r)));
        app.MapPost("/storage/network/connections/action",(SmbAction r)=>Safe(async()=>{await shares.ConnectionAction(r,workloads.SavedWorkloads());return new{success=true};}));
        app.MapPost("/storage/network/hosted",(SmbHostRequest r)=>Safe(async()=>await shares.AddHosted(r)));
        app.MapPost("/storage/network/hosted/remove",(SmbAction r)=>Safe(async()=>{await shares.RemoveHosted(r.Id);return new{success=true};}));
        app.MapPost("/storage/network/server",(SmbServerSettings r)=>Safe(async()=>{await shares.SetServer(r);return new{success=true};}));
    }
}
