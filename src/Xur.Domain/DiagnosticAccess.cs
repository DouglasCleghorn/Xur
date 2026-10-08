namespace Xur.Domain;

public record DiagnosticSshStatus(bool Enabled=false,int KeyCount=0,string? Error=null);
public record DiagnosticSshRequest(bool Enabled,string PublicKeys="");
public record DiagnosticProbe(string Command,string[] Arguments,int ExitCode,string Output,bool Truncated=false);
