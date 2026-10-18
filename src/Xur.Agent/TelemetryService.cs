using Xur.Domain;
namespace Xur.Agent;

public static class TelemetryService
{
    // Install on agent startup so upgrades performed by older updaters also gain the service.
    // The private socket has its own root-only directory; no TCP listener or public VM API.
    public static async Task Ensure()
    {
        const string unit="/etc/systemd/system/xur-metrics.service";
        var binary=Path.Combine(AppContext.BaseDirectory,"victoriametrics","victoria-metrics-prod");
        if(!File.Exists(binary))throw new IOException("The application bundle is missing VictoriaMetrics.");
        Directory.CreateDirectory("/var/lib/xur/metrics");File.SetUnixFileMode("/var/lib/xur/metrics",(UnixFileMode)448);
        using var resource=typeof(TelemetryService).Assembly.GetManifestResourceStream("Xur.MetricsService")!;
        using var reader=new StreamReader(resource);
        var content="# Xur-managed telemetry service; bundle "+ApplicationIdentity.Id+"\n"+await reader.ReadToEndAsync();
        // Updating the bundle restarts storage against the new pinned binary. Unchanged starts are cheap.
        var changed=!File.Exists(unit)||File.ReadAllText(unit)!=content;
        if(changed)
        {
            File.WriteAllText(unit+".tmp",content);File.Move(unit+".tmp",unit,true);
            var reload=await Processes.Run("systemctl",["daemon-reload"],30);if(reload.ExitCode!=0)throw new IOException("Could not reload telemetry service.");
        }
        var start=await Processes.Run("systemctl",[changed?"restart":"start","xur-metrics.service"],150);
        if(start.ExitCode!=0)throw new IOException("Could not start telemetry storage.");
    }
}
