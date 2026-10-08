using System.Text;
using Xur.Domain;
namespace Xur.Agent;

// Shared by installer opt-in and installed diagnostic sessions. All authorization is runtime-only.
public sealed class DiagnosticSsh(string runDirectory,string systemDirectory="/run/systemd/system",
    Func<string,string[],int,Task<ProcessResult>>? runner=null,bool? selinux=null)
{
    string Keys=>Path.Combine(runDirectory,"diagnostic-authorized_keys");
    string Override=>Path.Combine(systemDirectory,"sshd.service.d","99-xur-diagnostics.conf");
    string FirewallMarker=>Path.Combine(runDirectory,"diagnostic-ssh-firewall");
    public bool Enabled {get;private set;}
    public async Task<bool> ObserveEnabled()
    {
        if(!File.Exists(Override))return Enabled=false;
        return Enabled=(await Run("systemctl",["is-active","sshd.service"],5)).ExitCode==0;
    }
    Task<ProcessResult> Run(string exe,string[] args,int timeout=10)=>runner!=null?runner(exe,args,timeout):Processes.Run(exe,args,timeout);
    async Task Require(string exe,string[] args,int timeout=10)
    {
        var result=await Run(exe,args,timeout);
        if(result.ExitCode!=0)
        {
            var detail=Redaction.Logs(result.Output).Trim();if(detail.Length>2048)detail=detail[..2048]+"…";
            throw new IOException("Diagnostic SSH setup failed: "+Path.GetFileName(exe)+(detail.Length==0?"":". "+detail));
        }
    }
    static void Write(string path,string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp=path+".tmp";
        using(var stream=new FileStream(temp,new FileStreamOptions{Mode=FileMode.Create,Access=FileAccess.Write,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite}))
        using(var writer=new StreamWriter(stream,Encoding.ASCII))writer.Write(text);
        File.SetUnixFileMode(temp,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        File.Move(temp,path,true);
    }
    static string Quote(string value)=>"\""+value.Replace("\\","\\\\").Replace("\"","\\\"").Replace("%","%%")+"\"";
    public async Task Configure(DiagnosticsConfiguration? config)
    {
        if(config?.SshAuthorizedKeys.Length is not >0){await Stop();return;}
        if(!config.AllowControl)throw new InvalidOperationException("Diagnostic SSH requires control permission.");
        await ConfigureKeys(config.SshAuthorizedKeys);
    }
    public async Task ConfigureKeys(string[] keys,bool protectExisting=false)
    {
        if(keys.Length is <1 or >8)throw new InvalidOperationException("Use one to eight Ed25519 public keys.");
        keys=keys.Select(DiagnosticsConfiguration.PublicKey).ToArray();
        if(keys.Distinct(StringComparer.Ordinal).Count()!=keys.Length)throw new InvalidOperationException("Duplicate SSH public keys.");
        if(protectExisting && !File.Exists(Override))
        {
            if((await Run("systemctl",["is-active","sshd.service"],5)).ExitCode==0 ||
                (await Run("systemctl",["is-active","sshd.socket"],5)).ExitCode==0)
                throw new InvalidOperationException("An existing SSH service is active. Diagnostic SSH will not replace it.");
        }
        // Use an isolated config: per-connection Match rules can override command-line options.
        // Default host keys remain in use; ordinary key files, helpers and CAs cannot authorize access.
        string[] arguments=["-D","-e","-f","/dev/null","-o","PermitRootLogin=prohibit-password","-o","PasswordAuthentication=no",
            "-o","KbdInteractiveAuthentication=no","-o","AuthenticationMethods=publickey","-o","PubkeyAuthentication=yes",
            "-o","PubkeyAcceptedAlgorithms=ssh-ed25519","-o","AuthorizedKeysFile="+Keys,"-o","AuthorizedKeysCommand=none",
            "-o","TrustedUserCAKeys=none","-o","AllowUsers=root","-o","UsePAM=yes","-o","DisableForwarding=yes",
            "-o","Port=22"];
        try
        {
            Write(Keys,string.Join('\n',keys)+"\n");
            if(selinux??Directory.Exists("/sys/fs/selinux"))await Require("chcon",["-t","ssh_home_t",Keys]);
            await Require("systemctl",["start","sshd-keygen.target"],30);
            await Require("/usr/sbin/sshd",["-t",..arguments.Skip(2)]);
            Write(Override,"[Service]\nKillMode=control-group\nRestart=no\nExecStart=\nExecStart=:/usr/sbin/sshd "+string.Join(' ',arguments.Select(Quote))+"\n");
            await Require("systemctl",["daemon-reload"]);
            await Require("systemctl",["restart","sshd.service"],30);
            await Require("systemctl",["is-active","sshd.service"]);
            if((await Run("systemctl",["is-active","firewalld"],5)).ExitCode==0 && (await Run("firewall-cmd",["--query-port=22/tcp"])).ExitCode!=0)
            {
                Write(FirewallMarker,"22/tcp\n");
                await Require("firewall-cmd",["--add-port=22/tcp"]);
            }
            Enabled=true;
        }
        catch
        {
            await Stop();throw;
        }
    }
    public async Task Stop()
    {
        // Remove authorization first, including after an interrupted previous agent.
        File.Delete(Keys);
        if(File.Exists(Override))
        {
            Enabled=true;
            await Require("systemctl",["stop","sshd.service"],30);
            Enabled=false;
            File.Delete(Override);
            await Require("systemctl",["daemon-reload"]);
        }
        else Enabled=false;
        if(File.Exists(FirewallMarker)){await Require("firewall-cmd",["--remove-port=22/tcp"]);File.Delete(FirewallMarker);}
    }
}
