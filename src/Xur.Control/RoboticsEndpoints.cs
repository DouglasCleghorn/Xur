namespace Xur.Control;

public static class RoboticsEndpoints
{
    public static void MapRobotics(this WebApplication app, Appliance appliance)
    {
        async Task Relay(HttpContext context,string path)
        {
            if(appliance.Installer){context.Response.StatusCode=409;await context.Response.WriteAsJsonAsync(new{error="Robotics is available after installation."});return;}
            using var request=new HttpRequestMessage(new HttpMethod(context.Request.Method),"/robotics/"+path);
            if(context.Request.Method=="POST")
            {
                if(context.Request.ContentLength>16*1024){context.Response.StatusCode=413;return;}
                // Bound chunked bodies as well as requests with Content-Length.
                using var buffer=new MemoryStream();var bytes=new byte[4096];int count;
                while((count=await context.Request.Body.ReadAsync(bytes,context.RequestAborted))!=0)
                {if(buffer.Length+count>16*1024){context.Response.StatusCode=413;return;}await buffer.WriteAsync(bytes.AsMemory(0,count),context.RequestAborted);}
                request.Content=new ByteArrayContent(buffer.ToArray());request.Content.Headers.ContentType=new("application/json");
            }
            using var response=await appliance.Agent.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,context.RequestAborted);
            context.Response.StatusCode=(int)response.StatusCode;
            context.Response.ContentType=response.Content.Headers.ContentType?.ToString()??"application/json";
            context.Response.Headers.CacheControl="no-store";
            await response.Content.CopyToAsync(context.Response.Body,context.RequestAborted);
        }
        foreach(var path in new[]{"status","jobs","devices","configuration","detection","observation","calibration-assessment"})app.MapGet("/api/robotics/"+path,(HttpContext c)=>Relay(c,path));
        app.MapGet("/api/robotics/jobs/{id}",(HttpContext c,string id)=>Relay(c,"jobs/"+Uri.EscapeDataString(id)));
        app.MapGet("/api/robotics/jobs/{id}/captures",(HttpContext c,string id)=>Relay(c,"jobs/"+Uri.EscapeDataString(id)+"/captures"));
        app.MapGet("/api/robotics/jobs/{id}/markers",(HttpContext c,string id)=>Relay(c,"jobs/"+Uri.EscapeDataString(id)+"/markers"));
        app.MapGet("/api/robotics/jobs/{id}/captures/{name}",(HttpContext c,string id,string name)=>Relay(c,"jobs/"+Uri.EscapeDataString(id)+"/captures/"+Uri.EscapeDataString(name)));
        app.MapGet("/api/robotics/cameras/{camera}",(HttpContext c,string camera)=>Relay(c,"cameras/"+Uri.EscapeDataString(camera)));
        foreach(var path in new[]{"arm","controller","start-controller","auto-calibrate","emotes","tasks","stop","estop","reset-estop","probe","prepare","detect-buses","configure","record","train","skills/review","skills/evaluate"})app.MapPost("/api/robotics/"+path,(HttpContext c)=>Relay(c,path));
    }
}
