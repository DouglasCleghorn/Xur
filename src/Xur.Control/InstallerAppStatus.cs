using System.Text.Json;
namespace Xur.Control;

public static class InstallerAppStatus
{
    public static string Message(string runDirectory)
    {
        string? state=null;
        try
        {
            using var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(runDirectory,"app/check.json")));
            if(json.RootElement.TryGetProperty("state",out var value)&&value.ValueKind==JsonValueKind.String)state=value.GetString();
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException){}
        return state switch
        {
            "checking"=>"Checking for Xur updates in the background. Setup remains available.",
            "current"=>"Xur update check complete. This app is current for the selected channel.",
            "available"=>"A newer Xur app is available. Use Update All after installation, or boot a newer installer.",
            "unavailable"=>"Update check unavailable. Setup remains available; retrying in the background.",
            "disabled"=>"Using bundled Xur. Online update checks are disabled for this boot.",
            "fallback"=>"Using bundled Xur after an app health-check failure. Setup remains available.",
            _=>"Using bundled Xur. Online update checks do not delay setup."
        };
    }
}
