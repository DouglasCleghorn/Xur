using Microsoft.AspNetCore.Http;
namespace Xur.Domain;
public static class StreamProxy
{
    static readonly HashSet<string> skip=new(StringComparer.OrdinalIgnoreCase) {"Host","Connection","Keep-Alive","Proxy-Authenticate","Proxy-Authorization","TE","Trailer","Transfer-Encoding","Upgrade","Cookie","Set-Cookie","Authorization"};
    public static async Task Forward(HttpContext ctx,HttpClient client,string url)
    {
        using var request=new HttpRequestMessage(new HttpMethod(ctx.Request.Method),url);
        if(ctx.Request.ContentLength>0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))request.Content=new StreamContent(ctx.Request.Body);
        var excluded=new HashSet<string>(skip,StringComparer.OrdinalIgnoreCase);
        foreach(var item in ctx.Request.Headers.Connection.ToString().Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries))excluded.Add(item);
        foreach(var h in ctx.Request.Headers)
            if(!excluded.Contains(h.Key) && !h.Key.StartsWith("Tailscale-",StringComparison.OrdinalIgnoreCase) && !request.Headers.TryAddWithoutValidation(h.Key,h.Value.ToArray()))request.Content?.Headers.TryAddWithoutValidation(h.Key,h.Value.ToArray());
        try
        {
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ctx.RequestAborted);
            ctx.Response.StatusCode=(int)response.StatusCode;
            foreach(var item in response.Headers.Connection)excluded.Add(item);
            foreach(var h in response.Headers.Concat(response.Content.Headers))if(!excluded.Contains(h.Key))ctx.Response.Headers[h.Key]=h.Value.ToArray();
            await using var source=await response.Content.ReadAsStreamAsync(ctx.RequestAborted);
            var buffer=new byte[16384];int count;
            while((count=await source.ReadAsync(buffer,ctx.RequestAborted))>0)
            {await ctx.Response.Body.WriteAsync(buffer.AsMemory(0,count),ctx.RequestAborted);await ctx.Response.Body.FlushAsync(ctx.RequestAborted);}
        }
        catch(OperationCanceledException) when(ctx.RequestAborted.IsCancellationRequested) { }
        catch(HttpRequestException) {if(!ctx.Response.HasStarted)ctx.Response.StatusCode=502;else ctx.Abort();}
    }
}
