using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xur.Domain;

namespace Xur.Agent;

// An independent listener keeps boot diagnostics reachable when the console host fails.
// Only these fixed routes cross the boundary to the private root sockets.
public static class InstallerDiagnosticsApi
{
    public const int Port=9443;
    public static X509Certificate2 Certificate(string run)
    {
        using var rsa=RSA.Create(3072);
        var request=new CertificateRequest("CN=Xur installer diagnostics",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature,true));
        var names=new SubjectAlternativeNameBuilder();names.AddDnsName("localhost");names.AddDnsName(Environment.MachineName);names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(30));
        // Public certificate only: the private key exists in this process for this boot.
        File.WriteAllText(Path.Combine(run,"diagnostics-cert.pem"),certificate.ExportCertificatePem());
        return certificate;
    }
    public static WebApplication Create(DiagnosticsConfiguration config,X509Certificate2 certificate,HttpClient agent,HttpClient console,
        IPAddress? address=null,int port=Port)
    {
        var builder=WebApplication.CreateSlimBuilder(new WebApplicationOptions{Args=[]});
        builder.Logging.ClearProviders(); // Never log Authorization headers, requests or input text.
        builder.WebHost.ConfigureKestrel(k=>{
            k.Limits.MaxRequestBodySize=4096;k.Limits.MaxConcurrentConnections=16;k.Limits.RequestHeadersTimeout=TimeSpan.FromSeconds(10);
            if(address==null)k.ListenAnyIP(port,o=>o.UseHttps(certificate));
            else k.Listen(address,port,o=>o.UseHttps(certificate));
        });
        var app=builder.Build();
        var expected=SHA256.HashData(Encoding.UTF8.GetBytes("Bearer "+config.ApiKey));
        var requests=new SemaphoreSlim(4,4);
        app.Use(async(context,next)=>{
            context.Response.Headers.CacheControl="no-store";
            context.Response.Headers["X-Content-Type-Options"]="nosniff";
            var header=context.Request.Headers.Authorization;
            if(header.Count!=1 || !CryptographicOperations.FixedTimeEquals(expected,SHA256.HashData(Encoding.UTF8.GetBytes(header.ToString()))))
            {context.Response.StatusCode=401;return;}
            // Browser scripts cannot use this root-level automation interface.
            if(context.Request.Headers.ContainsKey("Origin")){context.Response.StatusCode=403;return;}
            if(!await requests.WaitAsync(0)){context.Response.StatusCode=429;return;}
            try{await next(context);}
            catch(Exception e) when(e is HttpRequestException or TaskCanceledException or IOException or JsonException)
            {if(!context.Response.HasStarted){context.Response.StatusCode=503;await context.Response.WriteAsJsonAsync(new{error="Diagnostic source unavailable; retry status before repeating an action."});}}
            finally{requests.Release();}
        });
        string Sanitize(string text)=>Redaction.Logs(text.Replace(config.ApiKey,"[REDACTED]",StringComparison.Ordinal));
        JsonNode? Clean(JsonNode? value)
        {
            if(value is JsonValue scalar && scalar.TryGetValue<string>(out var text))return JsonValue.Create(Sanitize(text));
            if(value is JsonObject obj)return new JsonObject(obj.Select(p=>KeySecret(p.Key)?new KeyValuePair<string,JsonNode?>(p.Key,JsonValue.Create("[REDACTED]")):new(p.Key,Clean(p.Value))));
            if(value is JsonArray array)return new JsonArray(array.Select(Clean).ToArray());
            return value?.DeepClone();
        }
        async Task<IResult> Proxy(HttpClient client,string path,ConsoleDiagnosticAction? action=null)
        {
            using var response=action==null?await client.GetAsync(path):await client.PostAsJsonAsync(path,action);
            var text=await response.Content.ReadAsStringAsync();
            if(text.Length>2*1024*1024)return Results.Problem("Diagnostic response exceeded the size limit.",statusCode:503);
            return response.Content.Headers.ContentType?.MediaType=="application/json"
                ?Results.Json(Clean(JsonNode.Parse(text)),statusCode:(int)response.StatusCode)
                :Results.Text(Sanitize(text),statusCode:(int)response.StatusCode);
        }
        app.MapGet("/v1/status",async()=>Results.Json(new{
            schemaVersion=1,bundle=ApplicationIdentity.Id,bootId=File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim(),
            uptimeSeconds=double.Parse(File.ReadAllText("/proc/uptime").Split(' ')[0],System.Globalization.CultureInfo.InvariantCulture),
            capturedAt=DateTimeOffset.UtcNow,allowControl=config.AllowControl,
            certificateSha256=certificate.GetCertHashString(HashAlgorithmName.SHA256),
            installer=Clean(JsonNode.Parse(await agent.GetStringAsync("/status")))
        }));
        foreach(var (name,path) in new[]{("logs","/logs"),("installation-logs","/installation-logs"),("display","/diagnostics/display"),("display-history","/diagnostics/display-history"),("hardware","/hardware"),("network","/network/settings"),("wifi","/network/wifi")})
            app.MapGet("/v1/"+name,()=>Proxy(agent,path));
        app.MapGet("/v1/boot-logs",()=>Proxy(agent,"/diagnostics/boot-logs"));
        app.MapGet("/v1/console",()=>Proxy(console,"/local/diagnostics/console"));
        app.MapPost("/v1/console/action",(ConsoleDiagnosticAction action)=>config.AllowControl
            ?Proxy(console,"/local/diagnostics/console/action",action):Task.FromResult(Results.StatusCode(403)));
        return app;
    }
    static bool KeySecret(string key)=>key.Equals("apiKey",StringComparison.OrdinalIgnoreCase)||key.Equals("password",StringComparison.OrdinalIgnoreCase)||key.Equals("psk",StringComparison.OrdinalIgnoreCase)||key.Equals("bootstrapToken",StringComparison.OrdinalIgnoreCase);
}
