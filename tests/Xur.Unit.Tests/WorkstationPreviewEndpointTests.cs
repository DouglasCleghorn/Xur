using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xur.Control;

static class WorkstationPreviewEndpointTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var png=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAABAAAAAJCAIAAAC0SDtlAAAAF0lEQVR4nGN0LJvAQApgIkn1qAZaaQAAQcoBWfCjfbMAAAAASUVORK5CYII=");
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));
        await using var agent=builder.Build();bool fail=false;string? workstation=null;
        agent.MapGet("/workstations/{id}/screenshot",IResult(string id)=>{workstation=id;return fail?Results.Conflict(new{error="Workstation is not loaded."}):Results.File(png,"image/png");});
        var controlBuilder=WebApplication.CreateBuilder();controlBuilder.Logging.ClearProviders();controlBuilder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));
        await using var control=controlBuilder.Build();
        try
        {
            await agent.StartAsync();using var agentClient=new HttpClient{BaseAddress=new Uri(agent.Urls.Single())};control.MapWorkstationPreviews(agentClient);
            await control.StartAsync();using var client=new HttpClient{BaseAddress=new Uri(control.Urls.Single())};
            using var response=await client.GetAsync("/api/workstations/desktop-one/screenshot");
            check(response.IsSuccessStatusCode&&response.Content.Headers.ContentType?.MediaType=="image/png"&&(await response.Content.ReadAsByteArrayAsync()).SequenceEqual(png)&&workstation=="desktop-one","Workstation preview proxy routes the chosen desktop and preserves PNG bytes");
            check(response.Headers.CacheControl?.NoStore==true&&response.Headers.GetValues("X-Content-Type-Options").Single()=="nosniff","Desktop preview responses prevent caching and MIME sniffing");
            fail=true;using var stopped=await client.GetAsync("/api/workstations/desktop-one/screenshot");
            check(stopped.StatusCode==HttpStatusCode.Conflict&&stopped.Headers.CacheControl?.NoStore==true&&stopped.Content.Headers.ContentType?.MediaType=="application/json"&&(await stopped.Content.ReadAsStringAsync()).Contains("not loaded"),"A stopped desktop returns the agent error instead of a cached screenshot");
        }
        finally{await control.StopAsync();await agent.StopAsync();}
    }
}
