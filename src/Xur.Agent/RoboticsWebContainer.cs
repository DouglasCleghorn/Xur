using Xur.Domain;

namespace Xur.Agent;

// The web container gets the private agent socket, never hardware or a container-engine socket.
public sealed class RoboticsWebContainer(string runDirectory,Func<string,IEnumerable<string>,int,CancellationToken,Task<ProcessResult>>? run=null)
{
    public const string Name="xur-robot-web";
    public const string NightlyImage="ghcr.io/douglascleghorn/xur-robot:nightly";
    Task<ProcessResult> Run(IEnumerable<string> args,int seconds=30)=>run!=null?run("podman",args,seconds,CancellationToken.None):Processes.Run("podman",args,seconds);
    public async Task<bool> Running()
    {
        var exists=await Run(["container","exists",Name]);
        if(exists.ExitCode==1)return false;
        if(exists.ExitCode!=0)throw new InvalidOperationException("Could not inspect the robot web container.");
        var observed=await Run(["inspect","--format","{{.State.Running}}",Name]);
        if(observed.ExitCode!=0)throw new InvalidOperationException("Could not inspect the robot web session.");
        return observed.Output.Trim()=="true";
    }
    public async Task Start()
    {
        var image=Environment.GetEnvironmentVariable("XUR_ROBOT_IMAGE")??NightlyImage;
        if(!image.StartsWith("ghcr.io/",StringComparison.Ordinal)&&!image.StartsWith("localhost/",StringComparison.Ordinal))
            throw new InvalidOperationException("Use the published GHCR robot image or an explicitly prepared localhost image.");
        var exists=await Run(["image","exists",image]);
        if(exists.ExitCode is not (0 or 1))throw new InvalidOperationException("Could not inspect the robot web image.");
        if(exists.ExitCode==1)
        {
            var pulled=await Run(["pull",image],180);
            if(pulled.ExitCode!=0)throw new InvalidOperationException("Could not pull the robot nightly container from GHCR: "+Redaction.Logs(pulled.Output));
        }
        var present=await Run(["container","exists",Name]);
        if(present.ExitCode is not (0 or 1))throw new InvalidOperationException("Could not inspect the robot web container.");
        if(present.ExitCode==0)
        {
            var running=await Run(["inspect","--format","{{.State.Running}}",Name]);
            if(running.ExitCode!=0)throw new InvalidOperationException("Could not inspect the robot web session.");
            if(running.Output.Trim()=="true")return;
            if((await Run(["rm",Name])).ExitCode!=0)throw new InvalidOperationException("Could not remove the stopped robot web container.");
        }
        var socketDirectory=Path.Combine(runDirectory,"robot-web");Directory.CreateDirectory(socketDirectory);
        File.SetUnixFileMode(socketDirectory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        File.Delete(Path.Combine(socketDirectory,"app.sock"));
        var created=await Run(["run","--detach","--name",Name,"--pull=never","--restart=no",
            "--network=none","--cap-drop=ALL","--security-opt=no-new-privileges","--security-opt=label=disable","--read-only","--pids-limit=128",
            "--memory=256m","--cpus=1","--tmpfs=/tmp:rw,nosuid,nodev,size=16m",
            "--volume",runDirectory+":/run/xur:ro",
            "--volume",socketDirectory+":/run/xur/robot-web:rw",
            image],60);
        if(created.ExitCode!=0)throw new InvalidOperationException("Could not start the robot web container: "+Redaction.Logs(created.Output));
        try
        {
            using var client=LocalClient.Create(Path.Combine(socketDirectory,"app.sock"));client.Timeout=TimeSpan.FromSeconds(2);
            for(var attempt=0;attempt<30;attempt++)
            {
                try{using var health=await client.GetAsync("/robot/health");if(health.IsSuccessStatusCode)return;}catch(HttpRequestException){}catch(TaskCanceledException){}
                await Task.Delay(100);
            }
            throw new InvalidOperationException("The robot web container did not become ready.");
        }
        catch{await Stop();throw;}
    }
    public async Task Stop()
    {
        var exists=await Run(["container","exists",Name]);
        if(exists.ExitCode==1)return;
        if(exists.ExitCode!=0)throw new InvalidOperationException("Could not inspect the robot web container during shutdown.");
        if((await Run(["rm","--force",Name])).ExitCode!=0)throw new InvalidOperationException("Could not stop the robot web container.");
        File.Delete(Path.Combine(runDirectory,"robot-web","app.sock"));
    }
}
