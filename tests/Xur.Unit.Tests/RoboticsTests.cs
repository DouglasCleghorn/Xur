using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xur.Agent;
using Xur.Control;
using Xur.Domain;

// The host owns only profile exclusivity, container lifecycle and proxy access.
// Motor, camera, policy, calibration and persistent settings tests live in Xur.Robot.Tests.
static class RoboticsTests
{
    static readonly Recipe Recipe=new("xlerobot","Robot","host:xlerobot",[],0,"","CPU",0,0,"",Kind:"Robotics",Engine:"XLeRobot");
    static bool Rejected(Action action){try{action();return false;}catch(InvalidOperationException){return true;}}
    public static async Task Run(Action<bool,string> check)
    {
        var workload=new Workload("robot","Robot",Recipe,[],"robot");
        ProfilePolicy.Validate(new("robot","Robot",1,[workload]),new("",[],[]));
        var desktop=new Workload("desktop","Desktop",new("gaming-workstation","Desktop","host:plasma",[],0,"","Display",1,0,"",Kind:"Workstation",Engine:"Plasma"),["gpu"],"desktop");
        check(Rejected(()=>ProfilePolicy.Validate(new("robot","Robot",1,[workload,desktop]),new("",[],[]))),"Robotics rejects shared workstation/controller ownership");
        check(Rejected(()=>ProfilePolicy.ValidateRecipe(Recipe with{Command=["drive"]})),"Robotics recipes cannot introduce arbitrary commands");
        foreach(var prefix in new[]{"/api/robotics/","/robot/api/"})
        {
            foreach(var operation in new[]{"arm","reset-estop","auto-calibrate","start-controller","configure","prepare","detect-buses","train","record","skills/evaluate","skills/review"})
                check(!ApiKeys.Allows("robotics","POST",prefix+operation),"Robotics keys cannot change operator setup: "+prefix+operation);
            check(ApiKeys.Allows("robotics","POST",prefix+"tasks")&&ApiKeys.Allows("robotics","POST",prefix+"stop")
                &&ApiKeys.Allows("robotics","GET",prefix+"cameras/head"),"Robotics keys invoke bounded tasks and see camera feedback through "+prefix);
            check(ApiKeys.Allows("robotics","POST",prefix+"estop"),"Robotics keys may latch E-stop through "+prefix);
            check(ApiKeys.Allows("robotics","GET",prefix+"jobs/"+new string('a',32)+"/markers")
                &&!ApiKeys.Allows("robotics","GET",prefix+"jobs/../../config/markers"),"Robotics keys read known reports without arbitrary file access through "+prefix);
            check(!ApiKeys.Allows("diagnostics","POST",prefix+"tasks")&&!ApiKeys.Allows("testing","POST",prefix+"tasks"),"Existing read/test keys do not gain robotics motion access through "+prefix);
        }
        check(!ApiKeys.Allows("robotics","POST","/api/power/reboot"),"Robotics keys do not gain host power control");
        await Container(workload,check);
        await Proxy(check);
        await Migration(workload,check);
    }
    static async Task Container(Workload workload,Action<bool,string> check)
    {
        // Host command stubs inspect the exact process boundary without touching devices.
        var root=Path.GetFullPath(".build/robot-host-"+Guid.NewGuid().ToString("N")[..12]);Directory.CreateDirectory(root);
        try
        {
            var state=Path.Combine(root,"state");var running=false;var present=false;List<string[]> calls=[];List<string[]> independentUnits=[];
            const string instance="abcdef0123456789";
            var container=new RoboticsWebContainer(root,(executable,arguments,_,_)=>
            {
                var args=arguments.ToArray();
                if(executable=="systemctl")return Task.FromResult(new ProcessResult(0,""));
                if(executable=="systemd-run"){independentUnits.Add(args);args=args[(Array.IndexOf(args,"/usr/bin/podman")+1)..];}
                calls.Add(args);
                if(args[0]=="container")return Task.FromResult(new ProcessResult(present&&args.Last()==RoboticsWebContainer.Name?0:1,""));
                if(args[0]=="image")return Task.FromResult(new ProcessResult(0,args[1]=="inspect"?RoboticsWebContainer.RuntimeLabel:""));
                if(args[0]=="run"){present=true;running=true;return Task.FromResult(new ProcessResult(0,instance));}
                if(args[0]=="stop"){running=false;return Task.FromResult(new ProcessResult(0,""));}
                if(args[0]=="rm"){present=false;running=false;return Task.FromResult(new ProcessResult(0,""));}
                if(args[0]=="inspect")
                {
                    return Task.FromResult(new ProcessResult(0,System.Text.Json.JsonSerializer.Serialize(new[]{new
                    {
                        Id=instance,
                        Config=new{Image=RoboticsWebContainer.NightlyImage,Labels=new Dictionary<string,string>{{"io.xur.id",workload.Id},{"io.xur.fingerprint",workload.Fingerprint},{"io.xur.robot.runtime",RoboticsWebContainer.RuntimeLabel}}},
                        State=new{Pid=running?1234:0,Status=running?"running":"exited"}
                    }})));
                }
                return Task.FromResult(new ProcessResult(0,""));
            },stateDirectory:state,hardware:()=>new(["/dev/ttyUSB7","/dev/ttyUSB8"],["/dev/video7","/dev/video8"],["/dev/input/event7"]),healthy:()=>Task.FromResult(true),deviceExists:_=>true);
            await container.ReconcileOwnership();
            check(!Directory.Exists(Path.Combine(root,"robot-ownership")),"First startup reconciles an absent robotics container without creating or requiring a reservation directory");
            var started=await container.Start(workload);
            var create=calls.Single(a=>a[0]=="run");
            check(independentUnits.Single().Contains("--unit=xur-robot-container")
                &&independentUnits.Single().Contains("--property=Type=oneshot")
                &&independentUnits.Single().Contains("--property=RemainAfterExit=yes"),
                "Robotics container process ownership survives host agent service updates through an independent systemd unit");
            check(create.Contains("--network=bridge")&&!create.Contains("--network=host")&&create.Contains("--read-only")&&create.Contains("--cap-drop=ALL")
                &&create.Contains("--restart=no")&&!create.Contains("--privileged")&&!create.Contains("--publish"),
                "Robotics container has no host network, published ports, privileges or automatic restart");
            check(create.Count(a=>a=="--device")==5&&!create.Any(a=>a.Contains("podman.sock",StringComparison.Ordinal))
                &&!create.Any(a=>a.Contains("agent.sock",StringComparison.Ordinal))&&!create.Contains(root+":/run/xur:ro"),
                "The host grants robot hardware without its agent socket, other run state or container engine");
            check(create.Contains(state+":/state:rw")&&create.Contains(Path.Combine(root,"robot-web")+":/run/xur/robot-web:rw"),
                "Only persistent robot state and the private app socket are writable host mounts");
            check(File.Exists(Path.Combine(root,"robot-ownership","active"))&&started.Id==workload.Id,
                "Loading the container reserves controller ownership at the host profile boundary");
            File.Delete(Path.Combine(root,"robot-ownership","active"));await container.RestoreOwnership(workload);
            check(File.Exists(Path.Combine(root,"robot-ownership","active")),"Host restart restores controller ownership from the observed running container without robot settings");
            var observed=await container.Inspect(workload);
            check(observed?.InstanceId==started.InstanceId&&observed.Pid==started.Pid,"Host observations identify the actual robot container process");
            bool rejected=false;try{await container.Stop(new(workload.Id,"wrong-instance",started.Pid,started.BootId));}catch(InvalidOperationException){rejected=true;}
            check(rejected&&running,"A stale runtime identity cannot stop another robot container");
            foreach(var stale in new[]{new RuntimeStop(workload.Id,started.InstanceId,started.Pid+1,started.BootId),new RuntimeStop(workload.Id,started.InstanceId,started.Pid,"wrong-boot")})
            {
                rejected=false;try{await container.Stop(stale);}catch(InvalidOperationException){rejected=true;}
                check(rejected&&running,"A stale process or boot identity cannot stop the current robot container");
            }
            await container.Stop(new(workload.Id,started.InstanceId,started.Pid,started.BootId));
            check(calls.Any(a=>a[0]=="stop")&&!File.Exists(Path.Combine(root,"robot-ownership","active"))
                &&await container.Inspect(workload)==null,"Profile unload stops the independent app and releases host controller ownership");
            container.ReserveForRecovery();check(File.Exists(Path.Combine(root,"robot-ownership","active")),"Uncertain setup conservatively reserves gamepad ownership for recovery");
            await container.ReconcileOwnership();check(!File.Exists(Path.Combine(root,"robot-ownership","active")),"Recovery releases reserved gamepads only after verifying the robotics container is absent");
            var failure=new RoboticsWebContainer(root,(_,_,_,_)=>Task.FromResult(new ProcessResult(125,"inspection error")),stateDirectory:state,
                hardware:()=>new([],[],[]),healthy:()=>Task.FromResult(true),deviceExists:_=>true);
            rejected=false;try{await failure.Start(workload);}catch(InvalidOperationException){rejected=true;}
            check(rejected,"Robot image observation failures do not attempt silent registry fallbacks");
        }
        finally{Directory.Delete(root,true);}
    }
    static async Task Migration(Workload workload,Action<bool,string> check)
    {
        var root=Path.GetFullPath(".build/robot-migration-"+Guid.NewGuid().ToString("N")[..8]);Directory.CreateDirectory(root);
        try
        {
            var states=new Dictionary<string,(string Id,string Image,bool Running)>
            {
                ["xur-robotics-tools"]=("old-tools","localhost/xur-robotics-tools:old",true),
                [RoboticsWebContainer.Name]=("old-relay",RoboticsWebContainer.NightlyImage,true)
            };
            List<string[]> calls=[];
            var container=new RoboticsWebContainer(root,(_,arguments,_,_)=>
            {
                var args=arguments.ToArray();calls.Add(args);var name=args.Last();
                if(args[0]=="container")return Task.FromResult(new ProcessResult(states.ContainsKey(name)?0:1,""));
                if(args[0]=="inspect")
                {
                    var state=states[name];return Task.FromResult(new ProcessResult(0,JsonSerializer.Serialize(new[]{new
                    {Id=state.Id,Config=new{Image=state.Image,Labels=new Dictionary<string,string>()},State=new{Pid=state.Running?100:0,Status=state.Running?"running":"exited"}}})));
                }
                if(args[0] is "stop" or "rm")
                {
                    var entry=states.Single(item=>item.Value.Id==name);
                    if(args[0]=="stop")states[entry.Key]=(entry.Value.Id,entry.Value.Image,false);else states.Remove(entry.Key);
                }
                return Task.FromResult(new ProcessResult(0,""));
            });
            File.WriteAllText(Path.Combine(root,"robotics-controller"),"legacy controller");await container.MigrateLegacy();
            check(states.Count==0&&calls.Any(a=>a.SequenceEqual(["exec","old-tools","python","/opt/xur/bridge.py","--stop"]))
                &&calls.Count(a=>a[0]=="stop"&&a.Contains("--time=15"))==2&&calls.Count(a=>a[0]=="rm")==2
                &&!calls.Any(a=>a.Contains("--force"))&&!File.Exists(Path.Combine(root,"robotics-controller")),
                "Legacy migration software-stops tools, gracefully stops immutable container identities and verifies removal");
            states["xur-robotics-tools"]=("unknown","example.invalid/unrecognized:old",true);calls.Clear();
            bool rejected=false;try{await container.MigrateLegacy();}catch(InvalidOperationException){rejected=true;}
            check(rejected&&!calls.Any(a=>a[0] is "exec" or "stop" or "rm"),"Unexpected legacy container images cannot be adopted or stopped by migration");
            calls.Clear();var obsolete=new RoboticsWebContainer(root,(_,arguments,_,_)=>
            {var args=arguments.ToArray();calls.Add(args);return Task.FromResult(new ProcessResult(args[0]=="container"?1:0,""));});
            rejected=false;try{await obsolete.Start(workload);}catch(InvalidOperationException){rejected=true;}
            check(rejected&&calls.Any(a=>a.Take(2).SequenceEqual(["image","inspect"]))&&!calls.Any(a=>a[0]=="run"),
                "An obsolete relay image cannot start without the standalone container runtime label");
        }
        finally{Directory.Delete(root,true);}
    }
    static async Task Proxy(Action<bool,string> check)
    {
        var root=Path.GetFullPath(".build/rp-"+Guid.NewGuid().ToString("N")[..8]);var sockets=Path.Combine(root,"robot-web");Directory.CreateDirectory(sockets);
        var previousRun=Environment.GetEnvironmentVariable("XUR_RUN");var previousMode=Environment.GetEnvironmentVariable("XUR_MODE");
        Environment.SetEnvironmentVariable("XUR_RUN",root);Environment.SetEnvironmentVariable("XUR_MODE","Installed");
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.ListenUnixSocket(Path.Combine(sockets,"app.sock")));
        await using var backend=builder.Build();List<(string Path,string Method,string Body,bool Secret)> calls=[];
        backend.MapMethods("/robot/{**path}",["GET","POST"],async(HttpContext context)=>
        {
            using var reader=new StreamReader(context.Request.Body);var body=await reader.ReadToEndAsync();
            calls.Add((context.Request.Path,context.Request.Method,body,context.Request.Headers.ContainsKey("Cookie")||context.Request.Headers.ContainsKey("Authorization")||context.Request.Headers.ContainsKey("Tailscale-User-Login")));
            context.Response.Headers.ContentSecurityPolicy="default-src 'self'; img-src 'self' blob: data:";
            return Results.Json(new{path=context.Request.Path.Value,method=context.Request.Method,body});
        });
        var controlBuilder=WebApplication.CreateBuilder();controlBuilder.Logging.ClearProviders();controlBuilder.Services.AddAntiforgery();controlBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var control=controlBuilder.Build();var appliance=new Appliance();control.MapRobotProxy(appliance);
        try
        {
            await backend.StartAsync();await control.StartAsync();using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=new(control.Urls.Single())};
            using var request=new HttpRequestMessage(HttpMethod.Get,"/robot/api/status");request.Headers.Add("Cookie","session=secret");request.Headers.Add("Authorization","Bearer secret");request.Headers.Add("Tailscale-User-Login","owner");
            using var status=await http.SendAsync(request);
            check(status.IsSuccessStatusCode&&calls.Last().Path=="/robot/api/status"&&!calls.Last().Secret,
                "Host proxy forwards directly to the container while stripping browser credentials and identity headers");
            check(status.Headers.GetValues("Content-Security-Policy").Single().Contains("blob: data:"),"Host proxy preserves the robotics camera content security policy");
            using var legacy=await http.GetAsync("/api/robotics/configuration");
            check(legacy.IsSuccessStatusCode&&calls.Last().Path=="/robot/api/configuration","Legacy robotics API routes reach the same container-owned configuration endpoint");
            using var configure=await http.PostAsJsonAsync("/robot/api/configure",new{robotId="fixture"});
            check(configure.IsSuccessStatusCode&&calls.Last().Method=="POST"&&calls.Last().Body.Contains("fixture"),"Host proxy forwards operator settings unchanged for validation and persistence inside the container");
            using var launcher=await http.GetAsync("/robotics");
            check(launcher.StatusCode==HttpStatusCode.Redirect&&launcher.Headers.Location?.OriginalString=="/robot/setup","The former host settings page redirects to container-owned setup");
            await backend.StopAsync();File.Delete(Path.Combine(sockets,"app.sock"));
            using var missing=await http.GetAsync("/robot/api/status");
            check(missing.StatusCode==HttpStatusCode.ServiceUnavailable&&(await missing.Content.ReadAsStringAsync()).Contains("Robotics workload"),"A stopped robotics container returns a useful workload-start message");
        }
        finally
        {
            await control.StopAsync();await backend.StopAsync();appliance.Agent.Dispose();
            Environment.SetEnvironmentVariable("XUR_RUN",previousRun);Environment.SetEnvironmentVariable("XUR_MODE",previousMode);Directory.Delete(root,true);
        }
    }
}
