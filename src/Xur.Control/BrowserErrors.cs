using System.Text.Encodings.Web;
using System.Text.Json;
namespace Xur.Control;

public static class BrowserErrors
{
    public static void UseBrowserErrors(this WebApplication app,string spoolDirectory)
    {
        Directory.CreateDirectory(spoolDirectory);
        app.Use(async(context,next)=> {
            var path=context.Request.Path.Value??"/";
            var navigation=context.Request.Headers.Accept.ToString().Contains("text/html",StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("/api/")&&!path.StartsWith("/inference/")&&!path.StartsWith("/local/");
            if(!navigation){await next();return;}
            var original=context.Response.Body;
            await using var buffer=new Microsoft.AspNetCore.WebUtilities.FileBufferingWriteStream(64*1024,null,()=>spoolDirectory);
            context.Response.Body=buffer;
            try {
                try{await next();}
                catch(Exception e) when(!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested) {
                    app.Logger.LogError(e,"Browser request failed: {Path}; request {RequestId}",path,context.TraceIdentifier);
                    context.Response.Clear();context.Response.StatusCode=500;
                }
                if(context.Response.StatusCode>=400 && !context.Response.HasStarted) {
                    var code=context.Response.StatusCode;
                    var (title,message)=code switch {
                        400=>("Review your request", "The form expired or contains invalid information. Return to the page, review your entries and try again."),
                        401=>("Sign in to continue", "Your session has expired. Sign in again, then retry the action."),
                        403=>("Request blocked", "Xur could not verify this request. Open the page again and retry the action."),
                        404=>("Page not found", "This page may have moved or no longer exists."),
                        405=>("Open the page first", "This address handles a form submission. Return to the page to use the action."),
                        409=>("Action needs your attention", "This action is unavailable in the current state. Return to the page to review its status before trying again."),
                        429=>("Too many attempts", "Wait a moment before trying again."),
                        503=>("Xur is temporarily unavailable", "An update or restart may be in progress. Wait a moment, then open the page again."),
                        >=500=>("Something went wrong", "Xur could not complete the request. Open Diagnostics to check for a service problem before trying again."),
                        _=>("Request could not complete", "Return to the page to review its status before trying again.")
                    };
                    if(code>=500 && path=="/auth/setup") message="Xur could not finish creating the account. Download the setup logs, then return to account setup to retry.";
                    // Preserve deliberate validation messages, never raw server errors or HTML.
                    if(code is 400 or 409 && buffer.Length is >0 and <=65536 &&
                        context.Response.ContentType?.Split(';')[0].Trim().Equals("application/json",StringComparison.OrdinalIgnoreCase)==true)
                    {
                        using var payload=new MemoryStream();
                        await buffer.DrainBufferAsync(payload,context.RequestAborted);payload.Position=0;
                        try {
                            using var json=await JsonDocument.ParseAsync(payload,cancellationToken:context.RequestAborted);
                            if(json.RootElement.ValueKind==JsonValueKind.Object && json.RootElement.TryGetProperty("error",out var error) &&
                                error.ValueKind==JsonValueKind.String && error.GetString() is {Length:>0 and <=4096} detail && !string.IsNullOrWhiteSpace(detail))
                                message=detail;
                        }catch(JsonException) { }
                    }
                    var (href,label)=Recovery(context.Request.Path,code);
                    context.Response.Body=original;context.Response.ContentLength=null;context.Response.ContentType="text/html; charset=utf-8";context.Response.Headers.CacheControl="no-store";
                    context.Response.Headers["X-Content-Type-Options"]="nosniff";
                    context.Response.Headers.ContentSecurityPolicy="default-src 'none'; style-src 'self'; frame-ancestors 'none'; base-uri 'none'";
                    var requestId=HtmlEncoder.Default.Encode(context.TraceIdentifier);
                    var setupLogs=code>=500 && path=="/auth/setup" ? "<p><a href=\"/setup-account/logs\">Download setup logs</a></p>" : "";
                    await context.Response.WriteAsync($"""
                        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{HtmlEncoder.Default.Encode(title)} · Xur</title><link rel="stylesheet" href="/setup.css"></head><body><main class="browser-error"><h1>{HtmlEncoder.Default.Encode(title)}</h1><p role="alert">{HtmlEncoder.Default.Encode(message)}</p><div class="actions"><a class="button" href="{href}">{label}</a></div>{setupLogs}<details class="browser-error-details"><summary>Technical details</summary><p class="secondary-text">HTTP {code} · Request {requestId}</p></details></main></body></html>
                        """);return;
                }
                await buffer.DrainBufferAsync(original,context.RequestAborted);
            }finally{context.Response.Body=original;}
        });
    }

    private static (string Href,string Label) Recovery(PathString path,int code)
    {
        if(code==401)return ("/login","Sign in");
        if(code>=500 && path.StartsWithSegments("/auth/setup"))return ("/setup-account","Return to account setup");
        if(code>=500 && code!=503)return ("/diagnostics","Open Diagnostics");
        // Fixed GET destinations avoid resubmitting failed forms or trusting a referrer.
        foreach(var section in new[]{"profiles","workstations","storage","settings","network","updates","files","models","containers","gpus","endpoints","monitoring","tailscale"})
            if(path.StartsWithSegments("/"+section))return ("/"+section,"Return to "+char.ToUpperInvariant(section[0])+section[1..]);
        return ("/","Return home");
    }
}
