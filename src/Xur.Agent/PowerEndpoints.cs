using Xur.Domain;
namespace Xur.Agent;

public static class PowerEndpoints
{
    public static void MapPower(this WebApplication app,Func<bool> installing,Func<Task<bool>> deploymentBusy,
        Func<string,IEnumerable<string>,int,Task<ProcessResult>>? run=null)
    {
        run??=(exe,args,timeout)=>Processes.Run(exe,args,timeout);
        app.MapPost("/power/{action}",async Task<IResult>(string action)=> {
            if(action is not ("reboot" or "poweroff"))return Results.BadRequest();
            if(installing())return Results.Conflict(new{error="Installation is in progress. Wait for it to finish before restarting or shutting down."});
            try {
                if(await deploymentBusy())return Results.Conflict(new{error="An OS deployment is in progress. Wait for it to finish before restarting or shutting down."});
            }catch(Exception e) when(e is InvalidOperationException or System.Text.Json.JsonException or TaskCanceledException or IOException) {
                return Results.Json(new{error="Could not check for an active OS deployment. Review the system logs and retry."},statusCode:503);
            }
            // Acknowledge scheduling before the host stops the manager, so browser
            // clients enter the reconnect page only after an accepted power request.
            var result=await run("systemd-run",["--unit=xur-power","--collect","--on-active=2s","--timer-property=AccuracySec=1s","systemctl",action,"--no-block"],15);
            return result.ExitCode==0 ? Results.Accepted() : Results.Json(new{error="Could not schedule the power action. Review the system logs and retry."},statusCode:503);
        });
    }
}
