using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

public sealed class EngineRestartPolicy(Func<string,string[],int,Task<ProcessResult>>? execute=null)
{
    readonly Func<string,string[],int,Task<ProcessResult>> run=execute??((exe,args,seconds)=>Processes.Run(exe,args,seconds));
    public async Task Set(string name,string instanceId,string policy)
    {
        if(policy is not ("no" or "unless-stopped"))throw new ArgumentException("Invalid restart policy.");
        async Task<bool> Matches()
        {
            var result=await run("podman",["container","inspect",name],10);
            if(result.ExitCode!=0)return false;
            using var doc=JsonDocument.Parse(result.Output);
            var container=doc.RootElement[0];
            if(container.GetProperty("Id").GetString()!=instanceId)throw new InvalidOperationException("The engine instance changed while updating its restart policy.");
            return container.GetProperty("HostConfig").GetProperty("RestartPolicy").GetProperty("Name").GetString()==policy;
        }
        if(await Matches())return;
        var update=await run("podman",["update","--restart="+policy,name],10);
        // Podman persists restart metadata before asking crun to reapply resource
        // limits. Some versions send the unchanged CDI device rules too, which
        // crun cannot replace when a BPFProgram owns device filtering. Accept
        // only this known error, and only after reading back the intended policy
        // on the exact same container. Never relax its GPU/device boundary.
        if(update.ExitCode!=0 && !(update.Output.Contains("updating device access list not supported",StringComparison.OrdinalIgnoreCase) && update.Output.Contains("BPFProgram",StringComparison.Ordinal)))
            throw new InvalidOperationException("Could not set engine restart policy: "+Redaction.Logs(update.Output));
        if(!await Matches())throw new InvalidOperationException("The engine restart policy was not saved. Open workload logs before retrying.");
    }
}
