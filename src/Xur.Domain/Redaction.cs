using System.Text.RegularExpressions;
namespace Xur.Domain;
public static partial class Redaction
{
    public static string Logs(string value)
    {
        var lines=value.Split('\n').Where(l=>!l.Any(c=>"▀▄█".Contains(c)));
        return string.Join('\n',lines.Select(line=> {
            line=Code().Replace(line,"Access code: [REDACTED]");
            line=Claim().Replace(line,"[REDACTED CLAIM]");
            line=Credential().Replace(line,"$1=[REDACTED]");
            line=HubToken().Replace(line,"[REDACTED HF TOKEN]");
            line=Bearer().Replace(line,"Bearer [REDACTED]");
            line=ApiKey().Replace(line,"[REDACTED API KEY]");
            return SessionToken().Replace(line,"[REDACTED SESSION]");
        }));
    }
    [GeneratedRegex(@"\bhf_[A-Za-z0-9]{8,}\b")]
    private static partial Regex HubToken();
    [GeneratedRegex(@"(?i)(?:One-time|Access) code:\s*\S+")]
    private static partial Regex Code();
    [GeneratedRegex(@"https://(?:login|controlplane)\.tailscale\.com/\S+")]
    private static partial Regex Claim();
    [GeneratedRegex("""(?i)\b(token|auth[-_]?key|api[-_]?key|password|secret)\s*[=:]\s*(?:"[^"\r\n]*"|'[^'\r\n]*'|\S+)""")]
    private static partial Regex Credential();
    [GeneratedRegex(@"(?i)\bBearer\s+[^\s,;]+")]
    private static partial Regex Bearer();
    [GeneratedRegex(@"\bxur_[a-f0-9]{32}_[a-f0-9]{64}\b")]
    private static partial Regex ApiKey();
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b")]
    private static partial Regex SessionToken();
}
