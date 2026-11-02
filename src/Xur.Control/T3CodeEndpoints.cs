using QRCoder;
using Xur.Domain;

namespace Xur.Control;

public static class T3CodeEndpoints
{
    public static string Origin(HostString host)=>"http://"+new HostString(host.Host,T3CodeWorkload.HostPort);
    public static void MapEndpoints(WebApplication app,Appliance appliance)
    {
        app.MapGet("/api/t3-code/status",async Task<IResult>()=>
            appliance.Installer?Results.Conflict():Results.Json(await appliance.Agent.GetFromJsonAsync<T3CodeStatus>("/t3-code/status")??new(null,"Stopped")));
        app.MapPost("/api/t3-code/enabled",async Task<IResult>(T3CodeEnabled request)=>
        {
            if(appliance.Installer)return Results.Conflict();
            using var response=await appliance.Agent.PostAsJsonAsync("/t3-code/enabled",request);
            return Results.Content(await response.Content.ReadAsStringAsync(),"application/json",statusCode:(int)response.StatusCode);
        });
        app.MapGet("/api/t3-code/storage",async Task<IResult>()=>
            appliance.Installer?Results.Conflict():Results.Json(await appliance.Agent.GetFromJsonAsync<T3StorageConfig>("/t3-code/storage")));
        app.MapPost("/api/t3-code/storage",async Task<IResult>(T3StorageConfig request)=>
        {
            if(appliance.Installer)return Results.Conflict();
            using var response=await appliance.Agent.PostAsJsonAsync("/t3-code/storage",request);
            return Results.Content(await response.Content.ReadAsStringAsync(),"application/json",statusCode:(int)response.StatusCode);
        });
        app.MapPost("/api/t3-code/prepare",async Task<IResult>(T3CodePrepareRequest request)=>
        {
            if(appliance.Installer)return Results.Conflict();
            using var response=await appliance.Agent.PostAsJsonAsync("/t3-code/prepare",request);
            return Results.Content(await response.Content.ReadAsStringAsync(),"application/json",statusCode:(int)response.StatusCode);
        });
        app.MapPost("/api/t3-code/pair",async Task<IResult>(HttpContext context)=>
        {
            if(appliance.Installer)return Results.Conflict();
            context.Response.Headers.CacheControl="no-store";
            using var response=await appliance.Agent.PostAsJsonAsync("/t3-code/pair",new{});
            if(!response.IsSuccessStatusCode)return Results.Content(await response.Content.ReadAsStringAsync(),"application/json",statusCode:(int)response.StatusCode);
            var pairing=await response.Content.ReadFromJsonAsync<T3CodePairing>()??throw new InvalidOperationException("Missing pairing response.");
            var url=Origin(context.Request.Host)+"/pair#token="+Uri.EscapeDataString(pairing.Credential);
            using var generator=new QRCodeGenerator();using var data=generator.CreateQrCode(url,QRCodeGenerator.ECCLevel.M);
            using var qr=new SvgQRCode(data);
            return Results.Json(new{url,pairing.ExpiresAt,qr=qr.GetGraphic(4)});
        });
        app.MapPost("/api/t3-code/setup",async Task<IResult>(HttpContext context,T3SetupRequest request)=>
        {
            if(appliance.Installer)return Results.Conflict();
            if(request.Action is "connect" or "connection-status" or "mcp-connect"||request.Model!=null||request.Mcp!=null)return Results.BadRequest(new{error="Use the model connection action."});
            context.Response.Headers.CacheControl="no-store";
            using var response=await appliance.Agent.PostAsJsonAsync("/t3-code/setup",request,context.RequestAborted);
            return Results.Content(await response.Content.ReadAsStringAsync(context.RequestAborted),"application/json",statusCode:(int)response.StatusCode);
        });
    }
}
