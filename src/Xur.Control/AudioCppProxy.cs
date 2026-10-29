using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Antiforgery;
using System.Text.RegularExpressions;

namespace Xur.Control;

public static class AudioCppProxy
{
    public static string PrepareHtml(string html,string token,string nonce)
    {
        // Upstream ships a self-contained page with inline scripts. Give those
        // scripts a per-response nonce; keep Xur's normal CSP everywhere else.
        html=Regex.Replace(html,@"<script(?=[\s>])[^>]*>[\s\S]*?</script\s*>",
            match=>match.Value.Insert(7,$" nonce=\"{WebUtility.HtmlEncode(nonce)}\""),RegexOptions.IgnoreCase);
        var bridge=$"<meta name=\"xur-audio-csrf\" content=\"{WebUtility.HtmlEncode(token)}\"><script nonce=\"{WebUtility.HtmlEncode(nonce)}\" src=\"/audio-cpp.js\"></script>";
        var head=html.IndexOf("<head>",StringComparison.OrdinalIgnoreCase);
        if(head<0)throw new InvalidOperationException("The audio.cpp WebUI did not return an HTML document.");
        return html.Insert(head+6,bridge);
    }
    public static async Task ForwardPage(HttpContext context,HttpClient gateway,string path,string workloadId)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,"http://localhost/"+path);
        request.Headers.Add("X-Xur-Expected-Workload",workloadId);
        using var response=await gateway.SendAsync(request,context.RequestAborted);
        context.Response.StatusCode=(int)response.StatusCode;
        if(!response.IsSuccessStatusCode){await context.Response.WriteAsync("audio.cpp is unavailable.");return;}
        var token=context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context).RequestToken!;
        var nonce=Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        context.Response.Headers.CacheControl="no-store";
        context.Response.Headers.ContentSecurityPolicy=$"default-src 'self'; script-src 'self' 'nonce-{nonce}'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' data: blob:; font-src 'self' data:; worker-src 'self' blob:; frame-ancestors 'none'; form-action 'self'; base-uri 'none'";
        context.Response.ContentType="text/html; charset=utf-8";
        await context.Response.WriteAsync(PrepareHtml(await response.Content.ReadAsStringAsync(context.RequestAborted),token,nonce));
    }
}
