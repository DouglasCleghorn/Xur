using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xur.Robot.Backup;
using Xur.Robot.Backups;

var builder=WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("XUR_BACKUP_LISTEN")??"http://0.0.0.0:7081");
builder.WebHost.ConfigureKestrel(options=>options.Limits.MaxRequestBodySize=BackupProtocol.MaxFileBytes);
var root=Environment.GetEnvironmentVariable("XUR_BACKUP_STATE")??"/backup";Directory.CreateDirectory(root);
var secretPath=Environment.GetEnvironmentVariable("XUR_BACKUP_TOKEN_FILE")??"/run/secrets/backup-token";
var secret=File.ReadAllText(secretPath).Trim();
if(!BackupProtocol.Token(secret))throw new InvalidOperationException("Provide a private backup token of 32 to 512 non-whitespace characters.");
var secretDigest=SHA256.HashData(Encoding.UTF8.GetBytes(secret));var storage=new BackupStorage(root);
var app=builder.Build();app.Use(async(context,next)=>
{
    context.Response.Headers.CacheControl="no-store";context.Response.Headers.XContentTypeOptions="nosniff";
    if(context.Request.Path=="/health"){await next(context);return;}
    var authorization=context.Request.Headers.Authorization.ToString();
    if(!authorization.StartsWith("Bearer ",StringComparison.Ordinal)||authorization.Length>520
        ||!CryptographicOperations.FixedTimeEquals(secretDigest,SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..]))))
    {context.Response.StatusCode=401;return;}
    try{await next(context);}
    catch(Exception error) when(error is InvalidOperationException or JsonException)
    {if(context.Response.HasStarted)throw;context.Response.StatusCode=409;await context.Response.WriteAsJsonAsync(new BackupError(error.Message),BackupJson.Default.BackupError);}
});
app.MapGet("/health",()=>TypedResults.Text("ready · native-aot"));
app.MapGet("/api/snapshots/{id}",async(string id,CancellationToken token)=>TypedResults.Json(await storage.Status(id,token),BackupJson.Default.BackupRemoteStatus));
app.MapPost("/api/snapshots/{id}/manifest",async(string id,HttpContext context)=>
{
    if(!context.Request.HasJsonContentType())return Results.StatusCode(415);
    using var body=new MemoryStream();var buffer=new byte[8192];int count;
    while((count=await context.Request.Body.ReadAsync(buffer,context.RequestAborted))!=0)
    {if(body.Length+count>BackupProtocol.MaxManifestBytes)return Results.StatusCode(413);await body.WriteAsync(buffer.AsMemory(0,count),context.RequestAborted);}
    var manifest=BackupJson.Read<BackupManifest>(body.ToArray());
    return (IResult)TypedResults.Json(await storage.Prepare(id,manifest,context.RequestAborted),BackupJson.Default.BackupRemoteStatus);
});
app.MapPut("/api/snapshots/{id}/blobs/{hash}",async(string id,string hash,HttpContext context)=>
{await storage.Upload(id,hash,context.Request.Body,context.RequestAborted);return Results.NoContent();});
app.MapPost("/api/snapshots/{id}/commit",async(string id,HttpContext context)=>
{
    if(!context.Request.HasJsonContentType())return Results.StatusCode(415);
    if(context.Request.ContentLength is >1024)return Results.StatusCode(413);
    using var body=new MemoryStream();var buffer=new byte[256];int count;
    while((count=await context.Request.Body.ReadAsync(buffer,context.RequestAborted))!=0)
    {if(body.Length+count>1024)return Results.StatusCode(413);await body.WriteAsync(buffer.AsMemory(0,count),context.RequestAborted);}
    BackupJson.Read<BackupEmptyRequest>(body.ToArray());
    return (IResult)TypedResults.Json(await storage.Commit(id,context.RequestAborted),BackupJson.Default.BackupRemoteStatus);
});
await app.RunAsync();
