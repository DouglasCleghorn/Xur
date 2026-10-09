using Microsoft.AspNetCore.Antiforgery;
using Xur.Domain;

namespace Xur.Control;

public static class RobotProxy
{
    // Authentication, origin checking and antiforgery run before these endpoints.
    public static void MapRobotProxy(this WebApplication app, Appliance appliance)
    {
        var client=LocalClient.Create(Path.Combine(appliance.RunDirectory,"robot-web","app.sock"));
        app.Lifetime.ApplicationStopped.Register(client.Dispose);
        app.MapGet("/robot/csrf",(HttpContext context,IAntiforgery antiforgery)=>
            Results.Json(new{token=antiforgery.GetAndStoreTokens(context).RequestToken}));
        async Task Forward(HttpContext context)
        {
            if(appliance.Installer){context.Response.StatusCode=404;return;}
            if(!File.Exists(Path.Combine(appliance.RunDirectory,"robot-web","app.sock")))
            {
                context.Response.StatusCode=503;
                await context.Response.WriteAsJsonAsync(new{error="Load the Robotics workload in a profile to start the robot dashboard container."});
                return;
            }
            await StreamProxy.Forward(context,client,"http://robot"+context.Request.Path+context.Request.QueryString);
        }
        app.MapMethods("/robot",["GET","HEAD"],Forward);
        app.MapMethods("/robot/{**path}",["GET","HEAD","POST"],Forward);
    }
}
