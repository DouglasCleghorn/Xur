using Xur.Domain;
namespace Xur.Control;

public static class ConsoleProfileEndpoints
{
    public static void MapConsoleProfiles(this WebApplication app,Appliance appliance,ProfileManager manager,ProfileAccessSettings access)
    {
        async Task<IResult> Safe(Func<Task<IResult>> action)
        {
            if(appliance.Installer || !access.ConsoleAllowed)return Results.Json(new{error="Server console profile controls are disabled. Change Profile access in the web manager’s Settings."},statusCode:403);
            try{return await action();}catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}
        }
        app.MapGet("/local/profiles",()=>Safe(async()=>Results.Json(await manager.State())));
        app.MapPost("/local/profiles/preview",(ConsoleProfileSelection request)=>Safe(async()=>Results.Json(await manager.Preview(request.Id))));
        app.MapPost("/local/profiles/unload/preview",()=>Safe(async()=>Results.Json(await manager.PreviewUnload())));
        app.MapPost("/local/profiles/apply",(Approval request,HttpContext context)=>Safe(async()=> {
            var trigger=context.Request.Headers["X-Xur-Switch-Trigger"].ToString();
            if(trigger is not ("console-keyboard" or "console-controller" or "terminal"))trigger="console-menu";
            return Results.Json(await manager.Apply(request,new(trigger,"server-console")));
        }));
    }
}
public record ConsoleProfileSelection(string Id);
