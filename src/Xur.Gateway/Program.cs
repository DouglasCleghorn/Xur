using Xur.Domain;
using Xur.Gateway;
var builder=WebApplication.CreateBuilder(args);builder.Logging.ClearProviders();
var run=Environment.GetEnvironmentVariable("XUR_RUN") ?? "/run/xur";
var state=Environment.GetEnvironmentVariable("XUR_GATEWAY_STATE") ?? "/var/lib/xur";
Directory.CreateDirectory(run);File.SetUnixFileMode(run,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
var admin=Path.Combine(run,"gateway-admin.sock");var data=Path.Combine(run,"gateway.sock");File.Delete(admin);File.Delete(data);
builder.WebHost.ConfigureKestrel(k=> {
    k.ListenUnixSocket(admin,l=>l.Use(next=>c=> {c.Features.Set(new GatewayAdmin());return next(c);}));
    k.ListenUnixSocket(data);
});
var app=builder.Build();_=ApplicationIdentity.Id;var routes=new RouteTable(state);
using var backend=new HttpClient(new SocketsHttpHandler {AllowAutoRedirect=false,UseCookies=false}) {Timeout=Timeout.InfiniteTimeSpan};
app.MapGet("/health",(HttpContext c)=>c.Features.Get<GatewayAdmin>()!=null ? Results.Json(new {status="ok",id=ApplicationIdentity.Id}) : Results.NotFound());
app.MapGet("/routes",(HttpContext c)=>c.Features.Get<GatewayAdmin>()!=null ? Results.Json(routes.Snapshot()) : Results.NotFound());
app.MapPost("/routes",(HttpContext c,BackendRoute[] next)=> {
    if(c.Features.Get<GatewayAdmin>()==null)return Results.NotFound();
    try {routes.Publish(next);return Results.Ok();}catch(InvalidOperationException){return Results.BadRequest();}
});
app.MapPost("/drain",async(HttpContext c,DrainRequest request)=> {
    if(c.Features.Get<GatewayAdmin>()==null)return Results.NotFound();
    return await routes.Drain(request.Id,TimeSpan.FromSeconds(60),c.RequestAborted) ? Results.Ok() : Results.Conflict();
});
app.Map("/{route}/{**path}",async(HttpContext c,string route,string? path)=> {
    if(c.Features.Get<GatewayAdmin>()!=null){c.Response.StatusCode=404;return;}
    using var lease=routes.Acquire(route);
    if(lease==null){c.Response.StatusCode=503;await c.Response.WriteAsync("Workload is unavailable or draining.");return;}
    if(c.Request.Headers.TryGetValue("X-Xur-Expected-Workload",out var expectedWorkload) && expectedWorkload!=lease.Route.WorkloadId || c.Request.Headers.TryGetValue("X-Xur-Expected-Endpoint",out var expectedEndpoint) && expectedEndpoint!=lease.Route.Endpoint)
    {c.Response.StatusCode=409;await c.Response.WriteAsync("The selected workload changed.");return;}
    c.Request.Headers.Remove("X-Xur-Expected-Workload");c.Request.Headers.Remove("X-Xur-Expected-Endpoint");
    await StreamProxy.Forward(c,backend,lease.Route.Endpoint.TrimEnd('/')+"/"+(path??"")+c.Request.QueryString);
});
await app.StartAsync();foreach(var path in new[]{admin,data})File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);
await app.WaitForShutdownAsync();
sealed record GatewayAdmin;
sealed record DrainRequest(string Id);
