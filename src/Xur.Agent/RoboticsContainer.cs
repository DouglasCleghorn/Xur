using Xur.Domain;

namespace Xur.Agent;

public sealed class RoboticsContainer(string directory, Func<string,IEnumerable<string>,int,CancellationToken,Task<ProcessResult>>? run = null)
{
    public const string Name="xur-robotics-tools";
    Task<ProcessResult> Run(IEnumerable<string> arguments,int seconds,CancellationToken cancellation)=>
        run!=null?run("podman",arguments,seconds,cancellation):Processes.Run("podman",arguments,seconds,cancellation);

    async Task<string> Image(CancellationToken cancellation,bool discovery=false)
    {
        var source=Path.Combine(AppContext.BaseDirectory,"robotics");
        var recipe=discovery?"Discovery.Containerfile":"Containerfile";
        var files=discovery?new[]{recipe,"discover_buses.py","LICENSE","licensing.md"}:new[]{recipe,"bridge.py","discover_buses.py","motor_probe.py","detect_markers.c","LICENSE","licensing.md"};
        var image=(discovery?"localhost/xur-robotics-discovery:":"localhost/xur-robotics-tools:")+Canonical.Hash(files.Select(name=>File.ReadAllText(Path.Combine(source,name))).ToArray())[..20];
        var exists=await Run(["image","exists",image],15,cancellation);
        if(exists.ExitCode is not (0 or 1))throw new InvalidOperationException("Could not inspect the local robotics image cache.");
        if(exists.ExitCode!=0)
        {
            var context=Path.Combine(directory,".build","container");Directory.CreateDirectory(context);
            foreach(var name in files)
                File.Copy(Path.Combine(source,name),Path.Combine(context,name),true);
            var result=await Run(["build","--tag",image,"--file",Path.Combine(context,recipe),context],1800,cancellation);
            if(result.ExitCode!=0)throw new InvalidOperationException("Robotics image preparation failed. Docker Hub cache misses must be resolved through an approved registry; no Hub fallback is used. "+Redaction.Logs(result.Output[^Math.Min(result.Output.Length,2048)..]));
        }
        return image;
    }

