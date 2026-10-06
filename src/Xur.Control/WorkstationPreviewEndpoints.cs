namespace Xur.Control;

public static class WorkstationPreviewEndpoints
{
    public static void MapWorkstationPreviews(this WebApplication app,HttpClient agent)
    {
        app.MapGet("/api/workstations/{id}/screenshot",async(HttpContext context,string id)=>{
            using var response=await agent.GetAsync("/workstations/"+Uri.EscapeDataString(id)+"/screenshot",HttpCompletionOption.ResponseHeadersRead,context.RequestAborted);
            context.Response.StatusCode=(int)response.StatusCode;
            context.Response.ContentType=response.Content.Headers.ContentType?.ToString()??"application/json";
            context.Response.Headers.CacheControl="no-store";context.Response.Headers["X-Content-Type-Options"]="nosniff";
            await response.Content.CopyToAsync(context.Response.Body,context.RequestAborted);
        });
    }
}
