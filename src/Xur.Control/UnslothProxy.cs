using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Xur.Domain;

namespace Xur.Control;

// Dedicated owner-only Serve socket. Never mounted on the public manager
// listener: every path on this socket belongs to Studio, not Xur's API.
public sealed class UnslothProxy(UnslothSession session,Func<string?,bool> authorized,Func<string> managerUrl,HttpClient client)
{
    static readonly HashSet<string> Excluded=new(StringComparer.OrdinalIgnoreCase){"Host","Cookie","Set-Cookie","Authorization","Connection","Keep-Alive","Proxy-Authenticate","Proxy-Authorization","TE","Trailer","Transfer-Encoding","Upgrade","Accept-Encoding","Content-Length","Origin","Referer","RequestVerificationToken","Forwarded","X-Real-IP","If-None-Match","If-Modified-Since"};
    readonly Dictionary<string,(CancellationTokenSource Stop,int Count)> active=[];
    readonly object sync=new();
    // Studio decodes subject/role for display. This deliberately unsigned,
    // invalid JWT-shaped marker is UI state, never an authentication credential.
    public const string BrowserToken="xur-managed.eyJzdWIiOiJ1bnNsb3RoIiwicm9sZSI6Im93bmVyIiwieHVyX3VpX29ubHkiOnRydWV9.not-a-credential";
    public static readonly string SessionScript="""
        (()=>{
          localStorage.setItem('unsloth_auth_token','__XUR_UI_TOKEN__');
          localStorage.setItem('unsloth_auth_refresh_token','__XUR_UI_TOKEN__');
          localStorage.removeItem('unsloth_auth_must_change_password');
          if(!localStorage.getItem('unsloth_auth_session_mark'))localStorage.setItem('unsloth_auth_session_mark',crypto.randomUUID());
          const check=async()=>{
            try{const r=await fetch('/_xur/session',{cache:'no-store'});if(r.status===401){
              for(const key of ['unsloth_auth_token','unsloth_auth_refresh_token','unsloth_auth_must_change_password','unsloth_auth_session_mark'])localStorage.removeItem(key);
              const url=new URL('/login',location.href);url.port='';location.replace(url);
            }}catch{}
          };
          setInterval(check,15000);addEventListener('focus',check);
        })();
        """.Replace("__XUR_UI_TOKEN__",BrowserToken);
    public static HttpClient CreateClient()=>new(new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false,UseCookies=false})
        {BaseAddress=new Uri(UnslothWorkload.Backend+"/"),Timeout=Timeout.InfiniteTimeSpan};
    public void Revoke(string? cookie)
    {
        if(string.IsNullOrEmpty(cookie))return;
        lock(sync)if(active.TryGetValue(Key(cookie),out var current))current.Stop.Cancel();
    }
    static string Key(string cookie)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cookie)));
    public async Task Handle(HttpContext context)
    {
        context.Response.Headers.CacheControl="no-store";
        context.Response.Headers.XContentTypeOptions="nosniff";
        context.Response.Headers["Referrer-Policy"]="no-referrer";
        // Studio's model/dataset pickers call these public APIs from the browser.
        // Keep scripts, styles and fonts local; allow only the required Hub API origins.
        context.Response.Headers.ContentSecurityPolicy="default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; worker-src 'self' blob:; connect-src 'self' https://huggingface.co https://datasets-server.huggingface.co; frame-ancestors 'none'; form-action 'self'; base-uri 'none'";
        var manager=Manager();
        if(manager==null || context.Request.Scheme!="https" || context.Request.Host.Host!=manager.Host || context.Request.Host.Port!=UnslothWorkload.HttpsPort)
        {context.Response.StatusCode=400;return;}
        var unsafeRequest=context.Request.Method is not ("GET" or "HEAD" or "OPTIONS") || context.WebSockets.IsWebSocketRequest;
        // Studio does not send Xur antiforgery tokens. Require a browser Origin
        // on every mutation and WebSocket handshake, including multipart uploads.
        if(!BrowserSecurity.SameOrigin(context.Request) || unsafeRequest && string.IsNullOrEmpty(context.Request.Headers.Origin))
        {context.Response.StatusCode=403;return;}
        var cookie=context.Request.Cookies["xur.session"];
        if(!authorized(cookie))
        {
            if(context.Request.Method=="GET" && context.Request.Headers.Accept.ToString().Contains("text/html") && !context.WebSockets.IsWebSocketRequest)
                context.Response.Redirect(new Uri(manager,"login").AbsoluteUri);
            else context.Response.StatusCode=401;
            return;
        }
        // Gate the same normalized path that HttpClient sends and the backend
        // decodes; dot segments or escaped separators must not evade auth routes.
        var path=Uri.UnescapeDataString(Target(context,"").AbsolutePath);
        if(path=="/_xur/session" && context.Request.Method=="GET"){await context.Response.WriteAsJsonAsync(new{authenticated=true});return;}
        if(path=="/api/auth/logout" && context.Request.Method=="POST")
        {
            Revoke(cookie);context.Response.Cookies.Delete("xur.session",new CookieOptions{Path="/",Secure=true,HttpOnly=true});
            context.Response.StatusCode=204;return;
        }
        var key=Key(cookie!);CancellationTokenSource stop;
        lock(sync)
        {
            if(!active.TryGetValue(key,out var value))
            {
                if(active.Count>=256){context.Response.StatusCode=503;return;}
                value=(new CancellationTokenSource(),0);
            }
            active[key]=(value.Stop,value.Count+1);stop=value.Stop;
        }
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted,stop.Token);
        try
        {
            var token=await session.AccessToken();
            if(path=="/_xur/session.js" && context.Request.Method=="GET")
            {context.Response.ContentType="text/javascript; charset=utf-8";await context.Response.WriteAsync(SessionScript,lifetime.Token);return;}
            if(path.StartsWith("/api/auth/",StringComparison.Ordinal))
            {
                // Never expose upstream tokens or allow the shared password to be
                // changed behind the server's credential store.
                if(path=="/api/auth/status" && context.Request.Method=="GET")
                    await context.Response.WriteAsJsonAsync(new{initialized=true,default_username="unsloth",login_mode="single",full_access=true,requires_password_change=false,bootstrap_deadline_seconds=(int?)null},lifetime.Token);
                else if(path is "/api/auth/login" or "/api/auth/refresh" && context.Request.Method=="POST")
                    await context.Response.WriteAsJsonAsync(new{access_token=BrowserToken,refresh_token=BrowserToken,token_type="bearer",must_change_password=false,account_id="owner"},lifetime.Token);
                else {context.Response.StatusCode=403;await context.Response.WriteAsJsonAsync(new{detail="Studio sign-in is managed by Xur."},lifetime.Token);}
                return;
            }
            if(context.WebSockets.IsWebSocketRequest){await WebSocket(context,token,lifetime);return;}
            await Forward(context,token,lifetime.Token);
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){context.Abort();}
        catch(Exception error) when(error is HttpRequestException or OperationCanceledException or InvalidOperationException or IOException or WebSocketException)
        {
            if(context.Response.HasStarted){context.Abort();return;}
            context.Response.StatusCode=503;context.Response.Headers.RetryAfter="5";
            // Upstream errors may contain credentials; only return fixed guidance.
            await context.Response.WriteAsJsonAsync(new{detail="Unsloth Studio is not ready. Open Unsloth in Xur to check its status or reconnect its account."});
        }
        finally
        {
            lock(sync)
            {
                var value=active[key];
                if(value.Count==1){active.Remove(key);value.Stop.Dispose();}
                else active[key]=(value.Stop,value.Count-1);
            }
        }
    }
    Uri? Manager()=>Uri.TryCreate(managerUrl(),UriKind.Absolute,out var url) && url.Scheme=="https" && url.Port==443 && Uri.CheckHostName(url.Host)==UriHostNameType.Dns?url:null;
    Uri Target(HttpContext context,string token,bool websocket=false)
    {
        // Build from a fixed backend. A //path or an absolute URL supplied by a
        // browser must never turn the proxy into an arbitrary destination.
        var target=new UriBuilder(client.BaseAddress!){Path=context.Request.Path.Value,Query=context.Request.QueryString.Value};
        if(websocket)
        {
            target.Scheme=target.Scheme=="https"?"wss":"ws";
            var query=QueryHelpers.ParseQuery(context.Request.QueryString.Value);
            query.Remove("token");query.Remove("access_token");
            target.Query=QueryString.Create(query.SelectMany(p=>p.Value.Select(v=>new KeyValuePair<string,string?>(p.Key,v))).Append(new("token",token))).Value;
        }
        return target.Uri;
    }
    async Task Forward(HttpContext context,string token,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(new HttpMethod(context.Request.Method),Target(context,token));
        if(context.Request.ContentLength>0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))request.Content=new StreamContent(context.Request.Body);
        var excluded=new HashSet<string>(Excluded,StringComparer.OrdinalIgnoreCase);
        foreach(var name in context.Request.Headers.Connection.ToString().Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries))excluded.Add(name);
        foreach(var header in context.Request.Headers)
            if(!excluded.Contains(header.Key) && !header.Key.StartsWith("Tailscale-",StringComparison.OrdinalIgnoreCase) && !header.Key.StartsWith("X-Forwarded-",StringComparison.OrdinalIgnoreCase) && !header.Key.StartsWith("Sec-WebSocket-",StringComparison.OrdinalIgnoreCase)
                && !request.Headers.TryAddWithoutValidation(header.Key,header.Value.ToArray()))request.Content?.Headers.TryAddWithoutValidation(header.Key,header.Value.ToArray());
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if(response.StatusCode==HttpStatusCode.Unauthorized)await session.Invalidate();
        context.Response.StatusCode=(int)response.StatusCode;
        foreach(var name in response.Headers.Connection)excluded.Add(name);
        excluded.UnionWith(["Content-Security-Policy","Cache-Control","Referrer-Policy","X-Content-Type-Options","Location"]);
        foreach(var header in response.Headers.Concat(response.Content.Headers))
            if(!excluded.Contains(header.Key))context.Response.Headers[header.Key]=header.Value.ToArray();
        if(response.Headers.Location is {} redirect)
        {
            // Only local Studio redirects can cross this authentication boundary.
            var resolved=new Uri(client.BaseAddress!,redirect);
            if(resolved.Authority!=client.BaseAddress!.Authority || resolved.Scheme!=client.BaseAddress.Scheme){context.Response.StatusCode=502;return;}
            context.Response.Headers.Location=resolved.PathAndQuery;
        }
        if(context.Request.Method=="HEAD")return;
        if(response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType=="text/html")
        {
            await response.Content.LoadIntoBufferAsync(2*1024*1024,ct);
            var html=await response.Content.ReadAsStringAsync(ct);
            var head=Regex.Match(html,@"<head(?:\s[^>]*)?>",RegexOptions.IgnoreCase);
            if(!head.Success)throw new InvalidOperationException("Unsupported Studio HTML.");
            html=html.Insert(head.Index+head.Length,"<script src=\"/_xur/session.js\"></script>");
            context.Response.Headers.Remove("ETag");context.Response.Headers.Remove("Content-Encoding");
            await context.Response.WriteAsync(html,ct);
        }
        else
        {
            await using var source=await response.Content.ReadAsStreamAsync(ct);
            var buffer=new byte[16384];int count;
            while((count=await source.ReadAsync(buffer,ct))>0)
            {await context.Response.Body.WriteAsync(buffer.AsMemory(0,count),ct);await context.Response.Body.FlushAsync(ct);}
        }
    }
    async Task WebSocket(HttpContext context,string token,CancellationTokenSource lifetime)
    {
        using var upstream=new ClientWebSocket();
        upstream.Options.Proxy=null;
        upstream.Options.SetRequestHeader("Authorization","Bearer "+token);
        foreach(var protocol in context.WebSockets.WebSocketRequestedProtocols)upstream.Options.AddSubProtocol(protocol);
        await upstream.ConnectAsync(Target(context,token,true),lifetime.Token);
        using var downstream=await context.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol);
        var incoming=Copy(downstream,upstream,lifetime.Token);var outgoing=Copy(upstream,downstream,lifetime.Token);
        await Task.WhenAny(incoming,outgoing);await lifetime.CancelAsync();
        try{await Task.WhenAll(incoming,outgoing);}catch(OperationCanceledException){}
    }
    static async Task Copy(WebSocket from,WebSocket to,CancellationToken ct)
    {
        var buffer=new byte[16384];
        while(!ct.IsCancellationRequested)
        {
            var message=await from.ReceiveAsync(buffer.AsMemory(),ct);
            if(message.MessageType==WebSocketMessageType.Close)
            {await to.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,"Closed",ct);return;}
            await to.SendAsync(buffer.AsMemory(0,message.Count),message.MessageType,message.EndOfMessage,ct);
        }
    }
}