    public async Task Prepare(RoboticsConfiguration configuration,CancellationToken cancellation)
    {
        var image=await Image(cancellation);
        var present=await Run(["container","exists",Name],15,cancellation);
        if(present.ExitCode is not (0 or 1))throw new InvalidOperationException("Could not inspect the robotics container.");
        if(present.ExitCode==0)
        {
            var running=await Run(["inspect","--format","{{.State.Running}}",Name],10,cancellation);
            if(running.ExitCode!=0)throw new InvalidOperationException("Could not inspect the robotics tools session.");
            if(running.Output.Trim()=="true" && (await Run(["exec",Name,"python","/opt/xur/bridge.py","--idle"],10,cancellation)).ExitCode!=0)
                throw new InvalidOperationException("Finish or stop the interactive calibration before replacing its container.");
            var remove=await Run(["rm","--force",Name],20,cancellation);
            if(remove.ExitCode!=0)throw new InvalidOperationException("Could not replace the previous robotics tools container.");
        }
        Directory.CreateDirectory(Path.Combine(directory,".build"));
        Directory.CreateDirectory(Path.Combine(directory,"calibration"));
        var args=new List<string>{"run","--detach","--name",Name,"--pull=never","--restart=no",
            "--cap-drop=ALL","--security-opt=no-new-privileges","--pids-limit=1024","--network=none",
            "--volume",directory+":/state:rw,Z"};
        foreach(var (device,destination) in new[]{(configuration.LeftPort,"/dev/arm_left"),(configuration.RightPort,"/dev/arm_right"),
            (configuration.ControllerDevice,"/dev/xbox"),(configuration.HeadCamera,"/dev/camera_head"),(configuration.HandCamera,"/dev/camera_hand")})
        {
            if(destination=="/dev/xbox" && device=="")continue;
            if(!File.Exists(device))throw new InvalidOperationException("Robot device is disconnected: "+device);
            var resolved=File.ResolveLinkTarget(device,true)?.FullName??device;
            args.AddRange(["--device",resolved+":"+destination+":rw"]);
        }
        args.Add(image);
        var mapping=new Dictionary<string,string>{{"leftPort",configuration.LeftPort},{"rightPort",configuration.RightPort},
            {"controllerDevice",configuration.ControllerDevice},{"headCamera",configuration.HeadCamera},{"handCamera",configuration.HandCamera}};
        File.WriteAllText(Path.Combine(directory,".build","devices.json"),System.Text.Json.JsonSerializer.Serialize(mapping));
        var created=await Run(args,60,cancellation);
        if(created.ExitCode!=0)throw new InvalidOperationException("Could not start the robotics tools container: "+Redaction.Logs(created.Output));
    }
    public async Task<RobotBusDetection> DetectBuses(string[] ports,CancellationToken cancellation)
    {
        if(ports.Length!=2 || ports.Distinct(StringComparer.Ordinal).Count()!=2)
            throw new InvalidOperationException("Automatic XLeRobot detection requires exactly two distinct serial/by-id adapters.");
        var image=await Image(cancellation,discovery:true);
        const string name="xur-robotics-discovery";
        var args=new List<string>{"run","--rm","--name",name,"--pull=never","--restart=no",
            "--cap-drop=ALL","--security-opt=no-new-privileges","--pids-limit=64","--network=none"};
        for(var i=0;i<ports.Length;i++)
        {
            if(!File.Exists(ports[i]))throw new InvalidOperationException("Serial adapter disconnected during discovery.");
            var resolved=File.ResolveLinkTarget(ports[i],true)?.FullName??ports[i];
            args.AddRange(["--device",resolved+":/dev/candidate"+i+":rw"]);
        }
        args.AddRange(["--entrypoint","python",image,"/opt/xur/discover_buses.py","/dev/candidate0","/dev/candidate1"]);
        try
        {
            var result=await Run(args,60,cancellation);
            if(result.ExitCode!=0)throw new InvalidOperationException("Read-only upstream bus discovery failed: "+Redaction.Logs(result.Output[^Math.Min(result.Output.Length,2048)..]));
            // Processes.Run appends stderr after stdout. Consume the one JSON
            // value, preserving compatibility with Podman warning messages.
            var reader=new System.Text.Json.Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(result.Output));
            var inventories=System.Text.Json.JsonSerializer.Deserialize<Dictionary<int,int>[]>(ref reader)
                ??throw new InvalidOperationException("Bus discovery returned no inventory.");
            if(inventories.Length!=ports.Length)throw new InvalidOperationException("Bus discovery returned an incomplete inventory.");
            return RobotBusDetection.MatchXLeRobot(ports.Select((port,i)=>new RobotBusInventory(port,inventories[i])).ToArray());
        }
        finally{await Run(["rm","--force","--ignore",name],15,CancellationToken.None);}
    }
    public async Task Interrupt()
    {
        var exists=await Run(["container","exists",Name],10,CancellationToken.None);
        if(exists.ExitCode==1)return;
        if(exists.ExitCode!=0)throw new InvalidOperationException("Could not inspect robotics tools during software stop. Inspect motor power.");
        var running=await Run(["inspect","--format","{{.State.Running}}",Name],10,CancellationToken.None);
        if(running.ExitCode!=0)throw new InvalidOperationException("Could not inspect the robotics tools session.");
        if(running.Output.Trim()!="true")return;
        var signal=await Run(["exec",Name,"python","/opt/xur/bridge.py","--stop"],5,CancellationToken.None);
        if(signal.ExitCode!=0)
        {
            await Stop();
            throw new InvalidOperationException("Upstream software stop failed; tools container stopped. Inspect motor power before resetting.");
        }
        for(var attempt=0;attempt<5;attempt++)
        {
            if((await Run(["exec",Name,"python","/opt/xur/bridge.py","--idle"],2,CancellationToken.None)).ExitCode==0)return;
            await Task.Delay(200);
        }
        await Stop();
    }
    public async Task Stop()
    {
        var exists=await Run(["container","exists",Name],10,CancellationToken.None);
        if(exists.ExitCode==1)return;
        if(exists.ExitCode!=0)throw new InvalidOperationException("Could not inspect robotics tools during shutdown. Inspect motor power.");
        var result=await Run(["stop","--time=10",Name],20,CancellationToken.None);
        if(result.ExitCode!=0)throw new InvalidOperationException("Robotics container did not stop; inspect it before restarting.");
    }
}
