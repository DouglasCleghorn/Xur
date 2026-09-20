using System.Text.RegularExpressions;
namespace Xur.Domain;
public static partial class Redaction
{
    public static string Logs(string value)
    {
        var lines=value.Split('\n').Where(l=>!l.Any(c=>"▀▄█".Contains(c)));
        return string.Join('\n',lines.Select(l=>HubToken().Replace(Credential().Replace(Claim().Replace(Code().Replace(l,"Access code: [REDACTED]"),"[REDACTED CLAIM]"),"$1=[REDACTED]"),"[REDACTED HF TOKEN]")));
    }
    [GeneratedRegex(@"\bhf_[A-Za-z0-9]{8,}\b")]
    private static partial Regex HubToken();
    [GeneratedRegex(@"(?i)(?:One-time|Access) code:\s*\S+")]
    private static partial Regex Code();
    [GeneratedRegex(@"https://(?:login|controlplane)\.tailscale\.com/\S+")]
    private static partial Regex Claim();
    [GeneratedRegex(@"(?i)\b(token|auth[-_]?key|password|secret)\s*[=:]\s*\S+")]
    private static partial Regex Credential();
}
