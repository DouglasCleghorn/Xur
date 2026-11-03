using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xur.Control;
using Xur.Domain;

static class T3McpTests
{
    public static async Task Run(Action<bool,string> check)
    {
        using(var rsa=RSA.Create(2048))
        {
            var request=new CertificateRequest("CN=Xur",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            var names=new SubjectAlternativeNameBuilder();names.AddDnsName("xur-test");names.AddIpAddress(IPAddress.Loopback);
            var lan=IPAddress.Parse("192.0.2.10");var v6=IPAddress.Parse("2001:db8::10");
            names.AddIpAddress(v6);names.AddIpAddress(lan);request.CertificateExtensions.Add(names.Build());
            using var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1));
            check(T3Mcp.ContainerOrigin(cert,[IPAddress.Loopback,v6,lan],8443)=="https://192.0.2.10:8443","Container MCP uses a certificate-covered live host IP and direct TLS port without MagicDNS");
            check(T3Mcp.ContainerOrigin(cert,[v6],9443)=="https://[2001:db8::10]:9443","Container MCP handles IPv6 and custom manager ports");
            try{T3Mcp.ContainerOrigin(cert,[IPAddress.Loopback,IPAddress.Parse("192.0.2.11")],8443);throw new Exception("Accepted an uncertified address");}
            catch(InvalidOperationException){check(true,"Container MCP rejects stale certificate addresses and loopback without disabling TLS checks");}
        }
        var root=Path.GetFullPath(".build/evidence/t3-mcp-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var keys=new ApiKeys(root);var key=keys.Create(new("fixture","t3",0));
            var permissions=new T3StorageConfig();bool unavailable=false;int calls=0;
            var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();
            await using var app=builder.Build();
            app.UseApiKeys(keys,()=>true,()=>unavailable?Task.FromException<T3StorageConfig>(new IOException()):Task.FromResult(permissions));
            app.Run(context=>{calls++;context.Response.StatusCode=204;return Task.CompletedTask;});
            var pipeline=((IApplicationBuilder)app).Build();
            async Task<int> Request(string method)
            {
                var context=new DefaultHttpContext{RequestServices=app.Services};
                context.Request.Method=method;context.Request.Path="/api/system";
                context.Request.Headers.Authorization="Bearer "+key.Token;
                context.Response.Body=new MemoryStream();await pipeline(context);return context.Response.StatusCode;
            }
            check(await Request("GET")==403 && calls==0,"MCP disabled at API boundary");
            permissions=permissions with{McpRead=true};
            check(await Request("GET")==204 && await Request("POST")==403,"MCP read permission cannot mutate");
            permissions=permissions with{McpWrite=true};
            check(await Request("POST")==204,"MCP write permission reaches existing handler");
            permissions=new(FullHostAccess:true);
            check(await Request("POST")==204,"Full host access grants MCP actions");
            permissions=new();
            check(await Request("GET")==403,"Permission removal takes effect without replacing key");
            unavailable=true;
            check(await Request("GET")==503,"Permission outage fails closed");
            unavailable=false;permissions=new(FullHostAccess:true);keys.Revoke(key.Key.Id);
            check(await Request("GET")==401,"Revoked MCP key cannot use full host permissions");
        }
        finally{Directory.Delete(root,true);}
        foreach(var host in new[]{false,true})
        foreach(var read in new[]{false,true})
        foreach(var write in new[]{false,true})
        {
            var settings=new T3StorageConfig(FullHostAccess:host,McpRead:read,McpWrite:write);
            check(T3Mcp.Allowed(settings,"GET")== (host||read||write),$"MCP read {host}/{read}/{write}");
            check(T3Mcp.Allowed(settings,"POST")== (host||write),$"MCP write {host}/{read}/{write}");
        }
        check(ApiKeys.Allows("t3","GET","/api/system"),"T3 MCP reads system API");
        check(ApiKeys.Allows("t3","POST","/api/profiles"),"T3 MCP uses existing settings API");
        foreach(var path in new[]{"/api/api-keys","/api/t3-code/mcp/connect","/api/t3-code/storage","/api/t3-code/setup","/api/auth/login","/api/install/start","/local/status","/api/../auth"})
            check(!ApiKeys.Allows("t3","POST",path),"T3 MCP cannot escalate: "+path);

        var directory=Path.GetFullPath(".build/evidence/t3-mcp/"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var keys=new ApiKeys(directory);var created=keys.Create(new("MCP test","t3",0));
            var permissions=new T3StorageConfig(McpRead:true);bool unavailable=false;int writes=0;
            var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();
            await using var app=builder.Build();app.Urls.Add("http://127.0.0.1:0");
            app.UseApiKeys(keys,()=>true,()=>unavailable?throw new IOException("offline"):Task.FromResult(permissions));
            app.MapGet("/api/system",()=>Results.Ok(new{status="ready"}));
            app.MapPost("/api/timezone",()=>{writes++;return Results.Ok();});
            await app.StartAsync();
            using var client=new HttpClient(){BaseAddress=new Uri(app.Urls.Single())};
            client.DefaultRequestHeaders.Authorization=new("Bearer",created.Token);
            check((await client.GetAsync("/api/system")).StatusCode==HttpStatusCode.OK,"MCP key reads through auth middleware");
            check((await client.PostAsync("/api/timezone",null)).StatusCode==HttpStatusCode.Forbidden&&writes==0,"Read-only MCP cannot reach mutation handler");
            permissions=permissions with{McpWrite=true};
            check((await client.PostAsync("/api/timezone",null)).StatusCode==HttpStatusCode.OK&&writes==1,"Permission changes apply to existing credentials");
            permissions=new();
            check((await client.GetAsync("/api/system")).StatusCode==HttpStatusCode.Forbidden,"Disabling access blocks existing MCP key");
            permissions=permissions with{FullHostAccess=true};
            check((await client.PostAsync("/api/timezone",null)).StatusCode==HttpStatusCode.OK&&writes==2,"Host access overrides disabled MCP flags");
            unavailable=true;
            check((await client.GetAsync("/api/system")).StatusCode==HttpStatusCode.ServiceUnavailable,"Permission outage fails closed");
            unavailable=false;keys.Revoke(created.Key.Id);
            check((await client.GetAsync("/api/system")).StatusCode==HttpStatusCode.Unauthorized,"Revocation blocks an existing MCP connection");
            await app.StopAsync();
        }
        finally{Directory.Delete(directory,true);}
    }
}
