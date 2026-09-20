namespace Xur.Control;
public static class FileEndpoints
{
    public static void MapStationFiles(this WebApplication app,Appliance appliance)
    {
        app.MapGet("/api/files/roots",async()=>
        {
            using var result=await appliance.Agent.GetAsync("/files/roots");
            return Results.Content(await result.Content.ReadAsStringAsync(),"application/json",statusCode:(int)result.StatusCode);
        });
        app.MapGet("/api/files/list",async(string user,string? path,string? q)=>
        {
            using var result=await appliance.Agent.GetAsync(Url("list",user,path,q));
            return Results.Content(await result.Content.ReadAsStringAsync(),"application/json",statusCode:(int)result.StatusCode);
        });
        app.MapGet("/api/files/download",async(HttpContext ctx,string user,string path)=>
        {
            using var response=await appliance.Agent.GetAsync(Url("download",user,path,null),HttpCompletionOption.ResponseHeadersRead,ctx.RequestAborted);
            ctx.Response.StatusCode=(int)response.StatusCode;
            ctx.Response.ContentType=response.IsSuccessStatusCode?"application/octet-stream":"application/json";
            ctx.Response.Headers.CacheControl="no-store";ctx.Response.Headers["X-Content-Type-Options"]="nosniff";
            if(response.Content.Headers.ContentDisposition is {} attachment)ctx.Response.Headers.ContentDisposition=attachment.ToString();
            await response.Content.CopyToAsync(ctx.Response.Body,ctx.RequestAborted);
        });
    }
    static string Url(string action,string user,string? path,string? q)=>"/files/"+action+"?user="+Uri.EscapeDataString(user)+"&path="+Uri.EscapeDataString(path??"")+"&q="+Uri.EscapeDataString(q??"");
}
