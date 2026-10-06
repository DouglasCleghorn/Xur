using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

public sealed class OsUpdates
{
    const string Program = "/var/lib/xur/app/current/host/os-update";
    readonly Func<string,IEnumerable<string>,int,Task<ProcessResult>> run;
    public OsUpdates(Func<string,IEnumerable<string>,int,Task<ProcessResult>>? run=null)
        =>this.run=run ?? ((exe,args,timeout)=>Processes.Run(exe,args,timeout));
    public static bool Allowed(string action)=>action is "check" or "stage" or "rollback" or "enable" or "disable" or "schedule" or "skip";
    public async Task Initialize()
    {
        if(!File.Exists(Program))return;
        var result=await run(Program,["initialize"],40);
        if(result.ExitCode!=0)throw new InvalidOperationException("Could not configure the automatic update timer. "+Redaction.Logs(result.Output));
    }
    public async Task<bool> PowerBlocked()
    {
        if(!File.Exists(Program))return false;
        var result=await run(Program,["power-status"],5);
        if(result.ExitCode!=0)throw new InvalidOperationException("Could not check for an active OS deployment. Review the system logs and retry.");
        using var status=JsonDocument.Parse(result.Output);
        if(status.RootElement.ValueKind!=JsonValueKind.Object || !status.RootElement.TryGetProperty("blocked",out var blocked) || blocked.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException("Invalid OS deployment guard status.");
        return blocked.GetBoolean();
    }
    public async Task<OsUpdateStatus> Status()
    {
        var result=await run(Program,["status"],35);
        if(result.ExitCode!=0)throw new InvalidOperationException("OS update status is unavailable.");
        var status=JsonSerializer.Deserialize<OsUpdateStatus>(result.Output,new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Invalid OS update status.");
        var unit=await run("systemctl",["is-active","xur-os-manual.service"],10);
        return status with { Busy=status.Busy || unit.Output.Trim() is "active" or "activating" };
    }
    public Task Start(string action)=>Start(new OsUpdateAction(action));
    public async Task Start(OsUpdateAction request)
    {
        var action=request.Action;
        if(!Allowed(action))throw new InvalidOperationException("Unknown OS update action.");
        if(action is "schedule" or "skip" or "enable" or "disable")
        {
            if(action=="schedule" && (request.Schedule is not {} schedule ||
                !System.Text.RegularExpressions.Regex.IsMatch(schedule.Time??"",@"\A(?:[01][0-9]|2[0-3]):[0-5][0-9]\z") ||
                schedule.Days is not {Length:>0} || schedule.Days.Any(d=>d is <0 or >6) || schedule.WarningMinutes is <5 or >120))
                throw new InvalidOperationException("Choose a valid time, at least one day, and 5–120 minutes of advance notice.");
            if(action=="skip" && (string.IsNullOrEmpty(request.WindowId) || request.WindowId.Length>20 || !request.WindowId.All(char.IsAsciiDigit)))
                throw new InvalidOperationException("Refresh status and select the current update window.");
            var changed=await run(Program,[action,JsonSerializer.Serialize(request,new JsonSerializerOptions(JsonSerializerDefaults.Web))],40);
            if(changed.ExitCode!=0)
            {
                string error="Could not change automatic update settings. Refresh status and retry.";
                try {using var json=JsonDocument.Parse(changed.Output);if(json.RootElement.TryGetProperty("error",out var message))error=message.GetString()??error;}catch(JsonException){}
                throw new InvalidOperationException(error);
            }
            return;
        }
        if(await UpdateAll.Running())throw new InvalidOperationException("Wait for Update All to finish.");
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
