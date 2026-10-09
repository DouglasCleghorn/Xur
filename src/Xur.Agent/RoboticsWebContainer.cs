using System.Text.Json;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Xur.Domain;

namespace Xur.Agent;

// The host owns container setup and resource isolation. Robot settings, tools,
// calibration, model selection and motion sessions belong to the container app.
public sealed record RoboticsHardware(string[] Serial,string[] Cameras,string[] Controllers);

public sealed class RoboticsWebContainer(string runDirectory,
    Func<string,IEnumerable<string>,int,CancellationToken,Task<ProcessResult>>? run=null,
    string? stateDirectory=null,Func<RoboticsHardware>? hardware=null,Func<Task<bool>>? healthy=null,Func<string,bool>? deviceExists=null)
{
    public const string Name="xur-robot-web";
    public const string NightlyImage="ghcr.io/douglascleghorn/xur-robot:nightly";
    public const string RuntimeLabel="container-v1";
    public const string Unit="xur-robot-container";
    string SocketDirectory=>Path.Combine(runDirectory,"robot-web");
    string Ownership=>Path.Combine(runDirectory,"robot-ownership","active");
    string StateDirectory=>stateDirectory??"/var/lib/xur/robotics";
    string BootId=>File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
    Task<ProcessResult> Command(string executable,IEnumerable<string> args,int seconds=30)=>run!=null?run(executable,args,seconds,CancellationToken.None):Processes.Run(executable,args,seconds);
    Task<ProcessResult> Run(IEnumerable<string> args,int seconds=30)=>Command("podman",args,seconds);

    // Only stable serial/camera aliases and physical gamepad event interfaces
    // are eligible. No host disks, USB bus nodes, keyboards or arbitrary /dev.
    public static RoboticsHardware Inventory(string devRoot="/dev",string sysRoot="/sys")
    {
        string[] Resolve(string directory,string pattern,string category)
        {
            if(!Directory.Exists(directory))return [];
            var nodes=new HashSet<string>(StringComparer.Ordinal);
            foreach(var path in Directory.GetFiles(directory,pattern))
            {
                var target=new FileInfo(path).ResolveLinkTarget(true)?.FullName;
                if(target==null||!File.Exists(target))continue;
                if(!Regex.IsMatch(target,"\\A"+Regex.Escape(devRoot)+category+"\\z"))
                    throw new InvalidOperationException("A robotics device alias points outside its allowed device category.");
                nodes.Add(target);
            }
            return nodes.Order(StringComparer.Ordinal).ToArray();
        }
        var serial=Resolve(Path.Combine(devRoot,"serial","by-id"),"*",@"/tty(?:USB|ACM)[0-9]+");
        var cameras=Resolve(Path.Combine(devRoot,"v4l","by-id"),"*index0",@"/video[0-9]+")
            .Concat(Resolve(Path.Combine(devRoot,"v4l","by-path"),"*index0",@"/video[0-9]+"))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var peripherals=StationDeviceInventoryReader.Read([],sysRoot,devRoot);
        if(peripherals.Errors.Length!=0)throw new InvalidOperationException("Could not verify controller interfaces for robotics container setup: "+string.Join(" ",peripherals.Errors));
        var controllers=(peripherals.Controllers??[]).SelectMany(c=>c.Nodes)
            .Where(node=>Regex.IsMatch(node,"\\A"+Regex.Escape(devRoot)+@"/input/event[0-9]+\z"))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if(serial.Length+cameras.Length+controllers.Length>64)throw new InvalidOperationException("Robotics device inventory exceeds the supported container setup limit.");
        return new(serial,cameras,controllers);
    }

    sealed record ContainerIdentity(string WorkloadId,string Fingerprint,string InstanceId,int Pid,string Status,string Runtime,string Image);
    async Task<ContainerIdentity?> Identity(string name=Name)
    {
        var exists=await Run(["container","exists",name]);
        if(exists.ExitCode==1)return null;
        if(exists.ExitCode!=0)throw new InvalidOperationException("Could not inspect the robotics container.");
        var observed=await Run(["inspect",name]);
        if(observed.ExitCode!=0)throw new InvalidOperationException("Could not inspect the robotics container identity.");
        using var document=JsonDocument.Parse(observed.Output);var item=document.RootElement[0];
        var config=item.GetProperty("Config");var labels=config.GetProperty("Labels");
        string Label(string key)=>labels.ValueKind==JsonValueKind.Object&&labels.TryGetProperty(key,out var value)?value.GetString()??"":"";
        var state=item.GetProperty("State");
        return new(Label("io.xur.id"),Label("io.xur.fingerprint"),item.GetProperty("Id").GetString()!,state.GetProperty("Pid").GetInt32(),state.GetProperty("Status").GetString()!,Label("io.xur.robot.runtime"),config.GetProperty("Image").GetString()!);
    }
    public async Task<RuntimeInstance?> Inspect(Workload workload)
    {
        var observed=await Identity();if(observed==null)return null;
        if(observed.Runtime!=RuntimeLabel)
        {
            if(observed.WorkloadId.Length==0&&observed.Fingerprint.Length==0&&KnownLegacyWeb(observed.Image))return null;
            throw new InvalidOperationException("The robotics container has an unsupported runtime identity. Inspect it before continuing.");
        }
        if(observed.WorkloadId!=workload.Id||observed.Fingerprint!=workload.Fingerprint)
            throw new InvalidOperationException("Robotics container identity conflicts with its saved workload.");
        return new(workload.Id,workload.Fingerprint,observed.InstanceId,observed.Pid,BootId,"/robot",observed.Status,workload.Gpus);
    }
    public async Task<bool> Running()=>(await Identity())?.Status=="running";

    static bool KnownLegacyWeb(string image)=>new[]{"ghcr.io/douglascleghorn/xur-robot:","ghcr.io/douglascleghorn/xur-robot@sha256:","localhost/xur-robot-web:"}.Any(prefix=>image.StartsWith(prefix,StringComparison.Ordinal));
    // Migration only retires the fixed legacy containers. No robot settings,
    // calibration receipts, model files or persisted E-stop state are read.
    public async Task MigrateLegacy()
    {
        foreach(var name in new[]{"xur-robotics-tools",Name})
        {
            var previous=await Identity(name);if(previous==null||name==Name&&previous.Runtime==RuntimeLabel)continue;
            var known=name==Name?previous.WorkloadId.Length==0&&previous.Fingerprint.Length==0&&KnownLegacyWeb(previous.Image)
                :previous.Image.StartsWith("localhost/xur-robotics-tools:",StringComparison.Ordinal);
            if(!known)throw new InvalidOperationException("A legacy robotics container has an unexpected image or identity. Inspect it before migrating.");
            if(previous.Status=="running")
            {
                if(name=="xur-robotics-tools")
                {
                    var softwareStop=await Run(["exec",previous.InstanceId,"python","/opt/xur/bridge.py","--stop"],8);
                    if(softwareStop.ExitCode!=0)Console.Error.WriteLine("Legacy robotics software stop failed; stopping its container gracefully.");
                }
                if((await Run(["stop","--time=15",previous.InstanceId],25)).ExitCode!=0)throw new InvalidOperationException("The legacy robotics container did not stop; migration is withheld.");
            }
            var after=await Identity(name);
            if(after!=null&&(after.InstanceId!=previous.InstanceId||after.Status=="running"||after.Pid!=0))throw new InvalidOperationException("Legacy robotics container release could not be verified.");
            if(after!=null&&(await Run(["rm",previous.InstanceId])).ExitCode!=0)throw new InvalidOperationException("Could not remove the stopped legacy robotics container.");
        }
        File.Delete(Path.Combine(runDirectory,"robotics-controller"));
    }

    void Reserve()
    {
        var directory=Path.GetDirectoryName(Ownership)!;Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        File.WriteAllText(Ownership,"Robotics container owns gamepad interfaces.\n");
        File.SetUnixFileMode(Ownership,UnixFileMode.UserRead|UnixFileMode.UserWrite);
    }
    public async Task RestoreOwnership(Workload workload)
    {
        if((await Inspect(workload))?.State=="running")Reserve();
    }
    public void ReserveForRecovery()=>Reserve();
    void ReleaseOwnership()
    {
        try{File.Delete(Ownership);}
        catch(DirectoryNotFoundException){} // A first startup has no reservation directory.
    }
    public async Task ReconcileOwnership()
    {
        if(await Identity()==null)ReleaseOwnership();else Reserve();
    }
    async Task<bool> Healthy()
    {
        if(healthy!=null)return await healthy();
        using var client=LocalClient.Create(Path.Combine(SocketDirectory,"app.sock"));client.Timeout=TimeSpan.FromSeconds(2);
        try{using var health=await client.GetAsync("/robot/health");return health.IsSuccessStatusCode;}
        catch(HttpRequestException){return false;}catch(TaskCanceledException){return false;}
    }
    public async Task<RuntimeInstance> Start(Workload workload)
    {
        if(workload.Recipe.Kind!="Robotics"||!ProfilePolicy.EntityIdentifier(workload.Id))throw new InvalidOperationException("Choose a valid robotics workload.");
        await MigrateLegacy();
        var current=await Inspect(workload);
        if(current?.State=="running")
        {
            Reserve();
            if(!await Healthy())throw new InvalidOperationException("The running robotics container is unavailable. Open its logs before restarting.");
            return current;
        }
        var image=Environment.GetEnvironmentVariable("XUR_ROBOT_IMAGE")??NightlyImage;
        if(!image.StartsWith("ghcr.io/",StringComparison.Ordinal)&&!image.StartsWith("localhost/",StringComparison.Ordinal))
            throw new InvalidOperationException("Use the published GHCR robot image or an explicitly prepared localhost image.");
        var exists=await Run(["image","exists",image]);
        if(exists.ExitCode is not (0 or 1))throw new InvalidOperationException("Could not inspect the robotics image.");
        if(image.StartsWith("ghcr.io/",StringComparison.Ordinal))
        {
            var pulled=await Run(["pull",image],900);
            if(pulled.ExitCode!=0)throw new InvalidOperationException("Could not pull the robotics nightly container from GHCR: "+Redaction.Logs(pulled.Output));
        }
        else if(exists.ExitCode!=0)throw new InvalidOperationException("The prepared localhost robotics image is unavailable.");
        var runtime=await Run(["image","inspect","--format","{{index .Labels \"io.xur.robot.runtime\"}}",image]);
        if(runtime.ExitCode!=0||runtime.Output.Trim()!=RuntimeLabel)throw new InvalidOperationException("The selected robotics image does not contain the standalone container runtime. Pull a current robotics nightly image.");
        if(current!=null)
        {
            if(current.Pid!=0)throw new InvalidOperationException("The previous robotics container still has processes. Stop it before restarting.");
            if((await Run(["rm",current.InstanceId])).ExitCode!=0)throw new InvalidOperationException("Could not remove the stopped robotics container.");
        }
        var inventory=hardware?.Invoke()??Inventory();
        foreach(var (nodes,pattern) in new[]{(inventory.Serial,@"\A/dev/tty(?:USB|ACM)[0-9]+\z"),(inventory.Cameras,@"\A/dev/video[0-9]+\z"),(inventory.Controllers,@"\A/dev/input/event[0-9]+\z")})
            if(nodes.Any(node=>!Regex.IsMatch(node,pattern)||!(deviceExists?.Invoke(node)??File.Exists(node))))throw new InvalidOperationException("A robotics device is disconnected or outside its allowed device category.");
        Directory.CreateDirectory(SocketDirectory);Directory.CreateDirectory(StateDirectory);
        File.SetUnixFileMode(SocketDirectory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        File.SetUnixFileMode(StateDirectory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        File.Delete(Path.Combine(SocketDirectory,"app.sock"));Reserve();
        var args=new List<string>{"run","--detach","--name",Name,"--pull=never","--restart=no",
            "--label","io.xur.id="+workload.Id,"--label","io.xur.fingerprint="+workload.Fingerprint,
            "--network=bridge","--cap-drop=ALL","--security-opt=no-new-privileges","--security-opt=label=disable","--read-only",
            "--pids-limit=4096","--memory=4g","--cpus=4","--shm-size=512m","--tmpfs=/tmp:rw,nosuid,nodev,size=512m",
            "--env","XUR_ROBOT_WORKLOAD_ID="+workload.Id,
            "--volume",StateDirectory+":/state:rw","--volume",SocketDirectory+":/run/xur/robot-web:rw"};
        foreach(var node in inventory.Serial.Concat(inventory.Cameras).Concat(inventory.Controllers).Distinct(StringComparer.Ordinal))args.AddRange(["--device",node+":"+node+":rw"]);
        foreach(var alias in new[]{"/dev/serial/by-id","/dev/v4l/by-id","/dev/v4l/by-path"})if(Directory.Exists(alias))args.AddRange(["--volume",alias+":"+alias+":ro"]);
        args.AddRange(TimezoneSettings.ContainerArguments());args.Add(image);
        // Keep conmon outside xur-agent.service, so application upgrades cannot
        // terminate the independent robotics app or its motor stop handler.
        await Command("systemctl",["stop",Unit+".service"],20);
        await Command("systemctl",["reset-failed",Unit+".service"],10);
        var launch=new List<string>{"--unit="+Unit,"--collect","--property=Type=oneshot","--property=RemainAfterExit=yes","--property=TimeoutStartSec=60","/usr/bin/podman"};
        launch.AddRange(args);
        var created=await Command("systemd-run",launch,75);
        if(created.ExitCode!=0)
        {
            // Keep ownership if a partial create left processes behind.
            if((await Identity())==null)ReleaseOwnership();
            throw new InvalidOperationException("Could not start the robotics container: "+Redaction.Logs(created.Output));
        }
        try
        {
            for(var attempt=0;attempt<60;attempt++)
            {
                if(await Healthy())return await Inspect(workload)??throw new InvalidOperationException("The robotics container disappeared during startup.");
                if((await Inspect(workload))?.State!="running")break;
                await Task.Delay(250);
            }
            throw new InvalidOperationException("The robotics container did not become ready. Open its workload logs.");
        }
        catch
        {
            var failed=await Inspect(workload);
            if(failed!=null)await Stop(new(workload.Id,failed.InstanceId,failed.Pid,failed.BootId));
            throw;
        }
    }
    public async Task Stop(RuntimeStop request)
    {
        await MigrateLegacy();
        var current=await Identity();
        if(current==null)
        {
            await Command("systemctl",["stop",Unit+".service"],20);
            ReleaseOwnership();return;
        }
        if(current.WorkloadId!=request.Id||current.InstanceId!=request.InstanceId||(current.Status=="running"||current.Pid>0)&&
            (request.Pid!=null&&current.Pid!=request.Pid||request.BootId!=null&&request.BootId!=BootId))
            throw new InvalidOperationException("The robotics container changed outside this stop operation.");
        if(current.Status=="running"||current.Pid>0)
        {
            // Give the app first chance to stop tools and disable torque. Its
            // shutdown handler also repeats this when podman sends SIGTERM.
            if(File.Exists(Path.Combine(SocketDirectory,"app.sock")))
            {
                using var client=LocalClient.Create(Path.Combine(SocketDirectory,"app.sock"));client.Timeout=TimeSpan.FromSeconds(8);
                try
                {
                    using var stopped=await client.PostAsJsonAsync("/robot/api/stop",new{});
                    if(!stopped.IsSuccessStatusCode)Console.Error.WriteLine("Robotics software stop returned "+(int)stopped.StatusCode+"; stopping its container gracefully.");
                }
                catch(Exception e) when(e is HttpRequestException or TaskCanceledException){Console.Error.WriteLine("Robotics software stop unavailable; stopping container gracefully: "+Redaction.Logs(e.Message));}
            }
            if((await Run(["stop","--time=15",current.InstanceId],25)).ExitCode!=0)throw new InvalidOperationException("The robotics container did not stop. Keep gamepads reserved and inspect motor power.");
        }
        var after=await Identity();
        if(after!=null&&(after.InstanceId!=current.InstanceId||after.Status=="running"||after.Pid!=0))throw new InvalidOperationException("Robotics container release could not be verified.");
        if(after!=null&&(await Run(["rm",current.InstanceId])).ExitCode!=0)throw new InvalidOperationException("Could not remove the stopped robotics container.");
        await Command("systemctl",["stop",Unit+".service"],20);
        File.Delete(Path.Combine(SocketDirectory,"app.sock"));ReleaseOwnership();
    }
    public async Task<string> Logs()
    {
        var result=await Run(["logs","--tail=200",Name],10);return Redaction.Logs(result.Output);
    }
}
