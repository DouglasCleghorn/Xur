using Xur.Domain;
namespace Xur.Agent;

// A failed child with PartOf= can keep a stopped desktop unit loaded. Retire
// children first and wait for systemd to discard their transient fragments.
public sealed class StationUnits(Func<string,string[],int,Task<ProcessResult>>? runner=null)
{
    Task<ProcessResult> Run(string[] args)=>runner!=null?runner("systemctl",args,10):Processes.Run("systemctl",args,10);
    public async Task Retire(string unit,bool stopActive=false)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(unit,@"^xur-(station|stream|virtual-output|unit-probe)-[a-zA-Z0-9-]+\.service$"))
            throw new InvalidOperationException("Invalid workstation service name.");
        async Task<Dictionary<string,string>> Observe()
        {
            var result=await Run(["show",unit,"--property=LoadState,ActiveState,Transient,FragmentPath"]);
            var fields=result.Output.Split('\n').Where(l=>l.Contains('=')).Select(l=>l.Split('=',2)).ToDictionary(p=>p[0],p=>p[1]);
            if(!fields.ContainsKey("LoadState"))throw new InvalidOperationException("Could not inspect workstation service "+unit+". "+Redaction.Logs(result.Output));
            return fields;
        }
        var state=await Observe();
        if(state["LoadState"]=="not-found")return;
        if(state.GetValueOrDefault("Transient")!="yes")throw new InvalidOperationException("Workstation service "+unit+" has a non-transient unit file. It was preserved; inspect its systemd configuration.");
        if(!stopActive&&state.GetValueOrDefault("ActiveState") is not ("inactive" or "failed"))
            throw new InvalidOperationException("Workstation service "+unit+" is still running or changing state. Wait for it to finish, or unload the profile.");
        var stopped=await Run(["stop",unit]);
        if(stopped.ExitCode!=0&&(await Observe())["LoadState"]!="not-found")
            throw new InvalidOperationException("Could not stop workstation service "+unit+". "+Redaction.Logs(stopped.Output));
        // Legacy units were created without --collect and can retain a failed
        // state after stop. reset-failed also releases their restart counter.
        await Run(["reset-failed",unit]);
        for(var attempt=0;attempt<50;attempt++)
        {
            if((await Observe())["LoadState"]=="not-found")return;
            await Task.Delay(100);
        }
        throw new InvalidOperationException("Workstation service "+unit+" is stopped but still referenced by another systemd unit. Unload the profile and retry; its unit file was preserved.");
    }
}
