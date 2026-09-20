using Microsoft.AspNetCore.Antiforgery;
using System.Security.Cryptography;
namespace Xur.Control;

public sealed class PublicStaticAsset;
public static class BrowserSecurity
{
    public static bool ServeOrigin(HttpContext context)
    {
        if(context.Features.Get<EndpointIdentity>()?.Kind!="serve")return true;
        context.Request.Scheme="https";
        // Serve replaces Host with localhost for Unix backends. It overwrites
        // X-Forwarded-Host with the browser's original Host. Trust this only on
        // the dedicated owner-only Unix socket, never on public listeners.
        var forwarded=context.Request.Headers["X-Forwarded-Host"];
        if(forwarded.Count==0)return true;
        if(forwarded.Count!=1 || !Uri.TryCreate("https://"+forwarded[0],UriKind.Absolute,out var uri) ||
            uri.UserInfo.Length>0 || uri.AbsolutePath!="/" || uri.Query.Length>0 || uri.Fragment.Length>0 ||
            forwarded[0]!.Contains(',') || forwarded[0]!.Contains('/') || forwarded[0]!.Contains('\\'))return false;
        context.Request.Host=HostString.FromUriComponent(uri.Authority);
        return true;
    }
    public static bool SameOrigin(HttpRequest request)
    {
        var origin=request.Headers.Origin.ToString();
        if(origin.Length>0 && (!Uri.TryCreate(origin,UriKind.Absolute,out var uri) || uri.AbsolutePath!="/" || uri.Query.Length>0 || uri.Fragment.Length>0 || uri.UserInfo.Length>0 ||
            !Uri.TryCreate(request.Scheme+"://"+request.Host,UriKind.Absolute,out var own) || uri.Scheme!=own.Scheme || uri.IdnHost!=own.IdnHost || uri.Port!=own.Port))return false;
        var site=request.Headers["Sec-Fetch-Site"].ToString();
        return site is "" or "none" or "same-origin" ||
            (origin.Length==0 && request.Method is "GET" or "HEAD" && request.Headers["Sec-Fetch-Mode"]=="navigate");
    }
    public static bool PollPath(string path)=>path.StartsWith("/api/",StringComparison.Ordinal) &&
        !(path.StartsWith("/api/auth/",StringComparison.Ordinal) || path=="/api/bootstrap" || path.StartsWith("/api/api-keys",StringComparison.Ordinal) || path.EndsWith("/download",StringComparison.Ordinal) || path.EndsWith("/export",StringComparison.Ordinal));
    public static void UseBrowserOrigin(this WebApplication app)=>app.Use(async(context,next)=>
    {
        if(!SameOrigin(context.Request)){context.Response.StatusCode=403;await context.Response.WriteAsJsonAsync(new{error="Cross-origin browser requests are not allowed."});return;}
        await next();
    });
    public static void UseProtectedCompression(this WebApplication app,string spoolDirectory)
    {
        Directory.CreateDirectory(spoolDirectory);
        File.SetUnixFileMode(spoolDirectory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        // Authentication has already run. Safe-method compression requires a
        // cryptographically validated request token, not just a header's presence.
        app.Use(async(context,next)=>
        {
            if(context.Request.Method=="GET" && PollPath(context.Request.Path.Value??"") && context.Request.Headers.ContainsKey("RequestVerificationToken"))
            {
                try {await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);context.Items["compressPoll"]=true;}
                catch(AntiforgeryValidationException){context.Response.StatusCode=400;return;}
            }
            await next();
        });
        app.UseWhen(context=>context.Items.ContainsKey("compressPoll"),branch=>
        {
            branch.UseResponseCompression();
            branch.Use(async(context,next)=>
            {
                // Spill large telemetry/log snapshots to a temporary file rather
                // than retaining an unbounded second copy in memory.
                var original=context.Response.Body;
                await using var buffer=new Microsoft.AspNetCore.WebUtilities.FileBufferingWriteStream(64*1024,null,()=>spoolDirectory);
                using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                context.Response.Body=new HashSink(buffer,hash);
                try {
                    await next();
                    if(context.Response.StatusCode==200 && !context.Response.HasStarted)
                    {
                        var tag="W/\""+Convert.ToHexString(hash.GetHashAndReset())+"\"";
                        context.Response.Headers.ETag=tag;
                        if(context.Request.Headers.IfNoneMatch.ToString().Split(',').Any(t=>t.Trim()==tag || t.Trim()==tag[2..]))
                        {context.Response.StatusCode=304;context.Response.ContentLength=null;return;}
                    }
                    await buffer.DrainBufferAsync(original,context.RequestAborted);
                } finally {context.Response.Body=original;}
            });
        });
    }
    sealed class HashSink(Stream output,IncrementalHash hash):Stream
    {
        public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;
        public override long Length=>output.Length;public override long Position {get=>output.Length;set=>throw new NotSupportedException();}
        public override void Flush(){} public override Task FlushAsync(CancellationToken ct)=>Task.CompletedTask;
        public override void Write(byte[] buffer,int offset,int count){hash.AppendData(buffer,offset,count);output.Write(buffer,offset,count);}
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken ct=default){hash.AppendData(buffer.Span);await output.WriteAsync(buffer,ct);}
        public override Task WriteAsync(byte[] buffer,int offset,int count,CancellationToken ct)=>WriteAsync(buffer.AsMemory(offset,count),ct).AsTask();
        public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException();
        public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();
        public override void SetLength(long v)=>throw new NotSupportedException();
    }
}
