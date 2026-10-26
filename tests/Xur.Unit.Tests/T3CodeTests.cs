using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Diagnostics;
using Xur.Agent;
using Xur.Control;
using Xur.Domain;

static class T3CodeTests
{
    public static Recipe Recipe()
    {
        var preset=T3CodeWorkload.Prepare();
        return new("container-t3","T3 Code","sha256:"+new string('a',64),preset.Command!,preset.Port,preset.HealthPath,"CPU",0,0,"",
            Kind:"Container",Engine:T3CodeWorkload.Engine,Container:new([new("xur-volume-"+new string('b',32),T3CodeWorkload.Home)],preset.Environment!));
    }
    public static async Task Run(Action<bool,string> check)
    {
        var recipe=Recipe();ProfilePolicy.ValidateRecipe(recipe);
        var workload=new Workload("t3","T3 Code",recipe,[],"coding");
        var observed=new RuntimeObservation("g",[],[new(workload.Id,workload.Fingerprint,"instance",1,"boot","http://127.0.0.1:3773/","running",[])]);
        var renamed=workload with{Name="Coding",Route="t3-code"};
        check(ProfilePolicy.Steps(new("p","Coding",1,[renamed]),observed).Any(s=>s.Kind=="Keep"),"Legacy T3 recipe identities stay stable across name and route changes");
        var reuse=T3CodeWorkload.Prepare(recipe.Container!.Mounts[0].Volume);
        check(reuse.Volumes![0].Volume==recipe.Container.Mounts[0].Volume,"Preparing a replacement T3 Code image can reuse its home volume");
        foreach(var invalid in new[]{recipe with{Port=8080},recipe with{Command=[]},recipe with{Engine="Unknown"},recipe with{Container=recipe.Container with{Mounts=[]}},recipe with{Container=recipe.Container with{Mounts=[recipe.Container.Mounts[0] with{ReadOnly=true}]}}})
        {bool rejected=false;try{ProfilePolicy.ValidateRecipe(invalid);}catch(InvalidOperationException){rejected=true;}check(rejected,"Invalid T3 Code runtime contract is rejected");}
        bool duplicate=false;try{ProfilePolicy.Validate(new("p","Two servers",1,[workload,workload with{Id="t3-other",Route="other"}]),observed);}catch(InvalidOperationException){duplicate=true;}
        check(duplicate,"Only one T3 Code server can own the connection address");
        check(!ApiKeys.Allows("automation","POST","/api/t3-code/pair"),"Automation keys cannot mint T3 Code device credentials");
        check(T3CodeEndpoints.Origin(new HostString("[fd00::1]:8443"))=="http://[fd00::1]:3773","T3 Code links use its direct port and preserve IPv6 hosts");
        check(T3CodeEndpoints.Origin(new HostString("coding.test:8443"))=="http://coding.test:3773","T3 Code links target the server hostname rather than the browsing device");
        await PersistedComputeTests.Run(check);
        await PersistentUpdates(check);
        await Preparation(check);
        await T3CodeGpuTests.Run(check);
        await T3StorageTests.Run(check);
        await Endpoints(check);
        await T3ModelTests.Run(check);
    }
    static async Task Preparation(Action<bool,string> check)
    {
        var root=Path.GetFullPath(".build/evidence/t3-prepare-"+Guid.NewGuid().ToString("N")[..12]);Directory.CreateDirectory(root);
        var previousPath=Environment.GetEnvironmentVariable("PATH");var previousArgs=Environment.GetEnvironmentVariable("XUR_T3_PREPARE_ARGS");
        var capture=Path.Combine(root,"arguments");
        await File.WriteAllTextAsync(Path.Combine(root,"podman"),"""
            #!/bin/sh
            printf '%s\n' "$@" > "$XUR_T3_PREPARE_ARGS"
            for argument do context="$argument"; done
            if [ -f "$context/t3-launcher" ]; then
                cp "$context/t3-launcher" "$XUR_T3_PREPARE_ARGS.launcher"
                cp "$context/Dockerfile" "$XUR_T3_PREPARE_ARGS.dockerfile"
            fi
            printf '%s\n' 'Fixture container build failed.' >&2
            exit 1
            """);
        File.SetUnixFileMode(Path.Combine(root,"podman"),UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        try
        {
            Environment.SetEnvironmentVariable("PATH",root+":"+previousPath);Environment.SetEnvironmentVariable("XUR_T3_PREPARE_ARGS",capture);
            var library=new ContainerLibrary(Path.Combine(root,"state"));
            foreach(var request in new[]{T3CodeWorkload.Prepare(),new ContainerPrepareRequest(Dockerfile:"FROM mirror.gcr.io/library/node:lts")})
            {
                var job=library.Prepare(request);
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while(library.Busy)await Task.Delay(10,timeout.Token);
                var completed=library.Jobs().Single(j=>j.Id==job.Id);var arguments=await File.ReadAllLinesAsync(capture);
                if(request.Engine==T3CodeWorkload.Engine)
                {
                    check(await File.ReadAllTextAsync(capture+".launcher")==T3CodeWorkload.Launcher &&
                        (await File.ReadAllTextAsync(capture+".dockerfile")).Contains("COPY t3-launcher /opt/t3/t3"),
                        "T3 preparation supplies the persistent runtime launcher in the build context");
                }
                else check(!arguments.Contains("--build-arg"),"Custom container builds retain their existing cache behavior");
                check(completed.Stage=="Failed" && completed.RecipeId==null && completed.Log.Contains("Fixture container build failed."),"Container build failures remain visible without selecting a recipe");
            }
        }
        finally{Environment.SetEnvironmentVariable("PATH",previousPath);Environment.SetEnvironmentVariable("XUR_T3_PREPARE_ARGS",previousArgs);Directory.Delete(root,true);}
    }
    static async Task PersistentUpdates(Action<bool,string> check)
    {
        var root=Path.GetFullPath(".build/evidence/t3-startup-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(root+"/launcher",T3CodeWorkload.Launcher);
            var result=await Processes.Run("python3",["tests/Xur.Integration.Tests/t3-startup.py",root+"/launcher"],60);
            check(result.ExitCode==0,"T3 background startup updates: "+result.Output);
            var workload=new Workload("t3","T3 Code",Recipe(),[],"coding");
            using var old=JsonDocument.Parse("{}");
            using var current=JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string,string>{{T3CodeRuntime.Label,T3CodeRuntime.Policy},{T3CodeGpuAccess.Label,"all-v1"}}));
            check(T3CodeRuntime.Fingerprint(workload,old.RootElement)!=workload.Fingerprint && T3CodeRuntime.Fingerprint(workload,current.RootElement)==workload.Fingerprint,
                "Existing T3 containers migrate once to current bootstrap while preserving recipe identity");
            var command=T3CodeRuntime.Command(workload.Recipe);
            var arguments=T3CodeRuntime.ContainerArguments();
            check(arguments[0]=="--hostname" && arguments[1]==Environment.MachineName,
                "T3 advertises the Xur host name instead of a generated container identity");
            check(command[2]==T3CodeWorkload.Launcher && command.Skip(4).SequenceEqual(workload.Recipe.Command.Skip(1)),
                "Saved T3 images use current startup updates without rebuilding their image or changing storage");
        }
        finally{Directory.Delete(root,true);}
    }
    static async Task Endpoints(Action<bool,string> check)
    {
        var root=Path.GetFullPath(".build/evidence/t3-api-"+Guid.NewGuid().ToString("N")[..12]);Directory.CreateDirectory(root);
        var oldRun=Environment.GetEnvironmentVariable("XUR_RUN");var oldMode=Environment.GetEnvironmentVariable("XUR_MODE");
        Environment.SetEnvironmentVariable("XUR_RUN",root);Environment.SetEnvironmentVariable("XUR_MODE","Installed");
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.ListenUnixSocket(root+"/agent.sock"));
        await using var agent=builder.Build();var available=true;string? requestedVolume=null;
        var expires=DateTimeOffset.UtcNow.AddMinutes(5);bool enabled=true;
        agent.MapGet("/t3-code/status",()=>new T3CodeStatus("t3","Running",true,enabled));
        agent.MapPost("/t3-code/enabled",(T3CodeEnabled request)=>{enabled=request.Enabled;return Results.Ok(new{enabled});});
        T3StorageConfig storage=new();
        agent.MapGet("/t3-code/storage",()=>storage);
        agent.MapPost("/t3-code/storage",(T3StorageConfig request)=>storage=request);
        agent.MapPost("/t3-code/pair",IResult()=>available?Results.Json(new T3CodePairing("test/credential+only",expires)):Results.Conflict(new{error="Enable T3 Code first."}));
        agent.MapPost("/t3-code/prepare",(T3CodePrepareRequest request)=>{requestedVolume=request.Volume;return Results.Json(new{stage="Preparing"});});
        var frontBuilder=WebApplication.CreateBuilder();frontBuilder.Logging.ClearProviders();frontBuilder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));
        await using var front=frontBuilder.Build();var appliance=new Appliance();T3CodeEndpoints.MapEndpoints(front,appliance);
        try
        {
            await agent.StartAsync();await front.StartAsync();using var client=new HttpClient{BaseAddress=new Uri(front.Urls.Single())};client.DefaultRequestHeaders.Host="coding.test:8443";
            var status=await client.GetFromJsonAsync<T3CodeStatus>("/api/t3-code/status");
            check(status is {State:"Running",WorkloadId:"t3"},"Connection status reports the selected running T3 workload");
            var storageDefaults=await client.GetFromJsonAsync<T3StorageConfig>("/api/t3-code/storage");
            check(storageDefaults is {AllDrives:true,Models:true},"Storage endpoint returns broad defaults for existing installations");
            using var storageChanged=await client.PostAsJsonAsync("/api/t3-code/storage",new T3StorageConfig(false,true,["/data/projects"]));
            check(storageChanged.IsSuccessStatusCode&&storage is {AllDrives:false,Models:true}&&storage.Folders!.Single()=="/data/projects","Storage endpoint forwards the explicit folder selection");
            check(!ApiKeys.Allows("automation","POST","/api/t3-code/storage"),"An automation key cannot broaden T3's server file access");
            using var disabled=await client.PostAsJsonAsync("/api/t3-code/enabled",new T3CodeEnabled(false));
            check(disabled.IsSuccessStatusCode&&!enabled,"T3 disable requests reach the persistent lifecycle instead of profile operations");
            using var reenabled=await client.PostAsJsonAsync("/api/t3-code/enabled",new T3CodeEnabled(true));
            check(reenabled.IsSuccessStatusCode&&enabled,"T3 re-enable requests preserve the independent service identity");
            using var response=await client.PostAsJsonAsync("/api/t3-code/pair",new{});var pairing=await response.Content.ReadFromJsonAsync<JsonElement>();
            check(pairing.GetProperty("url").GetString()=="http://coding.test:3773/pair#token=test%2Fcredential%2Bonly" && pairing.GetProperty("expiresAt").GetDateTimeOffset()==expires,"Pairing uses the native T3 fragment and its expiry on the direct server port");
            check(response.Headers.CacheControl?.NoStore==true && pairing.GetProperty("qr").GetString()!.Contains("<svg"),"Pairing returns a local QR code with caching disabled");
            available=false;using var stopped=await client.PostAsJsonAsync("/api/t3-code/pair",new{});
            check(stopped.StatusCode==HttpStatusCode.Conflict && (await stopped.Content.ReadAsStringAsync()).Contains("Enable T3 Code"),"Pairing preserves actionable errors from a stopped workload");
            var volume="xur-volume-"+new string('b',32);using var preparation=await client.PostAsJsonAsync("/api/t3-code/prepare",new T3CodePrepareRequest(volume));
            check(preparation.IsSuccessStatusCode && requestedVolume==volume,"Preparation forwards the selected persistent storage to the agent");
        }
        finally
        {
            await front.StopAsync();await agent.StopAsync();appliance.Agent.Dispose();
            Environment.SetEnvironmentVariable("XUR_RUN",oldRun);Environment.SetEnvironmentVariable("XUR_MODE",oldMode);Directory.Delete(root,true);
        }
    }
}
