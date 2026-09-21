using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xur.Control;

static class BrowserErrorTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var directory=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../.build/evidence/browser-errors-"+Guid.NewGuid().ToString("N")));
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));
        await using var app=builder.Build();app.UseBrowserErrors(directory);
        const string reason="This profile changed. Reload before saving.";
        app.MapPost("/profiles/save",()=>Results.Conflict(new{error=reason}));
        app.MapPost("/profiles/unsafe",()=>Results.Conflict(new{error="Invalid <script>alert('x')</script> & name"}));
        app.MapPost("/profiles/malformed",()=>Results.Content("{broken","application/json",statusCode:409));
        app.MapPost("/profiles/large",()=>Results.Conflict(new{error=new string('x',70000)}));
        app.MapPost("/profiles/private",()=>Results.Json(new{error="private server detail"},statusCode:500));
        app.MapPost("/profiles/expired",()=>Results.Unauthorized());
        app.MapPost("/storage/refresh",()=>Results.StatusCode(409));
        app.MapPost("/api/profiles/save",()=>Results.Conflict(new{error=reason}));
        app.MapGet("/success",()=>Results.Text("unchanged"));
        try
        {
            await app.StartAsync();using var client=new HttpClient{BaseAddress=new Uri(app.Urls.Single())};
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
            using var conflict=await client.PostAsync("/profiles/save",null);var html=await conflict.Content.ReadAsStringAsync();
            check(conflict.StatusCode==HttpStatusCode.Conflict && html.Contains(reason),"Browser conflict preserves the actionable validation reason and status");
            check(html.Split("<a ").Length==2 && html.Contains("href=\"/profiles\">Return to Profiles"),"Browser error offers one safe recovery action instead of global navigation");
            check(html.Contains("<summary>Technical details</summary>") && html.Contains("HTTP 409") && conflict.Headers.CacheControl?.NoStore==true,"Browser error keeps technical status collapsed and disables caching");
            var escaped=await (await client.PostAsync("/profiles/unsafe",null)).Content.ReadAsStringAsync();
            check(!escaped.Contains("<script>") && escaped.Contains("&lt;script&gt;") && escaped.Contains("&amp;"),"Browser validation messages are HTML encoded");
            foreach(var path in new[]{"malformed","large"})
            {
                var fallback=await (await client.PostAsync("/profiles/"+path,null)).Content.ReadAsStringAsync();
                check(fallback.Contains("unavailable in the current state"),"Browser error safely falls back for "+path+" JSON");
            }
            var privateError=await (await client.PostAsync("/profiles/private",null)).Content.ReadAsStringAsync();
            check(!privateError.Contains("private server detail") && privateError.Contains("href=\"/diagnostics\""),"Server errors hide internal details and offer Diagnostics");
            var expired=await (await client.PostAsync("/profiles/expired",null)).Content.ReadAsStringAsync();
            check(expired.Contains("href=\"/login\">Sign in") && !expired.Contains("href=\"/profiles\""),"Expired sessions offer only sign in");
            var storage=await (await client.PostAsync("/storage/refresh",null)).Content.ReadAsStringAsync();
            check(storage.Contains("href=\"/storage\"") && !storage.Contains("profile change"),"Non-profile conflicts return to their own section");
            using var api=await client.PostAsync("/api/profiles/save",null);
            check(api.Content.Headers.ContentType?.MediaType=="application/json" && (await api.Content.ReadAsStringAsync()).Contains(reason),"Browser middleware preserves JSON API responses");
            check(await client.GetStringAsync("/success")=="unchanged","Browser middleware preserves successful responses");
            client.DefaultRequestHeaders.Accept.Clear();
            using var json=await client.PostAsync("/profiles/save",null);
            check(json.Content.Headers.ContentType?.MediaType=="application/json","Non-navigation requests retain their original JSON response");
        }
        finally {await app.StopAsync();Directory.Delete(directory,true);}
    }
}
