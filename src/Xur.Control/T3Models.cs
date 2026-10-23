using System.Text.Json;
using System.Text;
using Xur.Domain;

namespace Xur.Control;

public record T3Connection(string Id,string WorkloadId,string Fingerprint,string ModelId,string Harness,string KeyId,string Name,bool Configured=false);
public record T3AvailableModel(string WorkloadId,string Name,string Engine,string State,string[] Profiles,string[] ModelIds,string? Error=null,Dictionary<string,int>? ContextLimits=null);

public sealed class T3Models
{
    readonly string path;
    readonly Func<Task<ProfileState>> observe;
    readonly Func<Task<BackendRoute[]>> routes;
    readonly HttpClient gateway;
    readonly ApiKeys keys;
    readonly Func<T3SetupRequest,Task<T3SetupState>> setup;
    readonly Func<string> certificate;
    readonly SemaphoreSlim gate=new(1);
    readonly object sync=new();
    T3Connection[] connections;
    static readonly JsonSerializerOptions json=new(JsonSerializerDefaults.Web);
    public T3Models(string directory,Func<Task<ProfileState>> observe,Func<Task<BackendRoute[]>> routes,HttpClient gateway,ApiKeys keys,
        Func<T3SetupRequest,Task<T3SetupState>> setup,Func<string> certificate)
    {
        path=Path.Combine(directory,"t3-connections.json");this.observe=observe;this.routes=routes;this.gateway=gateway;this.keys=keys;this.setup=setup;this.certificate=certificate;
        connections=File.Exists(path)?JsonSerializer.Deserialize<T3Connection[]>(File.ReadAllText(path),json)??[]:[];
    }
    public T3Connection[] Connections(){lock(sync)return connections.ToArray();}
    void Save(T3Connection[] value)
    {
        lock(sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);var temp=path+".tmp";
            using(var file=new FileStream(temp,new FileStreamOptions{Mode=FileMode.Create,Access=FileAccess.Write,UnixCreateMode=(UnixFileMode)384}))
            {JsonSerializer.Serialize(file,value,json);file.Flush(true);}
            File.Move(temp,path,true);connections=value;
        }
    }
    static IEnumerable<Workload> Definitions(ProfileState state)=>(state.Active?.Workloads??[]).Concat(state.Profiles.SelectMany(p=>p.Workloads));
    public static bool IsTextModel(Workload w)=>w.Recipe.Kind=="Model"&&w.Recipe.Engine is "llama.cpp" or "vLLM";
    public static bool Ready(Workload w,ProfileState state,BackendRoute[] published)=>state.Runtime.Instances.Any(i=>i.Id==w.Id&&i.Fingerprint==w.Fingerprint&&i.State=="running"&&published.Any(r=>r.WorkloadId==w.Id&&r.Name==w.Route&&r.Endpoint==i.Endpoint));
    async Task<Dictionary<string,int>> Models(Workload w)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response=await gateway.GetAsync(Uri.EscapeDataString(w.Route)+"/v1/models",HttpCompletionOption.ResponseHeadersRead,timeout.Token);
        response.EnsureSuccessStatusCode();await response.Content.LoadIntoBufferAsync(1024*1024,timeout.Token);
        using var document=JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var result=new Dictionary<string,int>();
        foreach(var model in document.RootElement.GetProperty("data").EnumerateArray().Take(128))
        {
            var id=model.GetProperty("id").GetString();if(id==null||id.Length is <1 or >512||id.Any(char.IsControl))continue;
            var context=model.TryGetProperty("max_model_len",out var length)&&length.TryGetInt32(out var tokens)?tokens:0;
            result[id]=context is >=1024 and <=2097152?context:0;
        }
        if(w.Recipe.Engine=="llama.cpp"&&result.Values.Any(n=>n==0))
        {
            try
            {
                using var props=await gateway.GetAsync(Uri.EscapeDataString(w.Route)+"/props",timeout.Token);
                await props.Content.LoadIntoBufferAsync(1024*1024,timeout.Token);
                using var info=JsonDocument.Parse(await props.Content.ReadAsStringAsync(timeout.Token));
                var context=info.RootElement.GetProperty("default_generation_settings").GetProperty("n_ctx").GetInt32();
                if(context is >=1024 and <=2097152)foreach(var id in result.Keys.ToArray())if(result[id]==0)result[id]=context;
            }
            catch(Exception e)when(e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException){}
        }
        return result;
    }
    public async Task<T3AvailableModel[]> Available()
    {
        var state=await observe();var published=await routes();var result=new List<T3AvailableModel>();
        foreach(var w in Definitions(state).Where(IsTextModel).DistinctBy(w=>w.Id))
        {
            var ready=Ready(w,state,published);Dictionary<string,int> ids=[];string? error=null;
            if(ready)try{ids=await Models(w);}catch(Exception e)when(e is HttpRequestException or JsonException or KeyNotFoundException or OperationCanceledException or InvalidOperationException)
            {ready=false;error="Could not read the model list. Check the workload and retry.";}
            result.Add(new(w.Id,w.Name,w.Recipe.Engine,ready?"Ready":state.Runtime.Instances.Any(i=>i.Id==w.Id)?"Starting or unavailable":"Profile not loaded",
                state.Profiles.Where(p=>p.Workloads.Any(x=>x.Id==w.Id&&x.Fingerprint==w.Fingerprint)).Select(p=>p.Name).ToArray(),ids.Keys.ToArray(),error,ids));
        }
        return result.ToArray();
    }
    public static string ValidateOrigin(string origin)
    {
        if(!Uri.TryCreate(origin,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length>0||uri.AbsolutePath!="/"||uri.Query.Length>0||uri.Fragment.Length>0||uri.IsLoopback)
            throw new InvalidOperationException("Use Xur's HTTPS LAN or Tailscale address, reachable from the T3 container.");
        return uri.GetLeftPart(UriPartial.Authority);
    }
    public async Task<T3Connection> Connect(T3ModelConnectRequest request)
    {
        if(request.Harness is not ("opencode" or "codex")||!ProfilePolicy.EntityIdentifier(request.WorkloadId))throw new InvalidOperationException("Choose a local model and harness.");
        var origin=ValidateOrigin(request.Origin);
        await gate.WaitAsync();
        try
        {
            var state=await observe();var published=await routes();
            var w=Definitions(state).FirstOrDefault(w=>w.Id==request.WorkloadId&&IsTextModel(w)&&Ready(w,state,published))??throw new InvalidOperationException("Load this model's profile and wait for its endpoint to be ready.");
            var served=await Models(w);
            if(!served.TryGetValue(request.ModelId,out var reportedContext))throw new InvalidOperationException("Choose an exact model ID served by this workload.");
            if(request.ContextTokens is <1024 or >2097152||reportedContext>0&&request.ContextTokens>reportedContext)throw new InvalidOperationException("Set a context limit between 1,024 tokens and the model's serving limit.");
            var id="xur_"+Canonical.Hash(new{w.Id,w.Fingerprint,request.ModelId,request.Harness})[..24];
            var model=new T3ModelSetup(id,w.Name,request.ModelId,origin+"/api/t3-code/inference/"+id+"/v1","",certificate(),request.ContextTokens);
            var existing=Connections().FirstOrDefault(c=>c.Id==id&&c.Configured&&keys.List().Any(k=>k.Id==c.KeyId&&k.RevokedAt==null&&(k.ExpiresAt==null||k.ExpiresAt>DateTimeOffset.UtcNow)));
            if(existing!=null&&(await setup(new("connection-status",request.Harness,Model:model))).State=="Configured")return existing;
            var key=keys.Create(new("T3 · "+w.Name[..Math.Min(w.Name.Length,60)],"coding",0));
            var connection=new T3Connection(id,w.Id,w.Fingerprint,request.ModelId,request.Harness,key.Key.Id,w.Name);
            var previous=Connections();Save([..previous.Where(c=>c.Id!=id),connection]);
            try
            {
                var result=await setup(new("connect",request.Harness,Model:model with{ApiKey=key.Token}));
                if(result.State!="Succeeded")throw new InvalidOperationException(result.Message);
                connection=connection with{Configured=true};Save([..previous.Where(c=>c.Id!=id),connection]);
                foreach(var old in previous.Where(c=>c.Id==id))keys.Revoke(old.KeyId);
                return connection;
            }
            catch{keys.Revoke(key.Key.Id);Save(previous);throw;}
        }
        finally{gate.Release();}
    }
    public async Task Forward(HttpContext context,string id,string path)
    {
        var connection=Connections().FirstOrDefault(c=>c.Id==id);
        if(connection==null||context.Items["apiKey"] is not ApiKeyInfo key||key.Id!=connection.KeyId)
        {context.Response.StatusCode=403;return;}
        if(!(context.Request.Method=="GET"&&path=="models"||context.Request.Method=="POST"&&path==(connection.Harness=="codex"?"responses":"chat/completions")))
        {context.Response.StatusCode=404;return;}
        var state=await observe();var published=await routes();
        var workload=Definitions(state).FirstOrDefault(w=>w.Id==connection.WorkloadId&&w.Fingerprint==connection.Fingerprint&&Ready(w,state,published));
        if(workload==null)
        {context.Response.StatusCode=503;await context.Response.WriteAsJsonAsync(new{error=new{message="This Xur model is unavailable. Load a profile containing the original workload, then retry.",type="model_unavailable"}});return;}
        if(context.Request.Method=="GET")
        {
            await context.Response.WriteAsJsonAsync(new{data=new[]{new{id=connection.ModelId,@object="model",owned_by="xur"}},@object="list"});return;
        }
        // Keep an exact model binding even if the engine starts serving other aliases.
        using var body=new MemoryStream();var buffer=new byte[65536];int count;
        while((count=await context.Request.Body.ReadAsync(buffer,context.RequestAborted))>0)
        {
            if(body.Length+count>16*1024*1024){context.Response.StatusCode=413;return;}
            await body.WriteAsync(buffer.AsMemory(0,count),context.RequestAborted);
        }
        body.Position=0;
        try
        {
            using var parsed=await JsonDocument.ParseAsync(body,cancellationToken:context.RequestAborted);
            if(parsed.RootElement.GetProperty("model").GetString()!=connection.ModelId){context.Response.StatusCode=400;return;}
        }
        catch(Exception e)when(e is JsonException or KeyNotFoundException or InvalidOperationException){context.Response.StatusCode=400;return;}
        body.Position=0;context.Request.Body=body;context.Request.ContentLength=body.Length;
        var instance=state.Runtime.Instances.Single(i=>i.Id==workload.Id&&i.Fingerprint==workload.Fingerprint&&i.State=="running");
        context.Request.Headers["X-Xur-Expected-Workload"]=workload.Id;
        context.Request.Headers["X-Xur-Expected-Endpoint"]=instance.Endpoint;
        await StreamProxy.Forward(context,gateway,"http://localhost/"+Uri.EscapeDataString(workload.Route)+"/v1/"+path);
    }
    public static void Map(WebApplication app,Appliance appliance,ProfileManager profiles,HttpClient gateway,HttpClient admin,ApiKeys keys)
    {
        using var cert=LocalTls.Load(appliance.StateDirectory);var certificate=cert.ExportCertificatePem();
        var setupClient=LocalClient.Create(Path.Combine(appliance.RunDirectory,"agent.sock"));setupClient.Timeout=TimeSpan.FromMinutes(5);
        app.Lifetime.ApplicationStopped.Register(setupClient.Dispose);
        var models=new T3Models(appliance.StateDirectory,profiles.State,async()=>await admin.GetFromJsonAsync<BackendRoute[]>("/routes")??[],gateway,keys,async request=>
        {
            using var response=await setupClient.PostAsJsonAsync("/t3-code/setup",request);
            if(!response.IsSuccessStatusCode)throw new InvalidOperationException("T3 setup is unavailable. Finish any current setup, then retry.");
            return await response.Content.ReadFromJsonAsync<T3SetupState>()??throw new InvalidOperationException("Invalid T3 setup response.");
        },()=>certificate);
        async Task<IResult> Safe(Func<Task<object>> action)
        {
            if(appliance.Installer)return Results.Conflict();
            try{return Results.Json(await action());}
            catch(Exception e)when(e is InvalidOperationException or ArgumentException or HttpRequestException or IOException or OperationCanceledException or TimeoutException)
            {return Results.Conflict(new{error=e is InvalidOperationException or ArgumentException?e.Message:"Model connection unavailable. Check the workload and retry."});}
        }
        app.MapGet("/api/t3-code/models",()=>Safe(async()=>new{models=await models.Available(),connections=models.Connections()}));
        app.MapPost("/api/t3-code/connect",(HttpContext context,T3ModelConnectRequest request)=>Safe(async()=>
        {
            // The browser may choose a reachable Xur address, but cannot turn this
            // credential-bearing request into an arbitrary outbound request.
            var origin=ValidateOrigin(request.Origin);
            if(!Uri.TryCreate("https://"+context.Request.Host,UriKind.Absolute,out var current)||new Uri(origin).Host!=current.Host||new Uri(origin).Port!=current.Port)
                throw new InvalidOperationException("Open Xur at its LAN or Tailscale HTTPS address before connecting a model.");
            return await models.Connect(request);
        }));
        app.Map("/api/t3-code/inference/{id}/v1/{**path}",async(HttpContext context,string id,string? path)=>
        {
            if(appliance.Installer){context.Response.StatusCode=409;return;}
            await models.Forward(context,id,path??"");
        });
    }
}
