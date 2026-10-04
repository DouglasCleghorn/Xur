using System.Text.Json;
using Xur.Domain;
namespace Xur.Control;

public static class InstallerDiagnosticWarning
{
    public const string Banner="WARNING: Diagnostics active";
    public static InstallerDiagnosticsStatus Read(string run)
    {
        try{return JsonSerializer.Deserialize<InstallerDiagnosticsStatus>(File.ReadAllText(Path.Combine(run,"diagnostics-status.json")))??new();}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException){return new();}
    }
}
