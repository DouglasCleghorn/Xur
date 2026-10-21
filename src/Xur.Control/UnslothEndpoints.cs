using Xur.Domain;
namespace Xur.Control;

public static class UnslothEndpoints
{
    public static async Task<UnslothConnection> Refresh(Appliance appliance,UnslothServe serve)
    {
        var status=await appliance.Agent.GetFromJsonAsync<UnslothStatus>("/unsloth/status")??new(false);
        return await serve.Refresh(status.Running,appliance.TailUrl);
    }
    public static void MapEndpoints(WebApplication app,Appliance appliance,UnslothServe serve)
    {
        app.MapGet("/api/unsloth/status",async Task<IResult>()=>
        {
            if(appliance.Installer)return Results.Conflict();
            await appliance.RefreshTailscale();
            return Results.Json(await Refresh(appliance,serve));
        });
        app.MapPost("/api/unsloth/password",async Task<IResult>(HttpContext context)=>
        {
            context.Response.Headers.CacheControl="no-store";
            if(appliance.Installer)return Results.Conflict();
            using var response=await appliance.Agent.PostAsJsonAsync("/unsloth/password",new{});
            return Results.Content(await response.Content.ReadAsStringAsync(),"application/json",statusCode:(int)response.StatusCode);
        });
    }
}
