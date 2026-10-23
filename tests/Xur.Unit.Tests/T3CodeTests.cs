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
        check(ProfilePolicy.Steps(new("p","Coding",1,[renamed]),observed).Any(s=>s.Kind=="Keep"),"T3 Code survives profile and route changes with the same persistent storage");
        var reuse=T3CodeWorkload.Prepare(recipe.Container!.Mounts[0].Volume);
        check(reuse.Volumes![0].Volume==recipe.Container.Mounts[0].Volume,"Preparing a replacement T3 Code image can reuse its home volume");
        foreach(var invalid in new[]{recipe with{Port=8080},recipe with{Command=[]},recipe with{Engine="Unknown"},recipe with{Container=recipe.Container with{Mounts=[]}},recipe with{Container=recipe.Container with{Mounts=[recipe.Container.Mounts[0] with{ReadOnly=true}]}}})
        {bool rejected=false;try{ProfilePolicy.ValidateRecipe(invalid);}catch(InvalidOperationException){rejected=true;}check(rejected,"Invalid T3 Code runtime contract is rejected");}
        bool duplicate=false;try{ProfilePolicy.Validate(new("p","Two servers",1,[workload,workload with{Id="t3-other",Route="other"}]),observed);}catch(InvalidOperationException){duplicate=true;}
        check(duplicate,"Only one T3 Code server can own the connection address");
        check(!ApiKeys.Allows("automation","POST","/api/t3-code/pair"),"Automation keys cannot mint T3 Code device credentials");
        check(T3CodeEndpoints.Origin(new HostString("[fd00::1]:8443"))=="http://[fd00::1]:3773","T3 Code links use its direct port and preserve IPv6 hosts");
        check(T3CodeEndpoints.Origin(new HostString("coding.test:8443"))=="http://coding.test:3773","T3 Code links target the server hostname rather than the browsing device");
        await PersistentUpdates(check);
        await Preparation(check);
        await T3CodeGpuTests.Run(check);
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
        var root=Path.GetFullPath(".build/evidence/t3-updates-test-"+Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root+"/bin");Directory.CreateDirectory(root+"/home/projects");
        await File.WriteAllTextAsync(root+"/home/projects/keep.txt","saved project");
        var preset=T3CodeWorkload.Prepare();
        await File.WriteAllTextAsync(root+"/launcher",preset.Files!.Single(f=>f.Name=="t3-launcher").Content);
        await File.WriteAllTextAsync(root+"/bin/curl","""
            #!/bin/sh
            set -eu
            test ! -f "$XUR_TEST_ROOT/offline" || exit 22
            printf '%s\n' "$*" >> "$XUR_TEST_ROOT/downloads"
            while [ "$#" -gt 0 ]; do
                if [ "$1" = -o ]; then cp "$XUR_TEST_ROOT/installer" "$2"; exit 0; fi
                shift
            done
            exit 1
            """);
        File.SetUnixFileMode(root+"/bin/curl",UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(root+"/installer","""
            #!/bin/sh
            set -eu
            test ! -f "$XUR_TEST_ROOT/install-fails" || exit 23
            mkdir -p "$T3CODE_HOME/runtime/versions/initial" "$T3CODE_INSTALL_BIN_DIR"
            cp "$XUR_TEST_ROOT/runtime" "$T3CODE_HOME/runtime/versions/initial/t3"
            chmod 755 "$T3CODE_HOME/runtime/versions/initial/t3"
            ln -sfn "$T3CODE_HOME/runtime/versions/initial/t3" "$T3CODE_INSTALL_BIN_DIR/t3"
            """);
        await File.WriteAllTextAsync(root+"/runtime","""
            #!/bin/sh
            set -eu
            if [ "${1:-}" = update ]; then
                mkdir -p "$T3CODE_HOME/runtime/versions/updated"
                cp "$XUR_TEST_ROOT/runtime" "$T3CODE_HOME/runtime/versions/updated/t3"
                chmod 755 "$T3CODE_HOME/runtime/versions/updated/t3"
                ln -sfn "$T3CODE_HOME/runtime/versions/updated/t3" "$T3CODE_INSTALL_BIN_DIR/t3"
                exit 0
            fi
            readlink "$T3CODE_INSTALL_BIN_DIR/t3"
            printf '<%s>\n' "$@"
            """);
        async Task<(int ExitCode,string Output)> Run(string executable,params string[] arguments)
        {
            var start=new ProcessStartInfo(executable){RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var argument in arguments)start.ArgumentList.Add(argument);
            start.Environment["PATH"]=root+"/bin:"+Environment.GetEnvironmentVariable("PATH");
            start.Environment["T3CODE_HOME"]=root+"/home/.t3";
            start.Environment["T3CODE_INSTALL_BIN_DIR"]=root+"/home/.local/bin";
            start.Environment["XUR_TEST_ROOT"]=root;
            using var process=Process.Start(start)!;
            var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return(process.ExitCode,await stdout+await stderr);
        }
        try
        {
            await File.WriteAllTextAsync(root+"/offline","");
            var unavailable=await Run("sh",root+"/launcher","start");
            check(unavailable.ExitCode!=0 && !File.Exists(root+"/home/.local/bin/t3"),"A failed first T3 download stops startup without publishing an installation");
            File.Delete(root+"/offline");await File.WriteAllTextAsync(root+"/install-fails","");
            var failed=await Run("sh",root+"/launcher","start");
            check(failed.ExitCode!=0 && !File.Exists(root+"/home/.local/bin/t3"),"A failed native installer stops T3 startup and remains retryable");
            File.Delete(root+"/install-fails");
            var first=await Run("sh",root+"/launcher","start","--base-dir",root+"/home/.t3","project with spaces");
            check(first.ExitCode==0 && first.Output.Contains("/versions/initial/t3") && first.Output.Contains("<project with spaces>"),"First T3 startup installs into its persistent home and preserves command arguments");
            var updated=await Run(root+"/home/.local/bin/t3","update");
            check(updated.ExitCode==0,"The native T3 command can select an updated runtime in persistent storage");
            var downloads=await File.ReadAllTextAsync(root+"/downloads");
            await File.WriteAllTextAsync(root+"/offline","");
            // A replacement image contributes only its launcher; it must honor the saved selection.
            await File.WriteAllTextAsync(root+"/replacement-launcher",T3CodeWorkload.Launcher);
            var restarted=await Run("sh",root+"/replacement-launcher","start");
            var pairing=await Run("sh",root+"/replacement-launcher","auth","pairing","create");
            check(restarted.ExitCode==0 && restarted.Output.Contains("/versions/updated/t3") && pairing.ExitCode==0 && pairing.Output.Contains("/versions/updated/t3"),"Restart and pairing use the updated T3 runtime after container replacement, even offline");
            check(await File.ReadAllTextAsync(root+"/downloads")==downloads && await File.ReadAllTextAsync(root+"/home/projects/keep.txt")=="saved project","T3 restarts leave installed updates and project data intact without downloading again");
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
        var expires=DateTimeOffset.UtcNow.AddMinutes(5);
        agent.MapGet("/t3-code/status",()=>new T3CodeStatus("t3","Running"));
        agent.MapPost("/t3-code/pair",IResult()=>available?Results.Json(new T3CodePairing("test/credential+only",expires)):Results.Conflict(new{error="Load a profile with T3 Code first."}));
        agent.MapPost("/t3-code/prepare",(T3CodePrepareRequest request)=>{requestedVolume=request.Volume;return Results.Json(new{stage="Preparing"});});
        var frontBuilder=WebApplication.CreateBuilder();frontBuilder.Logging.ClearProviders();frontBuilder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));
        await using var front=frontBuilder.Build();var appliance=new Appliance();T3CodeEndpoints.MapEndpoints(front,appliance);
        try
        {
            await agent.StartAsync();await front.StartAsync();using var client=new HttpClient{BaseAddress=new Uri(front.Urls.Single())};client.DefaultRequestHeaders.Host="coding.test:8443";
            var status=await client.GetFromJsonAsync<T3CodeStatus>("/api/t3-code/status");
            check(status is {State:"Running",WorkloadId:"t3"},"Connection status reports the selected running T3 workload");
            using var response=await client.PostAsJsonAsync("/api/t3-code/pair",new{});var pairing=await response.Content.ReadFromJsonAsync<JsonElement>();
            check(pairing.GetProperty("url").GetString()=="http://coding.test:3773/pair#token=test%2Fcredential%2Bonly" && pairing.GetProperty("expiresAt").GetDateTimeOffset()==expires,"Pairing uses the native T3 fragment and its expiry on the direct server port");
            check(response.Headers.CacheControl?.NoStore==true && pairing.GetProperty("qr").GetString()!.Contains("<svg"),"Pairing returns a local QR code with caching disabled");
            available=false;using var stopped=await client.PostAsJsonAsync("/api/t3-code/pair",new{});
            check(stopped.StatusCode==HttpStatusCode.Conflict && (await stopped.Content.ReadAsStringAsync()).Contains("Load a profile"),"Pairing preserves actionable errors from a stopped workload");
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
