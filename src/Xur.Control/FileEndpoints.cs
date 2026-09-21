namespace Xur.Control;
public static class FileEndpoints
{
    public static void MapStationFiles(this WebApplication app,Appliance appliance)
    {
        foreach(var prefix in new[]{"/files","/storage/files"})
        {
            foreach(var action in new[]{"roots","list","size","download","move","delete"})
            {
                if(prefix=="/storage/files"&&action=="roots")continue;
                app.MapMethods("/api"+prefix+"/"+action,[action is "move" or "delete"?"POST":"GET"],async(HttpContext ctx)=>
                {
                    using var request=new HttpRequestMessage(new HttpMethod(ctx.Request.Method),prefix+"/"+action+ctx.Request.QueryString);
                    if(ctx.Request.Method=="POST")
                    {
                        request.Content=new StreamContent(ctx.Request.Body);
                        request.Content.Headers.ContentType=new("application/json");
                    }
                    using var response=await appliance.Agent.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ctx.RequestAborted);
                    ctx.Response.StatusCode=(int)response.StatusCode;
                    ctx.Response.ContentType=response.Content.Headers.ContentType?.ToString()??"application/json";
                    ctx.Response.Headers.CacheControl="no-store";ctx.Response.Headers["X-Content-Type-Options"]="nosniff";
                    if(response.Content.Headers.ContentDisposition is {} attachment)ctx.Response.Headers.ContentDisposition=attachment.ToString();
                    await response.Content.CopyToAsync(ctx.Response.Body,ctx.RequestAborted);
                });
            }
        }
    }
}
