using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

public sealed class OsUpdates
{
    const string Program = "/var/lib/xur/app/current/host/os-update";
    readonly Func<string,IEnumerable<string>,int,Task<ProcessResult>> run;
    public OsUpdates(Func<string,IEnumerable<string>,int,Task<ProcessResult>>? run=null)
        =>this.run=run ?? ((exe,args,timeout)=>Processes.Run(exe,args,timeout));
    public static bool Allowed(string action)=>action is "check" or "stage" or "rollback" or "enable" or "disable";
    public async Task<OsUpdateStatus> Status()
    {
        var result=await run(Program,["status"],35);
        if(result.ExitCode!=0)throw new InvalidOperationException("OS update status is unavailable.");
        var status=JsonSerializer.Deserialize<OsUpdateStatus>(result.Output,new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Invalid OS update status.");
        var unit=await run("systemctl",["is-active","xur-os-manual.service"],10);
        return status with { Busy=status.Busy || unit.Output.Trim() is "active" or "activating" };
    }
    public async Task Start(string action)
    {
        if(await UpdateAll.Running())throw new InvalidOperationException("Wait for Update All to finish.");
        if(!Allowed(action))throw new InvalidOperationException("Unknown OS update action.");
        var status=await Status();
        if(status.Busy)throw new InvalidOperationException("An OS update operation is already running.");
        if(action is "stage" or "rollback" && (status.Pending!=null || status.RollbackQueued))
            throw new InvalidOperationException("An OS deployment is already queued. Reboot to finish.");
        if(action=="rollback" && status.Previous==null)throw new InvalidOperationException("No previous deployment is available.");
        var result=await run("systemd-run",["--unit=xur-os-manual","--collect","--no-block","--property=Type=oneshot",
            "--property=TimeoutStartSec=2h",Program,action],15);
        if(result.ExitCode!=0)throw new InvalidOperationException("Could not start the OS update operation.");
    }
}
