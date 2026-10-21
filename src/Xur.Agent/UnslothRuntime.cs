using System.Text.Json;
using System.Text.RegularExpressions;
using Xur.Domain;
namespace Xur.Agent;

public sealed class UnslothRuntime(Func<string,string[],int,Task<ProcessResult>>? execute=null)
{
    readonly Func<string,string[],int,Task<ProcessResult>> run=execute??((exe,args,seconds)=>Processes.Run(exe,args,seconds));
    public async Task<ModelImageSelection?> Refresh(RuntimeInstance? instance)
    {
        if(instance?.State=="running")return null;
        var pulled=await run("podman",["pull","--quiet","--policy=always",UnslothWorkload.Image],1800);
        if(pulled.ExitCode!=0)throw new InvalidOperationException("Could not pull Unsloth latest from mirror.gcr.io. Check the mirror and retry; no alternate registry is used. "+Redaction.Logs(pulled.Output));
        var image=pulled.Output.Trim();
        if(Regex.IsMatch(image,@"^[a-f0-9]{64}$"))image="sha256:"+image;
        if(!Regex.IsMatch(image,@"^sha256:[a-f0-9]{64}$"))throw new InvalidOperationException("Unsloth pull returned an invalid image identity.");
        if(instance==null)return new(image,false);
        var inspected=await run("podman",["container","inspect",instance.InstanceId],15);
        if(inspected.ExitCode!=0)throw new InvalidOperationException("Could not inspect the stopped Unsloth container.");
        using var doc=JsonDocument.Parse(inspected.Output);
        var container=doc.RootElement[0];
        if(container.GetProperty("State").GetProperty("Running").GetBoolean())throw new InvalidOperationException("Unsloth started while checking its image. Retry the profile change.");
        if(container.GetProperty("Image").GetString()==image)return new(image,false);
        var removed=await run("podman",["rm",instance.InstanceId],20);
        if(removed.ExitCode!=0)throw new InvalidOperationException("Could not replace the stopped Unsloth container. Persistent storage was retained.");
        return new(image,true);
    }
    public async Task EnsureStorage()
    {
        foreach(var mount in UnslothWorkload.Mounts)
        {
            var exists=await run("podman",["volume","exists",mount.Volume],15);
            if(exists.ExitCode==0)continue; // WorkloadRuntime verifies ownership before mounting.
            if(exists.ExitCode!=1)throw new InvalidOperationException("Could not inspect Unsloth persistent storage.");
            var created=await run("podman",["volume","create","--label","io.xur.volume="+mount.Volume,"--label","io.xur.name=Unsloth "+(mount.Destination=="/workspace"?"workspace and models":"Studio settings and outputs"),mount.Volume],20);
            if(created.ExitCode!=0)throw new InvalidOperationException("Could not create Unsloth persistent storage.");
        }
    }
    public static string[] ModelArguments(string state)
    {
        // Xur's content-addressed downloads must remain immutable. Training writes
        // belong in the writable workspace/Studio volumes, not the source cache.
        var args=new List<string>();
        foreach(var (folder,target) in new[]{("models","/models"),("model-sets","/model-sets")})
        {
            var path=Path.Combine(state,folder);Directory.CreateDirectory(path);
            args.AddRange(["--volume",path+":"+target+":ro,z"]);
        }
        return args.ToArray();
    }
}
