using Xur.Domain;
namespace Xur.Control;

public static class UnslothEndpoints
{
    public static async Task<UnslothConnection> Refresh(Appliance appliance,UnslothServe serve,UnslothSession session)
    {
        var status=await appliance.Agent.GetFromJsonAsync<UnslothStatus>("/unsloth/status")??new(false);
        // Migrate the old direct route even while Studio is starting or awaiting
        // credentials. No externally reachable route may bypass Xur sign-in.
        var connection=await serve.Refresh(status.Running,appliance.TailUrl);
        if(!status.Ready)
        {
            await session.Invalidate();
            return connection with{Url=null,Message=status.Message??"Studio is not responding. Check its workload logs."};
        }
        var login=await session.Prepare();
        return connection with{Url=login.Ready?connection.Url:null,Ready=login.Ready&&connection.Url!=null,NeedsPassword=login.NeedsPassword,Message=login.Ready?(connection.Url==null?connection.Message:login.Message):login.Message};
    }
    public static async Task<string> BootstrapPassword(Appliance appliance)
    {
        using var response=await appliance.Agent.PostAsJsonAsync("/unsloth/password",new{});
        if(response.StatusCode==System.Net.HttpStatusCode.Conflict)return "";
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UnslothPassword>())?.Password??"";
    }
    public static void MapEndpoints(WebApplication app,Appliance appliance,UnslothServe serve,UnslothSession session,Func<string?,bool> authorizedBrowser)
    {
        app.MapGet("/api/unsloth/status",async Task<IResult>()=>
        {
            if(appliance.Installer)return Results.Conflict();
            await appliance.RefreshTailscale();
            return Results.Json(await Refresh(appliance,serve,session));
        });
        app.MapPost("/api/unsloth/connect",async Task<IResult>(HttpContext context,Connect request)=>
        {
            context.Response.Headers.CacheControl="no-store";
            if(appliance.Installer)return Results.Conflict();
            if(!authorizedBrowser(context.Request.Cookies["xur.session"]))return Results.Unauthorized();
            if(string.IsNullOrEmpty(request.Password)||request.Password.Length>4096)return Results.BadRequest(new{error="Enter the current Studio password."});
            var status=await appliance.Agent.GetFromJsonAsync<UnslothStatus>("/unsloth/status");
            if(status?.Ready!=true)return Results.Conflict(new{error="Studio is not responding. Load its profile and check its logs."});
            return Results.Json(await session.Prepare(request.Password));
        });
    }
    public sealed record Connect(string Password);
}
