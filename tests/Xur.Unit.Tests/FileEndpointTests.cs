using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xur.Control;
using Xur.Domain;

public static class FileEndpointTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var directory=Path.GetFullPath(".build/evidence/files-api-"+Guid.NewGuid().ToString("N")[..8]);Directory.CreateDirectory(directory);
        var previous=Environment.GetEnvironmentVariable("XUR_RUN");Environment.SetEnvironmentVariable("XUR_RUN",directory);
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.ListenUnixSocket(directory+"/agent.sock"));
        await using var agent=builder.Build();string? received=null;var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.MapGet("/files/list",()=>Results.Json(new{ok=true,entries=Array.Empty<object>()}));
        agent.MapPost("/storage/files/delete",async(HttpContext ctx)=>{received=await new StreamReader(ctx.Request.Body).ReadToEndAsync();return Results.BadRequest(new{error="Delete confirmation is required."});});
        agent.MapGet("/storage/files/download",async(HttpContext ctx)=>
        {
            ctx.Response.ContentType="application/zip";ctx.Response.Headers.ContentDisposition="attachment; filename=folder.zip";
            await ctx.Response.Body.WriteAsync("PK\x03\x04"u8.ToArray());await ctx.Response.Body.FlushAsync();
            await release.Task.WaitAsync(ctx.RequestAborted);await ctx.Response.Body.WriteAsync("end"u8.ToArray());
        });
        var controlBuilder=WebApplication.CreateBuilder();controlBuilder.Logging.ClearProviders();controlBuilder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));
        await using var control=controlBuilder.Build();var device=new Appliance();using var applianceAgent=device.Agent;
        control.MapStationFiles(device);
        try
        {
            await agent.StartAsync();await control.StartAsync();using var client=new HttpClient{BaseAddress=new Uri(control.Urls.Single()),Timeout=TimeSpan.FromSeconds(10)};
            using var listing=await client.GetAsync("/api/files/list?user=station&path=");
            check(listing.IsSuccessStatusCode&&listing.Headers.CacheControl?.NoStore==true,"File listing proxy is available and explicitly uncached");
            using var deleted=await client.PostAsJsonAsync("/api/storage/files/delete?id=mount&path=folder",new{confirm=false});
            check(deleted.StatusCode==HttpStatusCode.BadRequest&&received?.Contains("false")==true&&(await deleted.Content.ReadAsStringAsync()).Contains("confirmation"),"File mutations forward confirmation and preserve agent errors");
            using var response=await client.GetAsync("/api/storage/files/download?id=mount&path=folder",HttpCompletionOption.ResponseHeadersRead);
            await using var stream=await response.Content.ReadAsStreamAsync();var buffer=new byte[4];await stream.ReadExactlyAsync(buffer);
            check(!release.Task.IsCompleted&&buffer.AsSpan().SequenceEqual("PK\x03\x04"u8)&&response.Content.Headers.ContentType?.MediaType=="application/zip"&&response.Content.Headers.ContentDisposition?.FileName=="folder.zip","ZIP download proxy delivers bytes and attachment headers before the archive is complete");
            release.SetResult();using var remainder=new StreamReader(stream);check(await remainder.ReadToEndAsync()=="end","ZIP download proxy forwards the remaining streamed bytes");
            check(!BrowserSecurity.PollPath("/api/files/list")&&!BrowserSecurity.PollPath("/api/storage/files/list")&&!BrowserSecurity.PollPath("/api/storage/files/download"),"File listings and downloads bypass polling response buffers");
        }
        finally{release.TrySetResult();await control.StopAsync();await agent.StopAsync();Environment.SetEnvironmentVariable("XUR_RUN",previous);Directory.Delete(directory,true);}
    }
}
