using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xur.Control;
using Xur.Domain;

static class NetworkShareMcpTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(".build/evidence/smb-mcp-"+Guid.NewGuid().ToString("N")[..8]);Directory.CreateDirectory(root);
        var oldRun=Environment.GetEnvironmentVariable("XUR_RUN");var oldMode=Environment.GetEnvironmentVariable("XUR_MODE");
        Environment.SetEnvironmentVariable("XUR_RUN",root);Environment.SetEnvironmentVariable("XUR_MODE","Installed");
        try
        {
            var agentBuilder=WebApplication.CreateBuilder();agentBuilder.Logging.ClearProviders();agentBuilder.WebHost.ConfigureKestrel(k=>k.ListenUnixSocket(root+"/agent.sock"));
            await using var agent=agentBuilder.Build();int writes=0;string? lastPath=null;JsonElement lastBody=default;bool conflict=false;
            agent.MapGet("/storage/network",()=>new NetworkSharesStatus(new(),[],[],false,true,false,null));
            agent.MapPost("/storage/network/{**action}",async (HttpRequest request)=>
            {
                writes++;lastPath=request.Path;lastBody=await JsonSerializer.DeserializeAsync<JsonElement>(request.Body);
                return conflict?Results.Conflict(new{error="Unload every workload using this connection first."}):Results.Ok(new{success=true});
            });
            await agent.StartAsync();
            var keys=new ApiKeys(root);var key=keys.Create(new("MCP fixture","t3",0));var automation=keys.Create(new("Automation fixture","automation",0));
            var permissions=new T3StorageConfig(McpRead:true);bool unavailable=false;
            var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();
            await using var app=builder.Build();app.Urls.Add("http://127.0.0.1:0");
            app.UseApiKeys(keys,()=>true,()=>unavailable?Task.FromException<T3StorageConfig>(new IOException()):Task.FromResult(permissions));
            var appliance=new Appliance();app.MapNetworkShares(appliance);T3Mcp.Map(app,appliance,keys);
            await app.StartAsync();
            using var client=new HttpClient{BaseAddress=new Uri(app.Urls.Single())};client.DefaultRequestHeaders.Authorization=new("Bearer",key.Token);
            var catalog=await client.GetFromJsonAsync<JsonElement>("/api/t3-code/mcp/catalog");
            var routes=catalog.GetProperty("endpoints").EnumerateArray().Where(e=>e.GetProperty("path").GetString()!.StartsWith("/api/storage/network")).ToArray();
            check(routes.Length==8&&routes.All(e=>!string.IsNullOrEmpty(e.GetProperty("description").GetString())),"MCP discovers all eight storage routes with operation guidance");
            var schema=routes.Single(e=>e.GetProperty("path").GetString()=="/api/storage/network/connections").GetProperty("parameters")[0].GetProperty("schema").GetProperty("properties");
            check(schema.TryGetProperty("password",out _)&&schema.TryGetProperty("readOnly",out _)&&schema.TryGetProperty("uid",out _),"MCP catalog exposes the real typed connection schema");
            check((await client.GetAsync("/api/storage/network")).StatusCode==HttpStatusCode.OK,"Read-only MCP can inspect network shares");
            var actions=new (string Path,object Body)[]{
                ("runtime",new{}),
                ("connections",new SmbConnectionRequest("NAS","nas.test","data","fixture","synthetic-secret")),
                ("connections/action",new SmbAction("smb-fixture","test")),
                ("hosted",new SmbHostRequest("data","/srv/data","fixture","synthetic-secret",1000,1000)),
                ("hosted/access",new SmbShareAccess("smb-fixture",["192.0.2.20","198.51.100.0/24"])),
                ("hosted/remove",new SmbAction("smb-fixture","remove")),
                ("server",new SmbServerSettings("192.0.2.10",true))
            };
            foreach(var (path,body) in actions)
                check((await client.PostAsJsonAsync("/api/storage/network/"+path,body)).StatusCode==HttpStatusCode.Forbidden&&writes==0,"Read-only MCP cannot mutate shares: "+path);
            permissions=new(McpWrite:true);
            foreach(var (path,body) in actions)
            {
                check((await client.PostAsJsonAsync("/api/storage/network/"+path,body)).StatusCode==HttpStatusCode.OK&&lastPath=="/storage/network/"+path,"Write-enabled MCP reaches the typed storage handler: "+path);
                if(path=="hosted/access")check(lastBody.GetProperty("allowedClients")[1].GetString()=="198.51.100.0/24","MCP forwards explicit allowed-system changes through the typed API");
                if(path=="connections")check(lastBody.GetProperty("password").GetString()=="synthetic-secret","Connection credentials reach only the privileged request body");
            }
            var before=writes;permissions=new();
            check((await client.PostAsJsonAsync("/api/storage/network/server",new SmbServerSettings("192.0.2.10",false))).StatusCode==HttpStatusCode.Forbidden&&writes==before,"MCP permission removal blocks storage mutations immediately");
            permissions=new(FullHostAccess:true);
            check((await client.PostAsJsonAsync("/api/storage/network/server",new SmbServerSettings("192.0.2.10",false))).StatusCode==HttpStatusCode.OK,"Full host access includes network share management");
            conflict=true;
            var denied=await client.PostAsJsonAsync("/api/storage/network/connections/action",new SmbAction("smb-fixture","disconnect"));
            check(denied.StatusCode==HttpStatusCode.Conflict&&(await denied.Content.ReadAsStringAsync()).Contains("Unload every workload"),"MCP preserves the privileged workload-in-use error");
            unavailable=true;before=writes;
            check((await client.PostAsJsonAsync("/api/storage/network/runtime",new{})).StatusCode==HttpStatusCode.ServiceUnavailable&&writes==before,"Permission-service outages cannot reach share mutations");unavailable=false;
            client.DefaultRequestHeaders.Authorization=new("Bearer",automation.Token);
            check((await client.PostAsJsonAsync("/api/storage/network/server",new SmbServerSettings())).StatusCode==HttpStatusCode.Forbidden,"General automation cannot publish or stop hosted shares");
            var automationCatalog=await client.GetFromJsonAsync<JsonElement>("/api/t3-code/mcp/catalog");
            check(!automationCatalog.GetProperty("endpoints").EnumerateArray().Any(e=>e.GetProperty("path").GetString()!.StartsWith("/api/storage/network/")&&e.GetProperty("method").GetString()=="POST"),"Automation catalog omits forbidden storage mutations");
            keys.Revoke(key.Key.Id);client.DefaultRequestHeaders.Authorization=new("Bearer",key.Token);
            check((await client.GetAsync("/api/storage/network")).StatusCode==HttpStatusCode.Unauthorized,"Revoked MCP keys cannot inspect shared storage");
            foreach(var path in new[]{"/api/storage/network/unknown","/api/api-keys","/api/t3-code/storage","/api/t3-code/mcp/connect"})
                check(!ApiKeys.Allows("t3","POST",path),"Network-share MCP grant does not authorize unrelated or unknown mutations: "+path);
            check(!File.ReadAllText(root+"/api-keys.json").Contains("synthetic-secret"),"MCP audit records never retain SMB request bodies");
            await app.StopAsync();await agent.StopAsync();appliance.Agent.Dispose();
        }
        finally{Environment.SetEnvironmentVariable("XUR_RUN",oldRun);Environment.SetEnvironmentVariable("XUR_MODE",oldMode);Directory.Delete(root,true);}
    }
}
