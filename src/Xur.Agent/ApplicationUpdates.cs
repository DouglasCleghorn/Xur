using Xur.Domain;
namespace Xur.Agent;
public sealed class ApplicationUpdates
{
    const string Program="/var/lib/xur/app/current/host/app-update";
    readonly SemaphoreSlim gate=new(1,1);
    public async Task<string> Status()
    {
        var result=await Processes.Run(Program,["status"],15);
        if(result.ExitCode!=0)throw new InvalidOperationException("Application update status is unavailable.");
        return result.Output;
    }
    public async Task Act(ApplicationUpdateRequest request)
    {
        await gate.WaitAsync();try
        {
            if(await UpdateAll.Running())throw new InvalidOperationException("Wait for Update All to finish.");
            if(request.Action is not ("configure" or "development" or "channel" or "check" or "update" or "rollback"))throw new InvalidOperationException("Unknown application update action.");
            var state=await Processes.Run("systemctl",["is-active","--quiet","xur-app-update.service"],5);
            if(state.ExitCode==0)throw new InvalidOperationException("An application update is already running.");
            if(request.Action=="channel") {
                if(request.Channel is not ("nightly" or "stable" or "local"))throw new InvalidOperationException("Choose nightly, stable or local build testing.");
                if(request.Channel=="local" && (string.IsNullOrWhiteSpace(request.Server) || request.Server.Length>2048 || string.IsNullOrWhiteSpace(request.PublicKey) || request.PublicKey.Length>4096 || request.PublicKey.Contains("PRIVATE KEY",StringComparison.Ordinal)))
                    throw new InvalidOperationException("Enter a local server and its Ed25519 public key in PEM format. Never enter a private key.");
                var saved=await Processes.Run(Program,request.Channel=="local"?["channel","local",request.Server!,request.PublicKey!]:["channel",request.Channel],10);
                if(saved.ExitCode!=0)throw new InvalidOperationException("Could not save the update channel. For local testing, enter a valid server address and an Ed25519 public key in PEM format.");return;
            }
            if(request.Action=="development") {
                var saved=await Processes.Run(Program,["development",request.Development?"true":"false"],10);
                if(saved.ExitCode!=0)throw new InvalidOperationException("Could not save local build testing setting.");
                return;
            }
            if(request.Action=="configure")
            {
                if(string.IsNullOrWhiteSpace(request.Server) || request.Server.Length>2048)throw new InvalidOperationException("Enter the update server IP or domain.");
                var configured=await Processes.Run(Program,["configure",request.Server],10);
                if(configured.ExitCode!=0)throw new InvalidOperationException("Enable local build testing in Settings, then enter a valid server address.");
                return;
            }
            var osUpdate=await Processes.Run("systemctl",["is-active","--quiet","xur-os-manual.service"],5);
            if(osUpdate.ExitCode==0)throw new InvalidOperationException("Wait for the OS update to finish.");
            var result=await Processes.Run("systemd-run",["--unit=xur-app-update","--collect","--no-block","--property=Type=oneshot","--property=TimeoutStartSec=1h","/usr/bin/python3",Path.GetFullPath(Path.Combine(Directory.ResolveLinkTarget("/var/lib/xur/app/current",true)!.FullName,"host/app-update")),request.Action],15);
            if(result.ExitCode!=0)throw new InvalidOperationException("Could not start the application update.");
        }finally{gate.Release();}
    }
}
