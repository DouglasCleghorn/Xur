namespace Xur.Domain;

public record InstallerDiagnosticsStatus(bool ApiEnabled=false,bool AllowControl=false,bool SshEnabled=false,string SshError="")
{
    public bool Active=>ApiEnabled||SshEnabled;
}
