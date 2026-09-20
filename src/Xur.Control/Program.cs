using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Connections;
using System.Security.Claims;
using System.Text.Json;
using Xur.Control;
using Xur.Control.Components;
using Xur.Domain;

return await Xur.Cli.Commands.Invoke(args, Root, RunCommand);

async Task Root()
{
    var run = Environment.GetEnvironmentVariable("XUR_RUN") ?? "/run/xur";
    bool running=false;
    if(File.Exists(Path.Combine(run,"control.sock")))
        try { using var client=LocalClient.Create(Path.Combine(run,"control.sock"));
            running=(await client.GetAsync("/local/status")).IsSuccessStatusCode; } catch { }
    if(!running) { await StartHost(); return; }
    while(true)
    {
        Console.Write(LocalConsole.Menu);
        var choice=Console.ReadLine(); if(choice==null) return;
        if(choice.Trim()=="8") { await InteractiveUpdates();continue; }
        var action=choice.Trim() switch { "1"=>"status", "2"=>"qr", "3"=>"network", "4"=>"hardware", "5"=>"logs", "6"=>"reboot", "7"=>"poweroff", _=>"" };
        if(action.Length>0) await RunCommand(action,false);
        if(choice.Trim()=="1") await RunCommand("login",false);
    }
}

async Task InteractiveUpdates()
{
    while(true)
    {
        Console.Write("\nUpdates\n1. Xur application\n2. Operating system\n0. Back to menu\nSelection: ");
        var choice=Console.ReadLine()?.Trim();if(choice is null or "0")return;
        if(choice is not ("1" or "2"))continue;
        var application=choice=="1";var prefix=application?"application-updates":"updates";
        var actions=application?new[]{"status","check","update","rollback"}:new[]{"status","check","stage","enable","disable","rollback"};
        var labels=application?new[]{"Show status","Check for updates","Update Xur","Roll back Xur"}:new[]{"Show status","Check for updates","Update OS","Enable automatic updates","Pause automatic updates","Roll back OS"};
        while(true)
        {
            Console.Write("\n"+(application?"Xur application":"Operating system")+"\n"+string.Join('\n',labels.Select((label,i)=>(i+1)+". "+label))+"\n0. Back to updates\nSelection: ");
            choice=Console.ReadLine()?.Trim();if(choice==null)return;if(choice=="0")break;
            if(int.TryParse(choice,out var selected) && selected>=1 && selected<=actions.Length)
                await RunCommand(actions[selected-1]=="status"?prefix:prefix+"/"+actions[selected-1],false);
        }
    }
}

