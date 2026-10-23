using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xur.Agent;
using Xur.Control;
using Xur.Domain;

static class UnslothSsoTests
{
    const string Origin="https://training.example.ts.net:8444";
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(".build/evidence/unsloth-sso/test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            await Sessions(check,root);
            await Proxy(check,root);
            await Sockets(check,root);
        }
        finally{Directory.Delete(root,true);}
    }
    static async Task Sessions(Action<bool,string> check,string root)
    {
        using var studio=new Studio();using var client=new HttpClient(studio){BaseAddress=new Uri(UnslothWorkload.Backend)};
        var path=root+"/credentials/unsloth.json";
        UnslothSession Session()=>new(path,()=>Task.FromResult("bootstrap-fixture"),client);
        var now=DateTimeOffset.UtcNow;var session=new UnslothSession(path,()=>Task.FromResult("bootstrap-fixture"),client,()=>now);var states=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>session.Prepare()));
        check(states.All(s=>s.Ready)&&studio.Changes==1&&!studio.MustChange,"Concurrent startup completes the upstream first-password requirement exactly once");
        check(studio.Password.Length==64&&studio.Password!="bootstrap-fixture"&&File.GetUnixFileMode(path)==(UnixFileMode.UserRead|UnixFileMode.UserWrite),"Managed password is random and persisted with owner-only permissions");
        check(File.GetUnixFileMode(Path.GetDirectoryName(path)!)==(UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute),"Credential directory is private");
        now=now.AddMinutes(3);
        check((await session.Prepare()).Ready&&studio.Refreshes==1&&studio.Changes==1,"Expired cached sessions renew their server-side refresh token without another password change");
        var password=studio.Password;
        check((await Session().Prepare()).Ready&&studio.Changes==1&&studio.Password==password,"Control restart uses the saved password without resetting Studio");
        File.Delete(path);studio.Password="already-changed";studio.MustChange=false;
        check((await Session().Prepare()) is {Ready:false,NeedsPassword:true}&&studio.Password=="already-changed","Existing accounts require their current password and are never silently reset");
        check((await Session().Prepare("already-changed")).Ready&&studio.Password!="already-changed","Explicitly connecting an existing account rotates its password");
        File.Delete(path);studio.Password="bootstrap-fixture";studio.MustChange=true;studio.FailAfterChange=true;
        check(!(await Session().Prepare()).Ready&&File.ReadAllText(path).Contains("pendingPassword"),"A failed reply after password commit retains both sides of the rotation");
        studio.FailAfterChange=false;password=studio.Password;var changes=studio.Changes;
        check((await Session().Prepare()).Ready&&studio.Password==password&&studio.Changes==changes,"Restart recovers an interrupted password rotation using its pending password");
        File.Delete(path);studio.Password="bootstrap-fixture";studio.MustChange=true;studio.FailBeforeChange=true;
        check(!(await Session().Prepare()).Ready,"An upstream password-change failure cannot advertise ready");
        studio.FailBeforeChange=false;
        check((await Session().Prepare()).Ready&&!studio.MustChange,"Rotation recovery also handles failure before Studio commits");
        File.WriteAllText(path,"{");check(!(await Session().Prepare()).Ready,"Corrupt saved credentials fail closed");
        studio.Health=HttpStatusCode.ServiceUnavailable;
        check((await UnslothRuntime.Status(true,client)) is {Running:true,Ready:false},"Agent health checks reject a supervisor-only running container");
        studio.Health=HttpStatusCode.OK;
        check((await UnslothRuntime.Status(true,client)).Ready&&!(await UnslothRuntime.Status(false,client)).Ready,"Agent readiness follows HTTP health and container state");
        check(!ApiKeys.Allows("automation","POST","/api/unsloth/connect"),"Automation API keys cannot connect a shared Studio account");
    }
    static async Task Proxy(Action<bool,string> check,string root)
    {
        using var services=new ServiceCollection().AddLogging().BuildServiceProvider();
        using var studio=new Studio();using var client=new HttpClient(studio){BaseAddress=new Uri(UnslothWorkload.Backend)};
        var session=new UnslothSession(root+"/proxy/unsloth.json",()=>Task.FromResult("bootstrap-fixture"),client);
        var auth=new Bootstrap(signingKey:RandomNumberGenerator.GetBytes(32),directory:root+"/manager");
        var setup=auth.Login("xur",auth.DisplayCode).Session;
        var cookie=auth.CreateAccount(setup,"owner","manager test password").Session!;
        cookie=auth.RememberBrowserSession(cookie)!;
        var proxy=new UnslothProxy(session,auth.AuthorizedBrowser,()=>"https://training.example.ts.net/",client);
        DefaultHttpContext Context(string path="/",string method="GET",string? credential=null)
        {
            var ctx=new DefaultHttpContext{RequestServices=services};ctx.Request.Scheme="https";ctx.Request.Host=new("training.example.ts.net",8444);
            ctx.Request.Path=path;ctx.Request.Method=method;ctx.Response.Body=new MemoryStream();
            if(credential!=null)ctx.Request.Headers.Cookie="xur.session="+credential;
            if(method!="GET")ctx.Request.Headers.Origin=Origin;
            return ctx;
        }
        foreach(var path in new[]{"/","/assets/app.js","/api/chat","/_xur/session.js","/api/auth/refresh"})
        {
            var denied=Context(path);denied.Request.Headers.Authorization="Bearer "+cookie;denied.Request.Headers["Tailscale-User-Login"]="owner";
            await proxy.Handle(denied);check(denied.Response.StatusCode==401,"Studio requires its Xur browser cookie on "+path);
        }
        foreach(var invalid in new[]{setup,"tampered",UnslothProxy.BrowserToken})
        {var ctx=Context(credential:invalid);await proxy.Handle(ctx);check(ctx.Response.StatusCode==401,"Setup, fake UI tokens and forged cookies cannot authenticate Studio");}
        var navigation=Context();navigation.Request.Headers.Accept="text/html";await proxy.Handle(navigation);
        check(navigation.Response.StatusCode==302&&navigation.Response.Headers.Location=="https://training.example.ts.net/login","Signed-out navigation returns to Xur sign-in on its manager port");
        var wrongHost=Context(credential:cookie);wrongHost.Request.Host=new("attacker.example",8444);await proxy.Handle(wrongHost);
        check(wrongHost.Response.StatusCode==400,"An untrusted Host cannot change the proxy origin or redirect target");
        foreach(var origin in new[]{"https://attacker.example","https://training.example.ts.net","null",""})
        {
            var ctx=Context("/api/train","POST",cookie);ctx.Request.Headers.Origin=origin;await proxy.Handle(ctx);
            check(ctx.Response.StatusCode==403,"Cookie mutations reject missing, opaque, cross-site and cross-port origins");
        }
        var page=Context(credential:cookie);page.Request.Headers.Cookie+="; other=private";page.Request.Headers.Authorization="Bearer client-supplied";page.Request.Headers["Tailscale-User-Login"]="owner";
        await proxy.Handle(page);var html=Text(page);
        check(page.Response.StatusCode==200&&html.IndexOf("/_xur/session.js",StringComparison.Ordinal)<html.IndexOf("app.js",StringComparison.Ordinal),"HTML initializes the managed browser session before the Studio module");
        check(studio.LastAuthorization=="Bearer upstream-fixture-token"&&!studio.ForwardedCookie&&!studio.ForwardedIdentity,"Proxy injects its own credential and strips browser cookies and identity headers");
        check(!html.Contains(studio.Password)&&!html.Contains("upstream-fixture-token")&&!page.Response.Headers.ContainsKey("Set-Cookie")&&page.Response.Headers.CacheControl=="no-store","Studio passwords, tokens and cookies never enter the browser response");
        var policy=page.Response.Headers.ContentSecurityPolicy.ToString().Split(';',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)
            .Select(d=>d.Split(' ',StringSplitOptions.RemoveEmptyEntries)).ToDictionary(d=>d[0],d=>d.Skip(1).ToArray());
        check(policy["connect-src"].ToHashSet().SetEquals(["'self'","https://huggingface.co","https://datasets-server.huggingface.co"]),"Studio permits Hugging Face model and dataset APIs without allowing arbitrary remote connections");
        check(policy["script-src"].SequenceEqual(["'self'","'wasm-unsafe-eval'"])&&policy["font-src"].SequenceEqual(["'self'","data:"]),"Hub API access does not enable remote scripts or fonts");
        var script=Context("/_xur/session.js",credential:cookie);await proxy.Handle(script);
        check(Text(script).Contains(UnslothProxy.BrowserToken)&&Text(script).Contains("/_xur/session")&&!Text(script).Contains(studio.Password),"Browser session script contains only opaque UI markers and a sign-out check");
        var claims=JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(UnslothProxy.BrowserToken.Split('.')[1].PadRight((UnslothProxy.BrowserToken.Split('.')[1].Length+3)/4*4,'=')));
        check(claims.GetProperty("sub").GetString()=="unsloth"&&claims.GetProperty("role").GetString()=="owner","Noncredential UI marker supplies the subject and owner display policy expected by Studio");
        var refresh=Context("/api/auth/refresh","POST",cookie);await proxy.Handle(refresh);
        check(refresh.Response.StatusCode==200&&Text(refresh).Contains(UnslothProxy.BrowserToken)&&!Text(refresh).Contains("upstream-fixture-token"),"Studio refresh facade never returns the real upstream token");
        foreach(var path in new[]{"/api/auth/change-password","/api/auth/api-keys","/api/auth/desktop-login"})
        {var ctx=Context(path,"POST",cookie);await proxy.Handle(ctx);check(ctx.Response.StatusCode==403,"Browser cannot escape managed credentials through "+path);}
        foreach(var path in new[]{"/unused/../api/auth/login","/api\\auth\\login","/%61pi/auth/login","/api%2fauth/login"})
        {
            var ctx=Context(path,"POST",cookie);await proxy.Handle(ctx);
            check(ctx.Response.StatusCode==200&&Text(ctx).Contains(UnslothProxy.BrowserToken),"Canonical auth route remains intercepted for "+path);
        }
        var post=Context("/api/upload","POST",cookie);post.Request.ContentType="application/octet-stream";post.Request.Body=new MemoryStream([1,2,3,4]);post.Request.ContentLength=4;await proxy.Handle(post);
        check(post.Response.StatusCode==200&&studio.LastBody.SequenceEqual(new byte[]{1,2,3,4}),"Authenticated uploads retain their bytes");
        var head=Context("/","HEAD",cookie);await proxy.Handle(head);check(head.Response.StatusCode==200&&head.Response.Body.Length==0,"HEAD does not attempt to rewrite an absent HTML body");
        var redirect=Context("/redirect",credential:cookie);await proxy.Handle(redirect);check(redirect.Response.StatusCode==502&&!redirect.Response.Headers.ContainsKey("Location"),"External upstream redirects cannot carry the browser outside the proxy");
        var logout=Context("/api/auth/logout","POST",cookie);await proxy.Handle(logout);
        check(logout.Response.StatusCode==204&&logout.Response.Headers.SetCookie.ToString().Contains("xur.session=;"),"Studio logout clears the shared Xur browser cookie");
    }
    static string Text(DefaultHttpContext context)=>Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
    static WebApplication App()
    {
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));return builder.Build();
    }
    static async Task Sockets(Action<bool,string> check,string root)
    {
        await using var upstream=App();upstream.UseWebSockets();
        var connected=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        upstream.Run(async ctx=>{
            if(ctx.Request.Path=="/stream")
            {
                ctx.Response.ContentType="text/event-stream";await ctx.Response.WriteAsync("data: first\n\n");await ctx.Response.Body.FlushAsync();
                try{await Task.Delay(Timeout.Infinite,ctx.RequestAborted);}catch(OperationCanceledException){}return;
            }
            if(!ctx.WebSockets.IsWebSocketRequest){ctx.Response.StatusCode=404;return;}
            connected.TrySetResult(ctx.Request.Headers.Authorization=="Bearer upstream-fixture-token"&&ctx.Request.Query["token"]=="upstream-fixture-token"&&!ctx.Request.Headers.ContainsKey("Cookie"));
            using var ws=await ctx.WebSockets.AcceptWebSocketAsync();var bytes=new byte[512];
            try{while(true){var read=await ws.ReceiveAsync(bytes.AsMemory(),ctx.RequestAborted);if(read.MessageType==WebSocketMessageType.Close)break;await ws.SendAsync(bytes.AsMemory(0,read.Count),read.MessageType,read.EndOfMessage,ctx.RequestAborted);}}
            catch(Exception e)when(e is WebSocketException or OperationCanceledException){}
        });
        await upstream.StartAsync();
        using var studio=new Studio();using var login=new HttpClient(studio){BaseAddress=new Uri(UnslothWorkload.Backend)};
        using var transport=new HttpClient(new SocketsHttpHandler{UseCookies=false,AllowAutoRedirect=false}){BaseAddress=new Uri(upstream.Urls.Single()),Timeout=Timeout.InfiniteTimeSpan};
        var proxy=new UnslothProxy(new(root+"/socket/unsloth.json",()=>Task.FromResult("bootstrap-fixture"),login),c=>c=="socket-manager",()=>"https://training.example.ts.net/",transport);
        await using var front=App();front.UseWebSockets();front.Run(async ctx=>{ctx.Request.Scheme="https";ctx.Request.Host=new("training.example.ts.net",8444);await proxy.Handle(ctx);});await front.StartAsync();
        try
        {
            var address=new UriBuilder(front.Urls.Single()){Scheme="ws",Path="/api/events",Query="token=browser-marker"}.Uri;
            using var denied=new ClientWebSocket();denied.Options.SetRequestHeader("Origin",Origin);
            bool blocked=false;try{await denied.ConnectAsync(address,CancellationToken.None);}catch(WebSocketException){blocked=true;}
            check(blocked&&!connected.Task.IsCompleted,"Anonymous WebSocket handshake never reaches Studio");
            using var crossOrigin=new ClientWebSocket();crossOrigin.Options.SetRequestHeader("Cookie","xur.session=socket-manager");crossOrigin.Options.SetRequestHeader("Origin","https://attacker.example");
            blocked=false;try{await crossOrigin.ConnectAsync(address,CancellationToken.None);}catch(WebSocketException){blocked=true;}
            check(blocked&&!connected.Task.IsCompleted,"Cross-origin WebSocket handshake is rejected before forwarding");
            using var browser=new ClientWebSocket();browser.Options.SetRequestHeader("Cookie","xur.session=socket-manager");browser.Options.SetRequestHeader("Origin",Origin);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));await browser.ConnectAsync(address,timeout.Token);
            check(await connected.Task.WaitAsync(timeout.Token),"WebSockets receive only server credentials in the header and token query");
            await browser.SendAsync(new byte[]{5,6,7},WebSocketMessageType.Binary,true,timeout.Token);var data=new byte[10];var received=await browser.ReceiveAsync(data.AsMemory(),timeout.Token);
            check(received.Count==3&&data.Take(3).SequenceEqual(new byte[]{5,6,7}),"Authenticated WebSocket frames stream in both directions");
            using var http=new HttpClient{BaseAddress=new Uri(front.Urls.Single())};http.DefaultRequestHeaders.Add("Cookie","xur.session=socket-manager");
            using var response=await http.GetAsync("/stream",HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);var chunk=new byte[64];var count=await stream.ReadAsync(chunk,timeout.Token);
            check(response.Content.Headers.ContentType?.MediaType=="text/event-stream"&&Encoding.UTF8.GetString(chunk,0,count).Contains("data: first"),"Event-stream chunks arrive before the upstream response completes");
            proxy.Revoke("socket-manager");blocked=false;
            bool streamClosed=false;try{streamClosed=await stream.ReadAsync(chunk,timeout.Token)==0;}catch(IOException){streamClosed=true;}
            check(streamClosed,"Xur logout terminates an active HTTP event stream");
            try{var end=await browser.ReceiveAsync(data.AsMemory(),timeout.Token);blocked=end.MessageType==WebSocketMessageType.Close;}catch(WebSocketException){blocked=true;}
            check(blocked,"Xur logout terminates an active Studio WebSocket");
        }
        finally{await front.StopAsync();await upstream.StopAsync();}
    }
    public sealed class Studio:HttpMessageHandler
    {
        public string Password="bootstrap-fixture",LastAuthorization="";
        public bool MustChange=true,FailAfterChange,FailBeforeChange,ForwardedCookie,ForwardedIdentity;
        public int Changes,Refreshes;public byte[] LastBody=[];public HttpStatusCode Health=HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var path=request.RequestUri!.AbsolutePath;
            LastAuthorization=request.Headers.Authorization?.ToString()??"";ForwardedCookie=request.Headers.Contains("Cookie");ForwardedIdentity=request.Headers.Contains("Tailscale-User-Login");
            if(path=="/api/auth/refresh"){Refreshes++;return new(HttpStatusCode.OK){Content=JsonContent.Create(new{access_token="upstream-fixture-token",refresh_token="upstream-fixture-refresh",must_change_password=false})};}
            if(path=="/api/health")return new(Health);
            if(path is "/api/auth/login" or "/api/auth/change-password")
            {
                var json=await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                var supplied=json.GetProperty(path.EndsWith("login")?"password":"current_password").GetString();
                if(supplied!=Password)return new(HttpStatusCode.Unauthorized);
                if(path.EndsWith("change-password"))
                {
                    if(FailBeforeChange)return new(HttpStatusCode.ServiceUnavailable);
                    Password=json.GetProperty("new_password").GetString()!;MustChange=false;Changes++;
                    if(FailAfterChange)return new(HttpStatusCode.ServiceUnavailable);
                }
                return new(HttpStatusCode.OK){Content=JsonContent.Create(new{access_token="upstream-fixture-token",refresh_token="upstream-fixture-refresh",must_change_password=MustChange})};
            }
            if(path=="/redirect")return new(HttpStatusCode.Found){Headers={Location=new Uri("https://attacker.example/")}};
            if(request.Content!=null)LastBody=await request.Content.ReadAsByteArrayAsync(ct);
            var response=new HttpResponseMessage(HttpStatusCode.OK){Content=path=="/"?new StringContent("<!doctype html><html><head><script type=\"module\" src=\"/app.js\"></script></head><body>Studio</body></html>",Encoding.UTF8,"text/html"):new StringContent("OK")};
            response.Headers.Add("Set-Cookie","upstream=private");response.Headers.TryAddWithoutValidation("cache-control","public");return response;
        }
    }
}
