using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;
public sealed class UpdateAll
{
    const string Program="/var/lib/xur/app/current/host/xurutil";
    public static async Task<bool> Running()=> (await Processes.Run("systemctl",["is-active","xur-update-all.service"],5)).Output.Trim() is "active" or "activating";
    public async Task<UpdateAllStatus> Status()
    {
        var result=await Processes.Run(Program,["update-all","status"],10);
        if(result.ExitCode!=0)throw new InvalidOperationException("Update All status is unavailable.");
        var status=JsonSerializer.Deserialize<UpdateAllStatus>(result.Output,new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        return status with{Busy=status.Busy||await Running()};
    }
    public async Task Start()
    {
        if(await Running())throw new InvalidOperationException("Update All is already running.");
        foreach(var unit in new[]{"xur-app-update.service","xur-os-manual.service"})
            if((await Processes.Run("systemctl",["is-active",unit],5)).Output.Trim() is "active" or "activating")throw new InvalidOperationException("Wait for the current update to finish.");
        var executable=Path.Combine(Directory.ResolveLinkTarget("/var/lib/xur/app/current",true)!.FullName,"host/xurutil");
        var result=await Processes.Run("systemd-run",["--unit=xur-update-all","--collect","--no-block","--property=Type=oneshot","--property=TimeoutStartSec=4h",executable,"update-all","run"],15);
        if(result.ExitCode!=0)throw new InvalidOperationException("Could not start Update All.");
    }
}