async Task RunCommand(string action, bool json)
{
    var run = Environment.GetEnvironmentVariable("XUR_RUN") ?? "/run/xur";
    using var client = LocalClient.Create(Path.Combine(run,"control.sock"));
    var result = action is "qr" or "poweroff" or "reboot" || (action.StartsWith("updates/",StringComparison.Ordinal) || action.StartsWith("application-updates/",StringComparison.Ordinal))
        ? await client.PostAsync("/local/"+action,null) : await client.GetAsync("/local/"+action);
    Console.WriteLine(await result.Content.ReadAsStringAsync());
}
async Task StartHost()
{
    var appliance = new Appliance();
    _ = appliance.Network(); // Observe hardware/network before identity creation and web host startup.
    var identityDirectory=appliance.Installer ? appliance.RunDirectory : "/var/lib/xur";
    var auth = new Bootstrap(signingKey:Bootstrap.LoadSigningKey(identityDirectory),directory:identityDirectory);
    var apiKeys=new ApiKeys(identityDirectory);
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args=Array.Empty<string>(), ContentRootPath=AppContext.BaseDirectory });
    builder.Logging.ClearProviders();
    builder.Services.AddSingleton(appliance); builder.Services.AddSingleton(auth);builder.Services.AddSingleton(apiKeys);
    var catalog=new RecipeCatalog(Environment.GetEnvironmentVariable("XUR_CATALOG") ?? "/usr/share/xur/catalog",Path.Combine(appliance.Installer ? appliance.RunDirectory : "/var/lib/xur","catalog-selected"));
    var profileStore=new ProfileStore(appliance.Installer ? Path.Combine(appliance.RunDirectory,"profiles") : "/var/lib/xur");
    var runtimeClient=LocalClient.Create(Path.Combine(appliance.RunDirectory,"agent.sock"));runtimeClient.Timeout=TimeSpan.FromMinutes(45);
    var gatewayClient=LocalClient.Create(Path.Combine(appliance.RunDirectory,"gateway-admin.sock"));
    var profileManager=new ProfileManager(profileStore,new AgentWorkloadRuntime(runtimeClient),new LocalWorkloadGateway(gatewayClient),catalog,async()=>await runtimeClient.GetFromJsonAsync<StationAccount[]>("/station-users") ?? []);
    builder.Services.AddSingleton(profileManager);builder.Services.AddSingleton(catalog);
    builder.Services.AddResponseCompression(o=>{o.EnableForHttps=true;});
    builder.Services.AddRazorComponents(); builder.Services.AddHttpContextAccessor();
    var formKeys=Path.Combine(identityDirectory,"form-keys");
    Directory.CreateDirectory(formKeys);File.SetUnixFileMode(formKeys,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
    builder.Services.AddDataProtection().SetApplicationName("Xur.Control").PersistKeysToFileSystem(new DirectoryInfo(formKeys));
    builder.Services.AddAntiforgery(o => { o.Cookie.Name = "xur.csrf.https"; o.Cookie.SameSite = SameSiteMode.Strict; o.Cookie.SecurePolicy=CookieSecurePolicy.Always; });
    Directory.CreateDirectory(appliance.RunDirectory);
    File.SetUnixFileMode(appliance.RunDirectory,UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    var socket = Path.Combine(appliance.RunDirectory,"control.sock"); File.Delete(socket);
    var serveSocket = Path.Combine(appliance.RunDirectory,"serve.sock"); File.Delete(serveSocket);
    builder.WebHost.ConfigureKestrel(k => {
        k.ListenAnyIP(appliance.Port);
        k.ListenAnyIP(appliance.Port+363,o=>o.UseHttps(LocalTls.Load(identityDirectory)));
        k.ListenUnixSocket(socket,l=>l.Use(next=>connection=>{connection.Features.Set(new EndpointIdentity("local"));return next(connection);}));
        k.ListenUnixSocket(serveSocket,l=>l.Use(next=>connection=>{connection.Features.Set(new EndpointIdentity("serve"));return next(connection);}));
    });
    var app = builder.Build();
    var maintenance=new ApplicationMaintenance();
    _=ApplicationIdentity.Id;
    app.Use(async(ctx,next)=> {
        if(ctx.Features.Get<EndpointIdentity>()?.Kind=="serve")ctx.Request.Scheme="https";
        if(ctx.Features.Get<EndpointIdentity>()==null&&!ctx.Request.IsHttps)
        {
            var url="https://"+new HostString(ctx.Request.Host.Host,appliance.Port+363)+ctx.Request.PathBase+ctx.Request.Path+ctx.Request.QueryString;
            ctx.Response.Headers.Location=url;
            if(HttpMethods.IsGet(ctx.Request.Method)||HttpMethods.IsHead(ctx.Request.Method))ctx.Response.StatusCode=308;
            else {ctx.Response.StatusCode=426;await ctx.Response.WriteAsJsonAsync(new{error="Use HTTPS for this request.",url});}
            return;
        }
        await next();
    });
    app.UseBrowserOrigin();
    app.UseApiKeys(apiKeys,()=>auth.AccountConfigured);
    app.Use(async (ctx,next) => {
        if(ctx.GetEndpoint()?.Metadata.GetMetadata<PublicStaticAsset>()==null)ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        ctx.Response.Headers["Referrer-Policy"] = "same-origin";
        ctx.Response.Headers.ContentSecurityPolicy = "default-src 'self'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; form-action 'self'; base-uri 'none'";
        bool local = ctx.Features.Get<EndpointIdentity>()?.Kind == "local";
        if (ctx.Request.Path.StartsWithSegments("/local"))
        { if (!local) { ctx.Response.StatusCode = 404; return; } await next(); return; }
        var viaServe = ctx.Features.Get<EndpointIdentity>()?.Kind == "serve"
            && appliance.ConfirmedAdministrator is { Length: >0 } admin
            && ctx.Request.Headers["Tailscale-User-Login"].ToString() == admin;
        // Serve strips caller-supplied identity headers. Only its dedicated private Unix socket is trusted.
        var authorization=ctx.Request.Headers.Authorization.ToString();
        bool bearer=ctx.Items["apiKey"] is ApiKeyInfo || authorization.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase) && auth.Authorized(authorization[7..]);
        var cookie=ctx.Request.Cookies["xur.session"];
        var setupSession=auth.CanSetup(cookie) ? cookie : authorization.StartsWith("Bearer ",StringComparison.Ordinal) && auth.CanSetup(authorization[7..]) ? authorization[7..] : null;
        var authorized = auth.Authorized(cookie) || viaServe && auth.AccountConfigured || bearer;
        bool setupBearer=setupSession!=null && authorization=="Bearer "+setupSession;
        ctx.Items["setupSession"]=setupSession;
        if (authorized) ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name,auth.Username ?? "xur")],"bootstrap"));
        var path = ctx.Request.Path.Value ?? "/";
        bool publicPath=ctx.GetEndpoint()?.Metadata.GetMetadata<PublicStaticAsset>()!=null || path is "/login" or "/auth/login" or "/api/bootstrap" or "/api/auth/login" or "/health" or "/api/status" or "/setup.css" or "/login.js" or "/fonts/IBMPlexSans.ttf" or "/manifest.webmanifest" or "/install-app.js" or "/sw.js" or "/icons/xur-icon.svg" or "/icons/xur-icon-180.png" or "/icons/xur-icon-192.png" or "/icons/xur-icon-512.png";
        bool setupPath=path is "/setup-account" or "/auth/setup" or "/api/auth/setup" or "/account-setup.js";
        if(setupSession!=null && path=="/login") { ctx.Response.Redirect("/setup-account");return; }
        if(!authorized && !publicPath && !(setupSession!=null && setupPath))
        {
            if(ctx.Request.Method=="GET" && !path.StartsWith("/api/"))ctx.Response.Redirect(setupSession!=null?"/setup-account":"/login");
            else if(setupSession!=null) { ctx.Response.StatusCode=403;await ctx.Response.WriteAsJsonAsync(new {error="Create your account first",setupRequired=true}); }
            else ctx.Response.StatusCode=401;
            return;
        }
        if(auth.AccountConfigured && setupPath) { if(ctx.Request.Method=="GET")ctx.Response.Redirect("/");else ctx.Response.StatusCode=409;return; }
        if(!appliance.Installer && ctx.Request.Method=="GET" && path.StartsWith("/install/"))
        { ctx.Response.Redirect("/");return; }
        // JSON token exchange cannot be submitted by an HTML form. Browser cookie
        // mutations still require CSRF; API mutations require an explicit bearer.
        if(path is "/api/bootstrap" or "/api/auth/login" or "/api/auth/setup" && ctx.Request.Method=="POST" && !ctx.Request.HasJsonContentType())
        { ctx.Response.StatusCode=415; return; }
        if (ctx.Request.Method is not ("GET" or "HEAD" or "OPTIONS") && path is not ("/api/bootstrap" or "/api/auth/login") && !((bearer || setupBearer && path=="/api/auth/setup") && (path.StartsWith("/api/") || path.StartsWith("/inference/"))))
        {
            try { await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx); }
            catch (AntiforgeryValidationException) {
                if(path=="/auth/login"){ctx.Response.Redirect("/login?error=refresh");return;}
                ctx.Response.StatusCode=400;await ctx.Response.WriteAsJsonAsync(new{error="This form expired. Refresh the page and try again."});return;
            }
        }
        await next();
    });
    app.Use(async(ctx,next)=> {
        var path=ctx.Request.Path.Value ?? "";
        var tracked=!appliance.Installer && (path.StartsWith("/inference/") || ctx.Request.Method!="GET" && !path.Contains("application-updates") && path!="/auth/login" && path!="/api/bootstrap");
        if(!tracked){await next();return;}
        if(!maintenance.Enter()){ctx.Response.StatusCode=503;ctx.Response.Headers.RetryAfter="5";await ctx.Response.WriteAsync("Application update in progress. Retry shortly.");return;}
        try{await next();}finally{maintenance.Exit();}
    });
    app.MapGet("/local/application-health",()=>Results.Json(new {id=ApplicationIdentity.Id,active=maintenance.Active,profileBusy=profileManager.UpdateBusy}));
    app.UseProtectedCompression(Path.Combine(identityDirectory,"response-spool"));
    app.MapStaticAssets().WithMetadata(new PublicStaticAsset());
    app.UseAntiforgery();
    app.MapGet("/api/page-state/tailscale",async()=>{await appliance.RefreshTailscale();return new {version=PagePolling.Tailscale(appliance)};});
    app.MapGet("/api/page-state/models",async()=>new{version=Canonical.Hash(await appliance.Agent.GetFromJsonAsync<ModelScanStatus>("/models"))});
    app.MapGet("/api/page-state/updates",async()=>new{version=Canonical.Hash(await appliance.Agent.GetFromJsonAsync<UpdateAllStatus>("/update-all"))});
    app.MapApiKeys(apiKeys);
    app.MapPost("/settings/huggingface",async Task<IResult>(HttpContext context)=> {
        var form=await context.Request.ReadFormAsync();
        var response=await appliance.Agent.PostAsJsonAsync("/huggingface",new {token=form["remove"]=="true"?"":form["token"].ToString()});
        return Results.Redirect(response.IsSuccessStatusCode?"/settings?hfSaved=true":"/settings?error=Invalid%20Hugging%20Face%20token.");
    });
    app.MapStationFiles(appliance);
    app.MapGet("/settings/backup",async Task<IResult>()=>{
        using var r=await appliance.Agent.GetAsync("/configuration/export");
        if(!r.IsSuccessStatusCode)return Results.Conflict(new{error="Could not export system settings. No incomplete backup was downloaded."});
        var host=await r.Content.ReadFromJsonAsync<JsonElement>();
        return Results.File(ConfigBackup.Create(await profileManager.ExportProfiles(),host,await profileManager.Stations()),"application/json","xur-config-"+DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+".json");
    });
    app.MapGet("/api/storage/mounts",async()=>{var r=await appliance.Agent.GetAsync("/storage/mounts");return Results.Content(await r.Content.ReadAsStringAsync(),"application/json",statusCode:(int)r.StatusCode);});
    app.MapGet("/api/storage/explore",async(string id,string? path,bool? refresh)=>{var r=await appliance.Agent.GetAsync("/storage/explore?id="+Uri.EscapeDataString(id)+"&path="+Uri.EscapeDataString(path??"")+"&refresh="+(refresh==true?"true":"false"));return Results.Content(await r.Content.ReadAsStringAsync(),"application/json",statusCode:(int)r.StatusCode);});
    app.MapGet("/api/storage/trim",async()=>{var r=await appliance.Agent.GetAsync("/storage/trim");return Results.Content(await r.Content.ReadAsStringAsync(),"application/json",statusCode:(int)r.StatusCode);});
    app.MapPost("/storage/trim",async(HttpContext ctx)=>{var f=await ctx.Request.ReadFormAsync();var r=await appliance.Agent.PostAsJsonAsync("/storage/trim",new TrimRequest(f["id"].ToString()));return Results.Redirect(r.IsSuccessStatusCode?"/storage?trimStarted=true":"/storage?trimError=true");});
    app.MapGet("/api/ntp",async()=> {var r=await appliance.Agent.GetAsync("/ntp");return Results.Content(await r.Content.ReadAsStringAsync(),"application/json",statusCode:(int)r.StatusCode);});
    app.MapPost("/settings/ntp",async(HttpContext ctx)=> {
        var form=await ctx.Request.ReadFormAsync();var servers=form["servers"].ToString().Split(new[]{'\r','\n'},StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        var r=await appliance.Agent.PostAsJsonAsync("/ntp",new NtpRequest(form["enabled"]=="true",servers));
        if(r.IsSuccessStatusCode)return Results.Redirect("/settings?ntpSaved=true");
        var error="Could not save NTP settings.";try{error=(await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()??error;}catch{}
        return Results.Redirect("/settings?error="+Uri.EscapeDataString(error));
    });
    app.MapGet("/api/power/status",async()=>Results.Content(await appliance.Agent.GetStringAsync("/power/status"),"application/json"));
    app.MapGet("/api/timezone",async()=> {var r=await appliance.Agent.GetAsync("/timezone");return Results.Content(await r.Content.ReadAsStringAsync(),"application/json",statusCode:(int)r.StatusCode);});
    app.MapPost("/api/timezone",async(TimezoneRequest request)=> {var r=await appliance.Agent.PostAsJsonAsync("/timezone",request);return Results.Content(await r.Content.ReadAsStringAsync(),"application/json",statusCode:(int)r.StatusCode);});
    app.MapPost("/settings/timezone/refresh",async()=> {
        var r=await appliance.Agent.PostAsync("/timezone/refresh",null);
        return Results.Redirect(r.IsSuccessStatusCode?"/settings?timezoneSaved=true":"/settings?error="+Uri.EscapeDataString("Timezone lookup failed. The existing timezone is retained; check the internet connection and try again."));
    });
    app.MapPost("/settings/timezone",async(HttpContext ctx)=> {
        var form=await ctx.Request.ReadFormAsync();var r=await appliance.Agent.PostAsJsonAsync("/timezone",new TimezoneRequest(form["zone"].ToString(),form["automatic"]=="true"));
        if(r.IsSuccessStatusCode)return Results.Redirect("/settings?timezoneSaved=true");
        var error="Could not save timezone.";try{error=(await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()??error;}catch{}
        return Results.Redirect("/settings?error="+Uri.EscapeDataString(error));
    });
    app.MapGet("/api/models",async()=>await appliance.Agent.GetFromJsonAsync<ModelScanStatus>("/models"));
    app.MapPost("/api/models/scan",async()=>Results.StatusCode((int)(await appliance.Agent.PostAsync("/models/scan",null)).StatusCode));
    app.MapPost("/models/scan",async()=>{await appliance.Agent.PostAsync("/models/scan",null);return Results.Redirect("/models");});
    app.MapGet("/api/network-usage",async(int? minutes,DateTimeOffset? since,HttpResponse response)=>TelemetryDelta.Filter((await appliance.Agent.GetFromJsonAsync<NetworkUsageSnapshot>("/network-usage?minutes="+Math.Clamp(minutes??15,1,1440)))!,since,response));
    app.MapGet("/api/workstations",async()=>WorkstationView.Build(await profileManager.State(),await appliance.Agent.GetFromJsonAsync<StationStreamStatus[]>("/workstations")??[]));
    app.MapGet("/api/workstations/{id}/display",async(string id)=> {var r=await appliance.Agent.GetAsync("/workstations/"+Uri.EscapeDataString(id)+"/display");return Results.Content(await r.Content.ReadAsStringAsync(),"application/json",statusCode:(int)r.StatusCode);});
    app.MapPost("/api/workstations/{id}/display",async(string id,StationDisplayRequest request)=> {var r=await appliance.Agent.PostAsJsonAsync("/workstations/"+Uri.EscapeDataString(id)+"/display",request);return Results.Content(await r.Content.ReadAsStringAsync(),"application/json",statusCode:(int)r.StatusCode);});
    app.MapPost("/workstations/display",async(HttpContext ctx)=> {
        var f=await ctx.Request.ReadFormAsync();
        if(!int.TryParse(f["width"],out var width)||!int.TryParse(f["height"],out var height)||!int.TryParse(f["fps"],out var fps))return Results.BadRequest();
        var r=await appliance.Agent.PostAsJsonAsync("/workstations/"+Uri.EscapeDataString(f["id"].ToString())+"/display",new StationDisplayRequest(width,height,fps));
        if(r.IsSuccessStatusCode)return Results.Redirect("/workstations?displaySaved=true");
        var error="Could not resize the virtual display.";try{error=(await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()??error;}catch{}
        return Results.Redirect("/workstations?error="+Uri.EscapeDataString(error));
    });
    app.MapGet("/api/workstations/{id}/graphics",async(string id)=> {var r=await appliance.Agent.GetAsync("/workstations/"+Uri.EscapeDataString(id)+"/graphics");return Results.Content(await r.Content.ReadAsStringAsync(),"application/json",statusCode:(int)r.StatusCode);});
    app.MapGet("/api/workstations/{id}/pairings",async(HttpContext ctx,string id)=>{
        if(!ctx.Request.IsHttps&&ctx.Features.Get<EndpointIdentity>()?.Kind!="serve")return Results.Json(new{error="Use HTTPS to pair a client."},statusCode:403);
        var r=await appliance.Agent.GetAsync("/workstations/"+Uri.EscapeDataString(id)+"/pairings");return Results.Content(await r.Content.ReadAsStringAsync(),"application/json",statusCode:(int)r.StatusCode);
    });
    app.MapPost("/api/workstations/{id}/pair",async(HttpContext ctx,string id,StationPairRequest request)=>{
        if(!ctx.Request.IsHttps&&ctx.Features.Get<EndpointIdentity>()?.Kind!="serve")return Results.Json(new{error="Use HTTPS to pair a client."},statusCode:403);
        var r=await appliance.Agent.PostAsJsonAsync("/workstations/"+Uri.EscapeDataString(id)+"/pair",request);return Results.Content(await r.Content.ReadAsStringAsync(),"application/json",statusCode:(int)r.StatusCode);
    });
    app.MapPost("/workstations/stream",async(HttpContext ctx)=>{
        var form=await ctx.Request.ReadFormAsync();var r=await appliance.Agent.PostAsync("/workstations/"+Uri.EscapeDataString(form["id"].ToString())+"/stream",null);
        var error=r.IsSuccessStatusCode?null:(await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();return Results.Redirect("/workstations"+(error==null?"":"?error="+Uri.EscapeDataString(error)));
    });
    app.MapPost("/api/workstations/{id}/stream",async(string id)=>{
        using var r=await appliance.Agent.PostAsync("/workstations/"+Uri.EscapeDataString(id)+"/stream",null);
        return Results.Content(await r.Content.ReadAsStringAsync(),"application/json",statusCode:(int)r.StatusCode);
    });
    app.MapPost("/api/workstations/{id}/stream/restart",async(string id)=>{var r=await appliance.Agent.PostAsync("/workstations/"+Uri.EscapeDataString(id)+"/stream/restart",null);return Results.Content(await r.Content.ReadAsStringAsync(),"application/json",statusCode:(int)r.StatusCode);});
    app.MapGet("/api/workstations/{id}/launcher",async Task<IResult>(HttpContext ctx,string id,string platform)=>{
        if(!(await profileManager.State()).Profiles.SelectMany(p=>p.Workloads).Any(w=>w.Id==id&&w.Recipe.Kind=="Workstation"))return Results.NotFound();
        try{var streams=await appliance.Agent.GetFromJsonAsync<StationStreamStatus[]>("/workstations")??[];var file=StationLauncher.Create(ctx.Request.Host.Host,platform,streams.SingleOrDefault(s=>s.Id==id)?.Port??47989);return Results.File(System.Text.Encoding.UTF8.GetBytes(file.Text),"application/octet-stream",file.Name);}catch(InvalidOperationException e){return Results.BadRequest(new{error=e.Message});}
    });
    app.MapGet("/api/gpu-power",async()=>{var r=await appliance.Agent.GetAsync("/gpu-power");return Results.Content(await r.Content.ReadAsStringAsync(),"application/json",statusCode:(int)r.StatusCode);});
    app.MapPost("/api/gpu-power",async(GpuPowerRequest request)=> {
        var r=await appliance.Agent.PostAsJsonAsync("/gpu-power",request);
        return Results.Content(await r.Content.ReadAsStringAsync(),"application/json",statusCode:(int)r.StatusCode);
    });
    app.MapPost("/gpus/power/save",async(HttpContext ctx)=> {
        var f=await ctx.Request.ReadFormAsync();double? watts=null;
        if(f["reset"]!="true") {
            if(!double.TryParse(f["watts"],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var value)||!double.IsFinite(value))return Results.Redirect("/gpus/power?error=Enter+a+valid+power+limit.");
            watts=value;
        }
        var r=await appliance.Agent.PostAsJsonAsync("/gpu-power",new GpuPowerRequest(f["pci"].ToString(),f["identity"].ToString(),watts));
        if(r.IsSuccessStatusCode)return Results.Redirect("/gpus/power?saved=true");
        var message="Could not change the power limit.";
        try{message=JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString()??message;}catch{}
        return Results.Redirect("/gpus/power?error="+Uri.EscapeDataString(message));
    });
    app.MapGet("/health",()=>Results.Json(new { status = "ok" }));
    app.MapGet("/api/status",()=>Results.Json(new { mode = appliance.Installer ? "Installer" : "Installed", bootId=appliance.BootId }));
    app.MapPost("/api/bootstrap",(ApiBootstrapRequest request)=> {
        var result=auth.Login("xur",request.Token ?? "");
        return result.Session is { } session ? Results.Json(new { accessToken=session, tokenType="Bearer", expiresIn=28800, setupRequired=true })
            : Results.Json(new { error=result.Status==503 ? "Setup is starting" : result.Status==429 ? "Wait 30 seconds before retrying" : "Invalid or expired token" },statusCode:result.Status);
    });
    app.MapPost("/api/install/plan",async (ApiPlanRequest request)=> {
        var result=await appliance.Agent.PostAsJsonAsync("/plan",new {path=request.Path});
        return Results.Content(await result.Content.ReadAsStringAsync(),"application/json",statusCode:(int)result.StatusCode);
    });
    app.MapPost("/api/install/approve",async (Approval request)=> {
        var result=await appliance.Agent.PostAsJsonAsync("/approve",request);
        return Results.Content(await result.Content.ReadAsStringAsync(),"application/json",statusCode:(int)result.StatusCode);
    });
    app.MapPost("/api/power/reboot",async()=>Results.StatusCode((int)(await appliance.Agent.PostAsync("/power/reboot",null)).StatusCode));
    void SetSession(HttpContext ctx,string session)=>ctx.Response.Cookies.Append("xur.session",session,new CookieOptions { HttpOnly=true,SameSite=SameSiteMode.Strict,Secure=true,MaxAge=TimeSpan.FromHours(8),Path="/" });
    app.MapPost("/auth/login",async (HttpContext ctx) => {
        var form=await ctx.Request.ReadFormAsync();
        var result=auth.AccountConfigured ? auth.PasswordLogin(form["username"].ToString(),form["password"].ToString()) : auth.Login("xur",form["code"].ToString());
        if(result.Session==null)return Results.Redirect("/login?error="+(result.Status==429?"limited":"invalid"));
        SetSession(ctx,result.Session);return Results.Redirect(auth.AccountConfigured?"/":"/setup-account");
    });
    app.MapPost("/auth/setup",async(HttpContext ctx)=> {
        var form=await ctx.Request.ReadFormAsync();
        var result=auth.CreateAccount(ctx.Items["setupSession"] as string,form["username"].ToString(),form["password"].ToString());
        if(result.Session==null)return Results.Redirect("/setup-account?error="+Uri.EscapeDataString(result.Error ?? "Could not create account."));
        SetSession(ctx,result.Session);LocalConsole.Refresh();return Results.Redirect("/");
    });
    app.MapPost("/api/auth/setup",(HttpContext ctx,ApiAccountRequest request)=> {
        var result=auth.CreateAccount(ctx.Items["setupSession"] as string,request.Username ?? "",request.Password ?? "");
        if(result.Session==null)return Results.Json(new {error=result.Error},statusCode:result.Status);
        LocalConsole.Refresh();return Results.Json(new {accessToken=result.Session,tokenType="Bearer",expiresIn=28800,setupRequired=false});
    });
    app.MapPost("/api/auth/login",(ApiAccountRequest request)=> {
        var result=auth.PasswordLogin(request.Username ?? "",request.Password ?? "");
        return result.Session is { } session ? Results.Json(new {accessToken=session,tokenType="Bearer",expiresIn=28800,setupRequired=false})
            : Results.Json(new {error=result.Status==429?"Wait 30 seconds before retrying":"Invalid username or password"},statusCode:result.Status);
    });
    app.MapPost("/auth/logout",(HttpContext ctx)=> { ctx.Response.Cookies.Delete("xur.session",new CookieOptions {Path="/"});return Results.Redirect("/login"); });
    app.MapGet("/api/diagnostics/display",async()=> {
        var report=System.Text.Json.Nodes.JsonNode.Parse(await appliance.Agent.GetStringAsync("/diagnostics/display"))!.AsObject();
        report["controlBundle"]=ApplicationIdentity.Id;
        report["mode"]=appliance.Installer?"Installer":"Installed";
        return Results.Json(report);
    });
    app.MapGet("/api/system",async()=>await appliance.Agent.GetFromJsonAsync<SystemSnapshot>("/system"));
    app.MapPost("/runtime/action",async(HttpContext ctx)=> {
        if(appliance.Installer)return Results.Conflict();
        var form=await ctx.Request.ReadFormAsync();
        var result=await appliance.Agent.PostAsJsonAsync("/runtime/action",new ServiceAction(form["name"].ToString(),form["action"].ToString()));
        return result.IsSuccessStatusCode ? Results.Redirect("/") : Results.Content(await result.Content.ReadAsStringAsync(),"application/json",statusCode:(int)result.StatusCode);
    });
    app.MapGet("/api/disks",async()=> await appliance.Agent.GetFromJsonAsync<Inventory>("/disks"));
    app.MapGet("/api/logs",async()=> Results.Text(await appliance.Agent.GetStringAsync("/logs")));
    app.MapGet("/api/installer",async()=> Results.Json(await appliance.AgentStatus()));
    app.MapPost("/tailscale/start",(HttpContext ctx)=> {
        if(!auth.Authorized(ctx.Request.Cookies["xur.session"])) return Results.StatusCode(403);
        appliance.StartWebLogin(); return Results.Redirect("/tailscale");
    });
    app.MapPost("/tailscale/confirm",async()=> { await appliance.ConfirmAdmin(); return Results.Redirect("/tailscale"); });
    app.MapPost("/install/plan",async (HttpContext ctx) => {
        var form = await ctx.Request.ReadFormAsync();
        var result = await appliance.Agent.PostAsJsonAsync("/plan",new { path=form["path"].ToString() });
        if (!result.IsSuccessStatusCode) return Results.Content(await result.Content.ReadAsStringAsync(),"application/json",statusCode:(int)result.StatusCode);
        var plan = await result.Content.ReadFromJsonAsync<InstallPlan>();
        appliance.Plan = plan;
        return Results.Redirect("/install/review");
    });
    app.MapPost("/install/approve",async (HttpContext ctx) => {
        var f = await ctx.Request.ReadFormAsync();
        var result = await appliance.Agent.PostAsJsonAsync("/approve",new Approval(f["id"].ToString(),f["digest"].ToString()));
        return result.IsSuccessStatusCode ? Results.Redirect("/install/progress") : Results.Content(await result.Content.ReadAsStringAsync(),"application/json",statusCode:(int)result.StatusCode);
    });
    app.MapPost("/power/reboot",async()=> {
        var state=await appliance.AgentStatus();
        if(state is not { } s)return Results.StatusCode(503);
        if(s.TryGetProperty("operation",out var operation) && operation.ValueKind==JsonValueKind.Object && operation.GetProperty("stage").GetString()=="Installing")return Results.Conflict();
        _=Task.Run(async()=> { await Task.Delay(1000);try { await appliance.Agent.PostAsync("/power/reboot",null); } catch { } });
        return Results.Redirect("/reboot?boot="+appliance.BootId);
    });
    app.MapGet("/local/console-frame",(int? columns,int? rows)=>Results.Text(LocalConsole.ExportFrame(columns??100,rows??40),"text/plain; charset=utf-8"));
    app.MapGet("/local/status",()=>Results.Text(JsonSerializer.Serialize(new { urls=appliance.Urls(),tailscale=appliance.TailscaleState, diskWrites="ApprovalRequired" })));
    app.MapGet("/local/network",()=>Results.Json(appliance.Network()));
    app.MapGet("/local/login",()=>Results.Text(auth.AccountConfigured ? "User: "+auth.Username+"\nSign in with your username and password." : "User: xur\nAccess code: "+auth.DisplayCode));
    app.MapGet("/local/tailscale",()=>Results.Json(new { state=appliance.TailscaleState, url=appliance.TailUrl }));
    app.MapGet("/local/hardware",async()=>Results.Text(await appliance.Agent.GetStringAsync("/hardware")));
    app.MapGet("/local/logs",async()=>Results.Text(await appliance.Agent.GetStringAsync("/logs")));
    app.MapPost("/local/qr",()=>{ appliance.StartQr();return Results.Text("Real QR enrollment started on local and serial consoles"); });
    app.MapPost("/local/{action}",async(string action)=>{ if(action is not ("reboot" or "poweroff")) return Results.BadRequest(); return Results.StatusCode((int)(await appliance.Agent.PostAsync("/power/"+action,null)).StatusCode); });
    app.MapProfiles(appliance,profileManager,catalog);
    app.MapUpdates(appliance);
    var gatewayData=LocalClient.Create(Path.Combine(appliance.RunDirectory,"gateway.sock"));gatewayData.Timeout=Timeout.InfiniteTimeSpan;
    app.MapModelLab(appliance,profileManager,gatewayData,gatewayClient,maintenance);
    app.Map("/inference/{**path}",async(HttpContext c,string? path)=> {
        if(appliance.Installer){c.Response.StatusCode=409;return;}
        await StreamProxy.Forward(c,gatewayData,"http://localhost/"+(path??"")+c.Request.QueryString);
    });
    app.MapRazorComponents<App>();
    await app.StartAsync();
    if(!appliance.Installer && !File.Exists(ApplicationMaintenance.Marker))await profileManager.Resume(automatic:true);
    File.SetUnixFileMode(socket,UnixFileMode.UserRead|UnixFileMode.UserWrite);
    File.SetUnixFileMode(serveSocket,UnixFileMode.UserRead|UnixFileMode.UserWrite);
    await LocalConsole.Start(appliance,auth);
    if(appliance.Installer) _ = Task.Run(async()=> {
        while(!auth.Configured && !app.Lifetime.ApplicationStopping.IsCancellationRequested)
        {
            try {
                var config=await appliance.Agent.GetFromJsonAsync<BootstrapConfiguration>("/bootstrap-config");
                if(config!=null && config.State!="Starting") { auth.Initialize(config.Token); LocalConsole.Refresh(); break; }
            } catch { /* Local login stays available; storage approval remains gated by the agent. */ }
            await Task.Delay(1000);
        }
    });
    app.Lifetime.ApplicationStopping.Register(LocalConsole.Stop);
    async Task ShowUpdates(bool refreshOnly=false)
    {
        if(appliance.Installer){LocalConsole.OpenUpdates(null,"OS updates are available after installation.");return;}
        try {
            var status=await appliance.Agent.GetFromJsonAsync<OsUpdateStatus>("/updates");
            if(!refreshOnly || LocalConsole.ViewingUpdates)LocalConsole.OpenUpdates(status);
        }
        catch { if(!refreshOnly || LocalConsole.ViewingUpdates)LocalConsole.OpenUpdates(null,"Could not load update status. Check the web manager or logs."); }
    }
    async Task Input(TextReader input, bool physical)
    {
        var keys=new ConsoleKeyReader();var buffer=new char[1];Task<int>? read=null;
        while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
        {
            char? key;
            if(Environment.GetEnvironmentVariable("XUR_CONSOLE")=="stdio")
            { var line=await input.ReadLineAsync(); if(line==null)break;key=LocalConsole.Command(line); }
            else
            {
                read??=input.ReadAsync(buffer,0,1);
                ConsoleKeyAction action;
                // Keep the pending read alive while distinguishing Esc from arrow-key sequences.
                if(keys.AwaitingEscape && await Task.WhenAny(read,Task.Delay(100))!=read)
                    action=keys.FlushEscape();
                else {
                    if(await read==0)break;
                    read=null;action=keys.Read(buffer[0]);
                }
                if(action==ConsoleKeyAction.None)continue;
                key=LocalConsole.Navigate(action);
            }
            if(key==null)continue;
            try
            {
                switch(key.Value)
                {
                    case '0':
                        if(LocalConsole.ViewingUpdates || LocalConsole.ViewingApplicationUpdates)LocalConsole.OpenUpdateMenu();else LocalConsole.Status(appliance,auth);break;
                    case '1': LocalConsole.Status(appliance,auth); break;
                    case '3': LocalConsole.OpenNetwork(); break;
                    case '2': appliance.StartQr(); break;
                    case '4': LocalConsole.Show("Hardware",await appliance.Agent.GetStringAsync("/hardware")); break;
                    case 'h':
                        LocalConsole.OpenApplicationUpdates(await appliance.Agent.GetFromJsonAsync<ApplicationUpdateStatus>("/application-updates"));break;
                    case 'e': case 'f': case 'g':
                        if(!LocalConsole.ViewingApplicationUpdates)break;
                        await appliance.Agent.PostAsJsonAsync("/application-updates",new ApplicationUpdateRequest(key.Value switch {'e'=>"check",'f'=>"update",_=>"rollback"}));
                        LocalConsole.OpenApplicationUpdates(await appliance.Agent.GetFromJsonAsync<ApplicationUpdateStatus>("/application-updates"));break;
                    case '8': case 'u': LocalConsole.OpenUpdateMenu();break;
                    case 'o': await ShowUpdates(); break;
                    case 'c': case 'd': case 'a': case 'b':
                        if(!LocalConsole.ViewingUpdates)break;
                        var updateAction=key.Value switch { 'c'=>"check",'d'=>"stage",'b'=>"rollback",_=> (await appliance.Agent.GetFromJsonAsync<OsUpdateStatus>("/updates"))!.Automatic ? "disable" : "enable" };
                        var updateResult=await appliance.Agent.PostAsJsonAsync("/updates",new OsUpdateAction(updateAction));
                        if(!updateResult.IsSuccessStatusCode)LocalConsole.OpenUpdates(null,"Action unavailable. Check update status in the web manager.");
                        else await ShowUpdates();
                        break;
                    case '5':
                        LocalConsole.UpdateLogs(await appliance.Agent.GetStringAsync("/console-logs")); LocalConsole.OpenLogs();
                        if(physical)await Processes.Run("chvt",["2"]);
                        break;
                    case 'n': LocalConsole.Page(1); break;
                    case 'p': LocalConsole.Page(-1); break;
                    case '6': await appliance.Agent.PostAsync("/power/reboot",null); break;
                    case '7': await appliance.Agent.PostAsync("/power/poweroff",null); break;
                }
            }
            catch { LocalConsole.Show("Action unavailable","The web setup remains running. Press 0 for the menu."); }
        }
    }
    if(Environment.GetEnvironmentVariable("XUR_CONSOLE") == "stdio") _ = Task.Run(()=>Input(Console.In,false));
    else foreach(var path in new[]{"/dev/tty3","/dev/ttyS0"}) _ = Task.Run(async()=>{
        try { using var input = new StreamReader(LocalConsole.OpenDevice(path,FileAccess.Read)); await Input(input,path=="/dev/tty3"); } catch { }
    });
    _ = Task.Run(async()=>{
        while(!app.Lifetime.ApplicationStopping.IsCancellationRequested)
        {
            await Task.Delay(500);LocalConsole.Refresh();
        }
    });
    // Network enrollment must not wait behind journal or update requests.
    _ = Task.Run(async()=>{
        while(!app.Lifetime.ApplicationStopping.IsCancellationRequested)
        {
            await appliance.RefreshTailscale();
            LocalConsole.Refresh();
            await Task.Delay(appliance.TailscaleState=="Running"?3000:1000);
        }
    });
    _ = Task.Run(async()=>{
        while(!app.Lifetime.ApplicationStopping.IsCancellationRequested)
        {
            await Task.Delay(5000);
            if(LocalConsole.ViewingUpdates)await ShowUpdates(refreshOnly:true);
            if(LocalConsole.ViewingApplicationUpdates)try {LocalConsole.OpenApplicationUpdates(await appliance.Agent.GetFromJsonAsync<ApplicationUpdateStatus>("/application-updates"),refreshOnly:true);}catch{}
            try { LocalConsole.UpdateLogs(await appliance.Agent.GetStringAsync("/console-logs")); } catch { }
        }
    });
    await app.WaitForShutdownAsync();
}
record EndpointIdentity(string Kind);
record ApiBootstrapRequest(string? Token);
record ApiPlanRequest(string Path);
record BootstrapConfiguration(string State,string? Token);

record ApiAccountRequest(string? Username,string? Password);
