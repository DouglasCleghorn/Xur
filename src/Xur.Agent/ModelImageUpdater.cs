using System.Text.Json;
using System.Text.RegularExpressions;
using Xur.Domain;
namespace Xur.Agent;

public record ModelImageSelection(string Image,bool Recreate,string? Warning=null);

// A tag update does not change an existing container's image. Resolve the pull
// to its actual ID, then replace a stopped container only if its image differs.
public sealed class ModelImageUpdater(Func<string,string[],int,Task<ProcessResult>>? execute=null)
{
    readonly Func<string,string[],int,Task<ProcessResult>> run=execute??((exe,args,seconds)=>Processes.Run(exe,args,seconds));
    public async Task<ModelImageSelection?> Refresh(Workload workload,RuntimeInstance? instance)
    {
        if(instance?.State=="running")return null;
        var reference=EngineImages.For(workload.Recipe);
        ProcessResult pulled;
        try{pulled=await run("podman",["pull","--quiet","--policy=always","--arch=amd64",reference],900);}
        catch(OperationCanceledException){pulled=new(124,"Image pull timed out.");}
        string? warning=null;string image;
        if(pulled.ExitCode==0)image=Identity(pulled.Output.Split('\n')[0].Trim());
        else
        {
            var log=Redaction.Logs(pulled.Output);log=log[^Math.Min(1800,log.Length)..];
            var cached=await new CachedEngineImages(run).Newest(workload.Recipe);
            if(cached==null && instance!=null)
            {
                // A stopped container retains its last usable image even if its
                // tag was removed.
                cached=await StoppedImage(workload,instance);
            }
            if(cached==null)throw new InvalidOperationException("Latest engine image download failed for "+reference+" and no cached image is available: "+log);
            image=Identity(cached);
            warning="Latest engine image download failed; using the newest locally available image "+image+". "+log;
        }
        // Use pull's result, rather than re-reading a shared tag that another
        // simultaneous workload could have updated after our download.
        if(instance==null)return new(image,false,warning);
        if(await StoppedImage(workload,instance)==image)return new(image,false,warning);
        // No force flag: a concurrently started container must not be removed.
        var removed=await run("podman",["rm",instance.InstanceId],20);
        if(removed.ExitCode!=0)throw new InvalidOperationException("Could not replace the stopped engine with its latest image: "+Redaction.Logs(removed.Output));
        return new(image,true,warning);
    }
    async Task<string> StoppedImage(Workload workload,RuntimeInstance instance)
    {
        var inspected=await run("podman",["container","inspect",instance.InstanceId],20);
        if(inspected.ExitCode!=0)throw new InvalidOperationException("Could not inspect the stopped engine before updating it.");
        using var doc=JsonDocument.Parse(inspected.Output);var container=doc.RootElement[0];
        if(container.GetProperty("Id").GetString()!=instance.InstanceId || container.GetProperty("Config").GetProperty("Labels").GetProperty("io.xur.fingerprint").GetString()!=workload.Fingerprint)
            throw new InvalidOperationException("The engine instance changed while checking for updates.");
        if(container.GetProperty("State").GetProperty("Status").GetString()=="running" || container.GetProperty("State").GetProperty("Pid").GetInt32()!=0)
            throw new InvalidOperationException("The engine started outside this operation. Retry after stopping it.");
        return Identity(container.GetProperty("Image").GetString()!);
    }
    static string Identity(string image)
    {
        if(Regex.IsMatch(image,@"\A[a-f0-9]{64}\z"))image="sha256:"+image;
        if(!Regex.IsMatch(image,@"\Asha256:[a-f0-9]{64}\z"))throw new InvalidOperationException("The downloaded engine has an invalid image identity.");
        return image;
    }
}
