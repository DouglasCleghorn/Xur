using System.Net.Sockets;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, RobotJson.Default));
var socket = Environment.GetEnvironmentVariable("XUR_ROBOT_SOCKET") ?? "/run/xur/robot-web/app.sock";
// TCP is an explicit local development option. Production exposes only a private socket.
if (Environment.GetEnvironmentVariable("XUR_ROBOT_DEV_URL") is { Length: > 0 } developmentUrl)
    builder.WebHost.UseUrls(developmentUrl);
else
{
    Directory.CreateDirectory(Path.GetDirectoryName(socket)!);
    File.Delete(socket);
    builder.WebHost.ConfigureKestrel(options => options.ListenUnixSocket(socket));
}
using var handler = new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    ConnectCallback = async (_, cancellation) =>
    {
        var connection = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await connection.ConnectAsync(new UnixDomainSocketEndPoint(
                Environment.GetEnvironmentVariable("XUR_AGENT_SOCKET") ?? "/run/xur/agent.sock"), cancellation);
            return new NetworkStream(connection, ownsSocket: true);
        }
        catch { connection.Dispose(); throw; }
    }
};
using var agent = new HttpClient(handler) { BaseAddress = new Uri("http://agent"), Timeout = TimeSpan.FromSeconds(90) };
var app = builder.Build();
app.UsePathBase("/robot");
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; img-src 'self' blob: data:; style-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    await next(context);
});
app.MapGet("/health", () => TypedResults.Ok(new RobotHealth("ready", "native-aot")));
app.MapGet("/", () => TypedResults.PhysicalFile(Path.Combine(app.Environment.ContentRootPath, "wwwroot/index.html"), "text/html"));
app.MapGet("/controller", () => TypedResults.PhysicalFile(Path.Combine(app.Environment.ContentRootPath, "wwwroot/controller.html"), "text/html"));
app.MapGet("/tags", () => TypedResults.PhysicalFile(Path.Combine(app.Environment.ContentRootPath, "wwwroot/tags.html"), "text/html"));
app.UseStaticFiles();
// Deliberately not a generic agent proxy: clients cannot choose a host, socket or command.
app.MapMethods("/api/{**path}", ["GET", "POST"], async (HttpContext context, string path) =>
{
    if (!RobotRoutes.Allows(context.Request.Method, path))
    { context.Response.StatusCode = 404; return; }
    using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), "/robotics/" + path);
    if (context.Request.Method == "POST")
    {
        if (!context.Request.HasJsonContentType())
        { context.Response.StatusCode = 415; return; }
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) != 0)
        {
            if (body.Length + count > 16 * 1024) { context.Response.StatusCode = 413; return; }
            await body.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted);
        }
        request.Content = new ByteArrayContent(body.ToArray());
        request.Content.Headers.ContentType = new("application/json");
    }
    try
    {
        using var response = await agent.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
    catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
    {
        if (context.Response.HasStarted) { context.Abort(); return; }
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new RobotError("Xur's robotics agent is unavailable. Check the Robotics profile and agent service."), RobotJson.Default.RobotError);
    }
});
await app.RunAsync();

internal record RobotHealth(string State, string Compilation);
internal record RobotError(string Error);

[JsonSerializable(typeof(RobotHealth))]
[JsonSerializable(typeof(RobotError))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class RobotJson : JsonSerializerContext;

internal static class RobotRoutes
{
    public static bool Allows(string method, string path)
    {
        if (method == "POST") return path is "auto-calibrate" or "start-controller" or "stop" or "estop" or "reset-estop" or "tasks" or "prepare";
        if (method != "GET") return false;
        if (path is "status" or "configuration" or "devices" or "jobs" or "observation" or "calibration-assessment") return true;
        var parts = path.Split('/');
        if (parts.Length < 2 || parts[0] != "jobs" || parts[1].Length != 32 || !parts[1].All(c => char.IsAsciiHexDigit(c) && !char.IsUpper(c))) return false;
        return parts.Length == 2 || parts.Length == 3 && parts[2] is "markers" or "captures"
            || parts.Length == 4 && parts[2] == "captures" && parts[3].EndsWith(".jpg", StringComparison.Ordinal)
                && parts[3][..^4].All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
    }
}
