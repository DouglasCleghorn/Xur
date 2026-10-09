using Xur.Robot;
var builder=WebApplication.CreateSlimBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options=>options.SerializerOptions.TypeInfoResolverChain.Insert(0,RobotJson.Default));
var socket=Environment.GetEnvironmentVariable("XUR_ROBOT_SOCKET")??"/run/xur/robot-web/app.sock";
if(Environment.GetEnvironmentVariable("XUR_ROBOT_DEV_URL") is {Length:>0} developmentUrl)builder.WebHost.UseUrls(developmentUrl);
else
{
    Directory.CreateDirectory(Path.GetDirectoryName(socket)!);File.Delete(socket);
    builder.WebHost.ConfigureKestrel(options=>options.ListenUnixSocket(socket));
}
var state=Environment.GetEnvironmentVariable("XUR_ROBOT_STATE")??"/state";
Directory.CreateDirectory(state);
var robot=new RoboticsRuntime(state,Path.Combine(state,".build","controller-reservation"));
await robot.Recover();
robot.Start(Environment.GetEnvironmentVariable("XUR_ROBOT_WORKLOAD_ID")??"robot");
var app=builder.Build();
app.UsePathBase("/robot");
app.Use(async(context,next)=>
{
    context.Response.Headers.CacheControl="no-store";
    context.Response.Headers.XContentTypeOptions="nosniff";
    context.Response.Headers.ContentSecurityPolicy="default-src 'self'; img-src 'self' blob: data:; style-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    if(HttpMethods.IsPost(context.Request.Method))
    {
        if(!context.Request.HasJsonContentType())
        {context.Response.StatusCode=415;return;}
        using var body=new MemoryStream();var buffer=new byte[4096];int count;
        while((count=await context.Request.Body.ReadAsync(buffer,context.RequestAborted))!=0)
        {
            if(body.Length+count>16*1024){context.Response.StatusCode=413;return;}
            await body.WriteAsync(buffer.AsMemory(0,count),context.RequestAborted);
        }
        body.Position=0;context.Request.Body=body;
        await next(context);return;
    }
    await next(context);
});
app.MapGet("/health",()=>TypedResults.Json(new RobotHealth("ready","native-aot"),RobotJson.Default.RobotHealth));
app.MapGet("/",()=>TypedResults.PhysicalFile(Path.Combine(app.Environment.ContentRootPath,"wwwroot/index.html"),"text/html"));
app.MapGet("/controller",()=>TypedResults.PhysicalFile(Path.Combine(app.Environment.ContentRootPath,"wwwroot/controller.html"),"text/html"));
app.MapGet("/tags",()=>TypedResults.PhysicalFile(Path.Combine(app.Environment.ContentRootPath,"wwwroot/tags.html"),"text/html"));
app.MapGet("/setup",()=>TypedResults.PhysicalFile(Path.Combine(app.Environment.ContentRootPath,"wwwroot/setup.html"),"text/html"));
app.UseStaticFiles();
app.MapRobotics(robot);
app.Lifetime.ApplicationStopping.Register(()=>robot.Shutdown().GetAwaiter().GetResult());
await app.RunAsync();
