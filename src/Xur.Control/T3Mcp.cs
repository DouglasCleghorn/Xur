using System.Reflection;
using System.Text.Json;
using System.Text.Json.Schema;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http.Metadata;
using Xur.Domain;

namespace Xur.Control;

public static class T3Mcp
{
    public static bool Allowed(T3StorageConfig settings,string method)=>settings.FullHostAccess ||
        (method is "GET" or "HEAD" ? settings.McpRead || settings.McpWrite : settings.McpWrite);

    public static void Map(WebApplication app,Appliance appliance,ApiKeys keys)
    {
        var gate=new SemaphoreSlim(1);
        app.MapGet("/api/t3-code/mcp/catalog",(HttpContext context)=>
        {
            var scope=(context.Items["apiKey"] as ApiKeyInfo)?.Scope??"automation";
            var json=new JsonSerializerOptions(JsonSerializerDefaults.Web){TypeInfoResolver=new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()};
            object Parameter(ParameterInfo p)
            {
                object? schema=null;
                try{schema=json.GetJsonSchemaAsNode(p.ParameterType);}catch(NotSupportedException){}catch(InvalidOperationException){}
                return new{name=p.Name,schema,optional=p.HasDefaultValue};
            }
            var endpoints=((IEndpointRouteBuilder)app).DataSources.SelectMany(s=>s.Endpoints).OfType<RouteEndpoint>()
                .Where(e=>e.RoutePattern.RawText?.StartsWith("/api/",StringComparison.Ordinal)==true)
                .SelectMany(e=>(e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods??[])
                    .Where(m=>ApiKeys.Allows(scope,m,e.RoutePattern.RawText!))
                    .Select(m=>new{method=m,path=e.RoutePattern.RawText,description=e.Metadata.GetMetadata<IEndpointDescriptionMetadata>()?.Description,parameters=e.Metadata.GetMetadata<MethodInfo>()?.GetParameters()
                        .Where(p=>p.ParameterType!=typeof(HttpContext)&&p.ParameterType!=typeof(HttpRequest)&&p.ParameterType!=typeof(HttpResponse)&&p.ParameterType!=typeof(CancellationToken))
                        .Select(Parameter).ToArray()??[]})).ToArray();
            return Results.Json(new{instructions="Use route parameters in the path, scalar parameters in the query and request objects as JSON bodies. Read current settings before updating. Preserve revision and approval fields. API errors are authoritative. Streaming and large downloads should use their dedicated clients.",endpoints});
        });
        app.MapPost("/api/t3-code/mcp/connect",async Task<IResult>(HttpContext context)=>
        {
            if(appliance.Installer)return Results.Conflict();
            // Bind credentials to the same HTTPS origin as the manager, never a supplied URL.
            var origin=T3Models.ValidateOrigin("https://"+context.Request.Host);
            await gate.WaitAsync(context.RequestAborted);
            try
            {
                using var cert=LocalTls.Load(appliance.StateDirectory);
                var key=keys.Create(new("T3 Xur MCP","t3",0));
                try
                {
                    using var response=await appliance.Agent.PostAsJsonAsync("/t3-code/setup",new T3SetupRequest("mcp-connect",Mcp:new(origin,key.Token,cert.ExportCertificatePem())),context.RequestAborted);
                    var result=response.IsSuccessStatusCode?await response.Content.ReadFromJsonAsync<T3SetupState>(context.RequestAborted):null;
                    if(result?.State!="Succeeded")
                    {keys.Revoke(key.Key.Id);return Results.Conflict(new{error=result?.Message??"MCP setup failed. Start T3 and retry."});}
                    foreach(var old in keys.List().Where(k=>k.Scope=="t3"&&k.Id!=key.Key.Id&&k.RevokedAt==null))keys.Revoke(old.Id);
                    return Results.Json(result);
                }
                catch{keys.Revoke(key.Key.Id);throw;}
            }
            finally{gate.Release();}
        });
    }
}
