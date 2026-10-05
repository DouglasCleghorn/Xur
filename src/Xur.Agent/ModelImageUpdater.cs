using System.Text.Json;
using System.Text.RegularExpressions;
using Xur.Domain;
namespace Xur.Agent;

public record ModelImageSelection(string Image,bool Recreate,string? Warning=null);

// A tag update does not change an existing container's image. Resolve the pull
// to its actual ID, then replace a stopped container only if its image differs.
public sealed class ModelImageUpdater(Func<string,string[],int,Task<ProcessResult>>? execute=null,string buildRoot="/var/lib/xur/engine-builds")
{
    readonly Func<string,string[],int,Task<ProcessResult>> run=execute??((exe,args,seconds)=>Processes.Run(exe,args,seconds));
    public async Task<ModelImageSelection?> Refresh(Workload workload,RuntimeInstance? instance)
    {
        if(instance?.State=="running")return null;
        var reference=EngineImages.For(workload.Recipe);
        var build=workload.Recipe.Engine=="vLLM-Omni"&&workload.Recipe.Vendor=="Intel";
        ProcessResult pulled;
        try{pulled=build?await BuildOmniXpu(reference):await run("podman",["pull","--quiet","--policy=always","--arch=amd64",reference],900);}
        catch(OperationCanceledException){pulled=new(124,"Engine image preparation timed out.");}
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
            var failure=build?"Engine image build failed":"Latest engine image download failed";
            if(cached==null)throw new InvalidOperationException(failure+" for "+reference+" and no cached image is available: "+log);
            image=Identity(cached);
            warning=failure+"; using the newest locally available image "+image+". "+log;
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
    async Task<ProcessResult> BuildOmniXpu(string reference)
    {
        // A private context and iidfile avoid shared-tag races between starts.
        var context=Path.Combine(buildRoot,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(context);
        try
        {
            using var stream=typeof(ModelImageUpdater).Assembly.GetManifestResourceStream("Xur.OmniXpu")!;
            using var reader=new StreamReader(stream);var file=Path.Combine(context,"Containerfile");
            await File.WriteAllTextAsync(file,await reader.ReadToEndAsync());var iid=Path.Combine(context,"image.id");
            var result=await run("podman",["build","--pull=always","--arch=amd64","--tag",reference,"--iidfile",iid,"--file",file,context],1800);
            return result.ExitCode==0?new(0,await File.ReadAllTextAsync(iid)):result;
        }
        finally{Directory.Delete(context,true);}
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
