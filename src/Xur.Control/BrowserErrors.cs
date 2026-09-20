using System.Text.Encodings.Web;
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
                    app.Logger.LogError(e,"Browser request failed: {Path}",path);
                    context.Response.Clear();context.Response.StatusCode=500;
                }
                if(context.Response.StatusCode>=400 && !context.Response.HasStarted) {
                    var code=context.Response.StatusCode;
                    var message=code switch {
                        400=>"This form expired or contains invalid information. Open the page again, review the form and retry.",
                        401=>"Your session has expired. Sign in again to continue.",
                        403=>"This request was rejected for your security. Open Xur directly, refresh the page and try again.",
                        404=>"This page could not be found. Use the links below to continue.",
                        405=>"This address accepts a form submission. Open its page below to use the action.",
                        409=>"The action could not be completed. Check the current profile change and refresh before retrying.",
                        503=>"Xur is temporarily unavailable or an update is in progress. Wait a moment, then open the page again.",
                        _=>"The action could not be completed. Check Diagnostics, then open the page again to retry."
                    };
                    context.Response.Body=original;context.Response.ContentLength=null;context.Response.ContentType="text/html; charset=utf-8";context.Response.Headers.CacheControl="no-store";
                    context.Response.Headers["X-Content-Type-Options"]="nosniff";
                    context.Response.Headers.ContentSecurityPolicy="default-src 'none'; style-src 'self'; frame-ancestors 'none'; base-uri 'none'";
                    await context.Response.WriteAsync("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Request could not complete · Xur</title><link rel=\"stylesheet\" href=\"/setup.css\"></head><body><main><h1>Request could not complete</h1><p role=\"alert\">"+HtmlEncoder.Default.Encode(message)+"</p><p class=\"secondary-text\">HTTP "+code+"</p><div class=\"actions\"><a class=\"button\" href=\"/\">Home</a><a class=\"button secondary\" href=\"/profiles\">Profiles</a><a class=\"button secondary\" href=\"/workstations\">Workstations</a><a class=\"button secondary\" href=\"/storage\">Storage</a><a class=\"button secondary\" href=\"/settings\">Settings</a><a class=\"button quiet\" href=\"/diagnostics\">Diagnostics</a><a class=\"button quiet\" href=\"/login\">Sign in</a></div></main></body></html>");return;
                }
                await buffer.DrainBufferAsync(original,context.RequestAborted);
            }finally{context.Response.Body=original;}
        });
    }
}
