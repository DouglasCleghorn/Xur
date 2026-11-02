using System.ComponentModel;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ModelContextProtocol.Client;

if(args is ["--verify"])
{
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await using var client=await McpClient.CreateAsync(new StdioClientTransport(new(){Command=Environment.ProcessPath!,Arguments=Path.GetFileNameWithoutExtension(Environment.ProcessPath)=="dotnet"?[typeof(XurTools).Assembly.Location]:[],Name="Xur validation"}),cancellationToken:timeout.Token);
    var tools=await client.ListToolsAsync(cancellationToken:timeout.Token);
    if(!new[]{"xur_catalog","xur_read","xur_write"}.All(name=>tools.Any(t=>t.Name==name)))throw new InvalidOperationException("MCP tool discovery failed.");
    foreach(var path in new[]{"https://evil/api/status","//evil/api/status","/api/../auth","/api/%2e%2e/auth","/api/a\\b","/api/status#fragment"})
    {
        var response=await client.CallToolAsync("xur_read",new Dictionary<string,object?>{{"path",path}},cancellationToken:timeout.Token);
        if(response.IsError!=true)throw new InvalidOperationException("MCP path validation failed.");
    }
    Console.Error.WriteLine("Xur MCP SDK handshake, tool discovery and path validation passed.");
    return;
}
var builder=Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o=>o.LogToStandardErrorThreshold=LogLevel.Trace);
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<XurTools>();
await builder.Build().RunAsync();

[McpServerToolType]
public sealed class XurTools
{
    const int Limit=8*1024*1024;
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public record Connection(string Origin,string ApiKey,string Certificate);

    [McpServerTool(Name="xur_catalog",ReadOnly=true),Description("Discover Xur API routes, data, settings and request schemas. Call first.")]
    public static Task<CallToolResult> Catalog(CancellationToken cancellationToken)=>Request("GET","/api/t3-code/mcp/catalog",null,cancellationToken);

    [McpServerTool(Name="xur_read",ReadOnly=true),Description("Read Xur data or settings using a catalog API path with an optional query string.")]
    public static Task<CallToolResult> Read(string path,CancellationToken cancellationToken)=>Request("GET",path,null,cancellationToken);

    [McpServerTool(Name="xur_write",Destructive=true),Description("Change Xur settings or run an action. Read current values first and preserve revision and approval fields. Requires write permission.")]
    public static Task<CallToolResult> Write(string path,string method,JsonElement? body=null,CancellationToken cancellationToken=default)=>
        method is "POST" or "PUT" or "PATCH" or "DELETE"?Request(method,path,body,cancellationToken):Task.FromResult(Error("Use POST, PUT, PATCH or DELETE."));

    static CallToolResult Result(string text,bool error=false)=>new(){Content=[new TextContentBlock{Text=text}],IsError=error};
    static CallToolResult Error(string text)=>Result(text,true);
    public static bool ValidPath(string path)
    {
        var pathname=path.Split('?')[0];
        return path.StartsWith("/api/",StringComparison.Ordinal)&&!path.Contains('#')&&!path.Any(char.IsControl)
            &&!pathname.Contains('%')&&!pathname.Contains('\\')&&!pathname.Contains("..")&&!pathname.Contains("//");
    }
    static async Task<CallToolResult> Request(string method,string path,JsonElement? body,CancellationToken token)
    {
        if(!ValidPath(path))return Error("Use a local /api/ path from xur_catalog.");
        try
        {
            var config=Environment.GetEnvironmentVariable("XUR_MCP_CONNECTION")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".local/share/xur-mcp/connection.json");
            var connection=JsonSerializer.Deserialize<Connection>(await File.ReadAllTextAsync(config,token),Json)??throw new IOException();
            var origin=new Uri(connection.Origin,UriKind.Absolute);
            if(origin.Scheme!="https"||origin.UserInfo.Length>0||origin.AbsolutePath!="/"||origin.Query.Length>0||origin.Fragment.Length>0)return Error("Reconnect using Xur's HTTPS address.");
            using var trusted=X509Certificate2.CreateFromPem(connection.Certificate);
            using var handler=new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false};
            handler.ServerCertificateCustomValidationCallback=(_,certificate,_,errors)=>
            {
                if(certificate==null||(errors&SslPolicyErrors.RemoteCertificateNameMismatch)!=0)return false;
                using var chain=new X509Chain();
                chain.ChainPolicy.TrustMode=X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(trusted);
                chain.ChainPolicy.RevocationMode=X509RevocationMode.NoCheck;
                return chain.Build(certificate);
            };
            using var client=new HttpClient(handler){Timeout=TimeSpan.FromMinutes(2)};
            using var request=new HttpRequestMessage(new HttpMethod(method),new Uri(origin,path));
            request.Headers.Authorization=new("Bearer",connection.ApiKey);
            if(body.HasValue)request.Content=new StringContent(body.Value.GetRawText(),Encoding.UTF8,"application/json");
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
            using var stream=await response.Content.ReadAsStreamAsync(token);
            using var buffer=new MemoryStream();var chunk=new byte[65536];int count;
            while((count=await stream.ReadAsync(chunk,token))>0)
            {
                if(buffer.Length+count>Limit)return Error("Response exceeds 8 MiB. Narrow the query or use the download API directly.");
                buffer.Write(chunk,0,count);
            }
            var media=response.Content.Headers.ContentType?.ToString()??"";
            var binary=buffer.Length>0&&!media.Contains("json")&&!media.StartsWith("text/");
            return Result(JsonSerializer.Serialize(new{status=(int)response.StatusCode,contentType=media,encoding=binary?"base64":"utf-8",
                body=binary?Convert.ToBase64String(buffer.ToArray()):Encoding.UTF8.GetString(buffer.ToArray())},Json),!response.IsSuccessStatusCode);
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
        catch(Exception e)when(e is IOException or HttpRequestException or JsonException or ArgumentException or System.Security.Cryptography.CryptographicException or OperationCanceledException)
        {return Error("Xur unavailable. Check T3 permissions, connection and certificate; reconnect MCP if needed.");}
    }
}
