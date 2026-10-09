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
            var path=context.Request.Path.Value??"/robot";
            if(context.Request.Path.StartsWithSegments("/api/robotics",out var remainder))path="/robot/api"+remainder;
            await StreamProxy.Forward(context,client,"http://robot"+path+context.Request.QueryString);
        }
        app.MapGet("/robotics",()=>Results.Redirect("/robot/setup"));
        // Existing scoped API keys and integrations retain their route while
        // all application endpoints are served by the robotics container.
        app.MapMethods("/api/robotics/{**path}",["GET","HEAD","POST"],Forward);
        app.MapMethods("/robot",["GET","HEAD"],Forward);
        app.MapMethods("/robot/{**path}",["GET","HEAD","POST"],Forward);
    }
}
