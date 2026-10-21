using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xur.Agent;
using Xur.Control;
using Xur.Domain;

static class UnslothTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var recipe=new RecipeCatalog("catalog/models").Recipes.Single(UnslothWorkload.Is);
        check(Canonical.Hash(recipe)==Canonical.Hash(UnslothWorkload.Recipe),"Installed Unsloth recipe matches the supported runtime and persistent mounts");
        foreach(var invalid in new[]{recipe with{Image="mirror.gcr.io/unsloth/unsloth:core"},recipe with{Port=8888},recipe with{Vendor="CPU",GpuCount=0},recipe with{Container=recipe.Container! with{Mounts=[]}},recipe with{Command=["sh"]}})
        {
            bool rejected=false;try{ProfilePolicy.ValidateRecipe(invalid);}catch(InvalidOperationException){rejected=true;}
            check(rejected,"Unsloth rejects changes that bypass its runtime contract");
        }
        check(!ApiKeys.Allows("automation","POST","/api/unsloth/password")&&!ApiKeys.Allows("diagnostics","POST","/api/unsloth/password"),"API keys cannot retrieve Unsloth credentials");
        var gpu=new GpuDevice("0000:01:00.0","NVIDIA","GPU","nvidia","GPU-123",24000,[],[]);
        var w=new Workload("unsloth","Training",recipe,[gpu.Pci],"training");
        var observed=new RuntimeObservation("generation",[gpu],[new(w.Id,w.Fingerprint,"old",1,"boot","http://127.0.0.1:8000/","running",w.Gpus)]);
        ProfilePolicy.Validate(new("p","Training",1,[w]),observed);
        check(ModelGpuArguments.For(w,[gpu]).Contains("nvidia.com/gpu=GPU-123"),"Unsloth receives the selected NVIDIA GPU through CDI");
        check(ProfilePolicy.Steps(new("p","Other profile",1,[w with{Name="Renamed",Route="new-route"}]),observed).Any(s=>s.Kind=="Keep"),"Unchanged Unsloth survives profile and route changes");
        bool duplicate=false;try{ProfilePolicy.Validate(new("p","Duplicate",1,[w,w with{Id="other",Route="other"}]),observed);}catch(InvalidOperationException){duplicate=true;}
        check(duplicate,"Only one Unsloth server can own the shared Studio storage and port");
        await Images(check);
        await Storage(check);
        await Serve(check);
        await Handoff(check,recipe,gpu);
        await Endpoints(check);
    }
    static async Task Images(Action<bool,string> check)
    {
        var calls=new List<string[]>();var image="sha256:"+new string('a',64);var oldImage="sha256:"+new string('b',64);
        bool fail=false;
        Task<ProcessResult> Run(string exe,string[] args,int seconds)
        {
            calls.Add(args);
            if(args[0]=="pull")return Task.FromResult(new ProcessResult(fail?1:0,fail?"cache miss":image));
            if(args[0]=="container")return Task.FromResult(new ProcessResult(0,JsonSerializer.Serialize(new[]{new{Image=oldImage,State=new{Running=false}}})));
            return Task.FromResult(new ProcessResult(0,""));
        }
        var runtime=new UnslothRuntime(Run);
        var stopped=new RuntimeInstance("unsloth","f","old-instance",0,"boot","","exited",[]);
        check(await runtime.Refresh(stopped with{State="running"})==null && calls.Count==0,"A running Unsloth is never pulled or replaced");
        var selection=await runtime.Refresh(null);
        check(selection is {Recreate:false}&&selection.Image==image&&calls.Single().SequenceEqual(new[]{"pull","--quiet","--policy=always",UnslothWorkload.Image}),"Each new start resolves latest through Google's mirror to an immutable image ID");
        calls.Clear();selection=await runtime.Refresh(stopped);
        check(selection is {Recreate:true}&&calls.Any(a=>a.SequenceEqual(new[]{"rm","old-instance"}))&&!calls.Any(a=>a.Contains("--force")||a.Contains("--volumes")),"Image changes replace only the stopped container and preserve its data");
        oldImage=image;calls.Clear();selection=await runtime.Refresh(stopped);
        check(selection is {Recreate:false}&&calls.All(a=>a[0]!="rm"),"Unchanged latest image reuses the stopped container");
        fail=true;calls.Clear();bool rejected=false;
        try{await runtime.Refresh(stopped);}catch(InvalidOperationException e){rejected=e.Message.Contains("mirror.gcr.io");}
        check(rejected&&calls.Count==1,"Mirror failure is explicit and never falls back to Docker Hub or a stale image");
    }
    static async Task Storage(Action<bool,string> check)
    {
        var volumes=new HashSet<string>();var calls=new List<string[]>();
        var runtime=new UnslothRuntime((exe,args,seconds)=>
        {
            calls.Add(args);
            if(args[1]=="exists")return Task.FromResult(new ProcessResult(volumes.Contains(args[2])?0:1,""));
            volumes.Add(args[^1]);return Task.FromResult(new ProcessResult(0,args[^1]));
        });
        await runtime.EnsureStorage();await runtime.EnsureStorage();
        check(volumes.Count==2&&calls.Count(a=>a[1]=="create")==2,"Repeated starts reuse both persistent volumes without recreating data");
        check(calls.Where(a=>a[1]=="create").All(a=>a.Contains("io.xur.volume="+a[^1])),"Unsloth volumes carry Xur ownership labels for storage inventory and mount verification");
        var root=Path.GetFullPath(".build/evidence/unsloth/storage-"+Guid.NewGuid().ToString("N"));
        try
        {
            var args=UnslothRuntime.ModelArguments(root);
            check(args.Contains(root+"/models:/models:ro,z")&&args.Contains(root+"/model-sets:/model-sets:ro,z"),"Existing Xur downloads and snapshots are shared read-only without private SELinux relabeling");
            check(recipeMountsWritable(),"Training downloads and outputs remain writable in persistent workspace and Studio mounts");
        }finally{Directory.Delete(root,true);}
        static bool recipeMountsWritable()=>UnslothWorkload.Recipe.Container!.Mounts.All(m=>!m.ReadOnly);
    }
    static async Task Handoff(Action<bool,string> check,Recipe recipe,GpuDevice gpu)
    {
        var root=Path.GetFullPath(".build/evidence/unsloth/profile-"+Guid.NewGuid().ToString("N"));
        var second=gpu with{Pci="0000:02:00.0",RuntimeId="GPU-456"};
        using var store=new ProfileStore(root);var runtime=new Runtime([gpu,second]);var gateway=new Gateway();var manager=new ProfileManager(store,runtime,gateway);
        var first=new Workload("first","Unsloth",recipe,[gpu.Pci],"first");
        await manager.Save(new("a","First",0,[first]));var plan=await manager.Preview("a");await manager.Apply(new(plan.Id,plan.Digest));await manager.Wait();
        check((await manager.State()).Operation?.Stage=="Complete"&&gateway.Routes.Length==0,"Unsloth loads through profiles without publishing a broken inference proxy route");
        await manager.Save(new("b","Second",0,[first with{Id="second",Gpus=[second.Pci],Route="second"}]));
        runtime.HoldStop=true;plan=await manager.Preview("b");await manager.Apply(new(plan.Id,plan.Digest));
        await runtime.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(3));
        check(runtime.Starts==1,"Changing GPUs waits for the old Studio server to release shared storage and its port");
        runtime.Release.TrySetResult();await manager.Wait();
        check((await manager.State()).Operation?.Stage=="Complete"&&runtime.Starts==2,"Unsloth replacement starts after the previous server stops");
    }
    static async Task Serve(Action<bool,string> check)
    {
        var fixture=new ServeFixture();var serve=new UnslothServe(fixture.Run);var time=DateTimeOffset.UtcNow;
        var missing=await serve.Refresh(true,"",time);
        check(missing.Url==null&&fixture.Calls.Count==0,"Unsloth requires a Tailscale HTTPS identity instead of deriving an insecure LAN link");
        fixture.Proxies["443"]="unix:/run/xur/serve.sock";
        var ready=await serve.Refresh(true,"https://training.example.ts.net/",time);
        check(ready.Url=="https://training.example.ts.net:8444/"&&fixture.Calls.Any(a=>a.SequenceEqual(new[]{"serve","--bg","--https=8444","http://127.0.0.1:8000"})),"Unsloth configures its dedicated HTTPS Serve port and loopback proxy");
        check(UnslothWorkload.Publish=="127.0.0.1:8000:8000"&&fixture.Proxies["443"]=="unix:/run/xur/serve.sock","Unsloth HTTP stays on loopback and the manager's HTTPS route is preserved");
        var count=fixture.Calls.Count;await serve.Refresh(true,"https://training.example.ts.net/",time.AddSeconds(1));
        check(fixture.Calls.Count==count,"Repeated status requests reuse the verified proxy until its next check");
        fixture.Proxies.Remove("8444");await serve.Refresh(true,"https://training.example.ts.net/",time.AddSeconds(31));
        check(fixture.Proxies.ContainsKey("8444"),"A removed Serve route is repaired automatically");
        var stopped=await serve.Refresh(false,"https://training.example.ts.net/",time.AddSeconds(32));
        check(stopped.Url==null&&!fixture.Proxies.ContainsKey("8444")&&fixture.Proxies.ContainsKey("443")&&fixture.Calls.All(a=>!a.Contains("reset")),"Unloading removes only Unsloth's HTTPS handler");
        fixture.Proxies["8444"]="http://127.0.0.1:9000";count=fixture.Calls.Count;
        var conflict=await serve.Refresh(true,"https://training.example.ts.net/",time.AddSeconds(33));
        check(conflict.Url==null&&conflict.Message.Contains("another service")&&fixture.Calls.Skip(count).All(a=>a[1]=="status")&&fixture.Proxies["8444"]=="http://127.0.0.1:9000","An occupied HTTPS port is reported without overwriting another service");
        fixture.Proxies.Remove("8444");fixture.Fail=true;
        var failed=await serve.Refresh(true,"https://training.example.ts.net/",time.AddMinutes(2));
        check(failed.Url==null&&failed.Message.Contains("unavailable"),"Failed Tailscale HTTPS configuration never advertises a ready connection");
        fixture.Fail=false;fixture.IgnoreConfiguration=true;
        var unverified=await serve.Refresh(true,"https://training.example.ts.net/",time.AddMinutes(3));
        check(unverified.Url==null,"Serve command success must be verified against the actual proxy configuration");
        fixture.IgnoreConfiguration=false;fixture.Host="renamed.example.ts.net";
        var renamed=await serve.Refresh(true,"https://renamed.example.ts.net/",time.AddMinutes(3));
        check(renamed.Url=="https://renamed.example.ts.net:8444/","Tailscale hostname changes update the Unsloth HTTPS origin");
    }
    sealed class ServeFixture
    {
        public string Host="training.example.ts.net";
        public bool Fail,IgnoreConfiguration;
        public List<string[]> Calls=[];
        public Dictionary<string,string> Proxies=[];
        public Task<ProcessResult> Run(string executable,string[] args,int timeout)
        {
            Calls.Add(args);
            if(args[0]=="status")return Task.FromResult(new ProcessResult(0,JsonSerializer.Serialize(new{BackendState="Running",Self=new{DNSName=Host+"."}})));
            if(args[1]=="status")return Task.FromResult(new ProcessResult(0,JsonSerializer.Serialize(new{
                TCP=Proxies.ToDictionary(p=>p.Key,p=>new{HTTPS=true}),
                Web=Proxies.ToDictionary(p=>Host+":"+p.Key,p=>new{Handlers=new Dictionary<string,object>{{"/",new{Proxy=p.Value}}}})})));
            if(Fail)return Task.FromResult(new ProcessResult(1,"HTTPS disabled"));
            var port=args.Single(a=>a.StartsWith("--https=")).Split('=')[1];
            if(args[^1]=="off")Proxies.Remove(port);
            else if(!IgnoreConfiguration)Proxies[port]=args[^1];
            return Task.FromResult(new ProcessResult(0,""));
        }
    }
    sealed class Runtime(GpuDevice[] gpus):IWorkloadRuntime
    {
        RuntimeInstance? current;
        public int Starts;public bool HoldStop;
        public TaskCompletionSource Stopping=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<RuntimeObservation> Observe()=>Task.FromResult(new RuntimeObservation("g",gpus,current==null?[]:[current]));
        public Task<RuntimeInstance> Start(Workload w){Starts++;current=new(w.Id,w.Fingerprint,"instance-"+Starts,Starts,"boot","http://127.0.0.1:8000/","running",w.Gpus);return Task.FromResult(current);}
        public async Task Stop(RuntimeStop request){if(HoldStop){Stopping.TrySetResult();await Release.Task;}current=null;}
    }
    sealed class Gateway:IWorkloadGateway
    {
        public BackendRoute[] Routes=[];
        public Task Drain(string id)=>Task.CompletedTask;
        public Task Publish(BackendRoute[] routes){Routes=routes;return Task.CompletedTask;}
    }
    static async Task Endpoints(Action<bool,string> check)
    {
        var root=Path.GetFullPath(".build/evidence/u-"+Guid.NewGuid().ToString("N")[..8]);Directory.CreateDirectory(root);
        var previousRun=Environment.GetEnvironmentVariable("XUR_RUN");var previousMode=Environment.GetEnvironmentVariable("XUR_MODE");
        Environment.SetEnvironmentVariable("XUR_RUN",root);Environment.SetEnvironmentVariable("XUR_MODE","Installed");
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.ListenUnixSocket(root+"/agent.sock"));
        await using var agent=builder.Build();bool running=true;
        agent.MapGet("/unsloth/status",()=>new UnslothStatus(running));
        agent.MapPost("/unsloth/password",IResult()=>running?Results.Json(new UnslothPassword("test-only-secret")):Results.Conflict(new{error="Load a profile with Unsloth first."}));
        var frontBuilder=WebApplication.CreateBuilder();frontBuilder.Logging.ClearProviders();frontBuilder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));
        await using var front=frontBuilder.Build();var fixture=new ServeFixture();var appliance=new Appliance(fixture.Run);UnslothEndpoints.MapEndpoints(front,appliance,new UnslothServe(fixture.Run));
        try
        {
            await agent.StartAsync();await front.StartAsync();using var client=new HttpClient{BaseAddress=new Uri(front.Urls.Single())};
            client.DefaultRequestHeaders.Host="192.0.2.10:8443";
            var connection=await client.GetFromJsonAsync<UnslothConnection>("/api/unsloth/status");
            check(connection is {Running:true,Url:"https://training.example.ts.net:8444/"},"Even a LAN browser receives the verified Tailscale HTTPS URL for Unsloth");
            using var response=await client.PostAsJsonAsync("/api/unsloth/password",new{});
            check((await response.Content.ReadFromJsonAsync<UnslothPassword>())?.Password=="test-only-secret"&&response.Headers.CacheControl?.NoStore==true,"First-login password is returned on request with caching disabled");
            running=false;using var stopped=await client.PostAsJsonAsync("/api/unsloth/password",new{});
            check(stopped.StatusCode==HttpStatusCode.Conflict&&(await stopped.Content.ReadAsStringAsync()).Contains("Load a profile"),"Stopped Unsloth returns an actionable credential error");
            Environment.SetEnvironmentVariable("XUR_MODE","Installer");
            var installerAppliance=new Appliance(fixture.Run);
            var installerBuilder=WebApplication.CreateBuilder();installerBuilder.Logging.ClearProviders();installerBuilder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));
            await using var installerFront=installerBuilder.Build();UnslothEndpoints.MapEndpoints(installerFront,installerAppliance,new UnslothServe(fixture.Run));
            await installerFront.StartAsync();
            try
            {
                running=true;using var installerClient=new HttpClient{BaseAddress=new Uri(installerFront.Urls.Single())};
                using var installer=await installerClient.PostAsJsonAsync("/api/unsloth/password",new{});
                check(installer.StatusCode==HttpStatusCode.Conflict,"Installer mode cannot retrieve Unsloth credentials even when a workload is running");
            }
            finally{await installerFront.StopAsync();installerAppliance.Agent.Dispose();}
        }
        finally
        {
            await front.StopAsync();await agent.StopAsync();appliance.Agent.Dispose();
            Environment.SetEnvironmentVariable("XUR_RUN",previousRun);Environment.SetEnvironmentVariable("XUR_MODE",previousMode);Directory.Delete(root,true);
        }
    }
}
