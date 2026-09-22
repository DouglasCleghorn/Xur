using Xur.Domain;
namespace Xur.Control;

public sealed class DiagnosticLogs(ApplicationLog application, HttpClient agent)
{
    public async Task<string> Read(bool console = false)
    {
        string services;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            services = await agent.GetStringAsync(console ? "/console-logs" : "/logs", timeout.Token);
        }
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException)
        { services = "Service journal unavailable: " + Redaction.Logs(e.Message); }
        return $"Xur application diagnostics\nBundle: {ApplicationIdentity.Id}\n\n" +
            "=== Web manager (current process) ===\n" + application.Read() +
            "\n\n=== Xur services and installer ===\n" + Redaction.Logs(services);
    }
}
