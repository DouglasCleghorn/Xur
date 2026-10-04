using System.Text;
using Xur.Domain;
namespace Xur.Agent;

// Only called on live installer boots. Runtime files never enter install-manager's copy list.
public sealed class InstallerDiagnosticSsh(string runDirectory,string systemDirectory="/run/systemd/system",
    Func<string,string[],int,Task<ProcessResult>>? runner=null,bool? selinux=null)
{
    string Keys=>Path.Combine(runDirectory,"diagnostic-authorized_keys");
    string Override=>Path.Combine(systemDirectory,"sshd.service.d","99-xur-diagnostics.conf");
    bool firewallAdded;
    public bool Enabled {get;private set;}
    Task<ProcessResult> Run(string exe,string[] args,int timeout=10)=>runner!=null?runner(exe,args,timeout):Processes.Run(exe,args,timeout);
    async Task Require(string exe,string[] args,int timeout=10)
    {
        if((await Run(exe,args,timeout)).ExitCode!=0)throw new IOException("Diagnostic SSH setup failed: "+Path.GetFileName(exe));
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
        // Command-line settings take precedence over the distribution's included configuration.
        // No ordinary authorized_keys file, helper or trusted certificate authority grants access.
        string[] arguments=["-D","-e","-o","PermitRootLogin=prohibit-password","-o","PasswordAuthentication=no",
            "-o","KbdInteractiveAuthentication=no","-o","AuthenticationMethods=publickey","-o","PubkeyAuthentication=yes",
            "-o","PubkeyAcceptedAlgorithms=ssh-ed25519","-o","AuthorizedKeysFile="+Keys,"-o","AuthorizedKeysCommand=none",
            "-o","TrustedUserCAKeys=none","-o","AllowUsers=root","-o","UsePAM=yes","-o","DisableForwarding=yes"];
        try
        {
            Write(Keys,string.Join('\n',config.SshAuthorizedKeys)+"\n");
            if(selinux??Directory.Exists("/sys/fs/selinux"))await Require("chcon",["-t","ssh_home_t",Keys]);
            await Require("systemctl",["start","sshd-keygen.target"],30);
            await Require("/usr/sbin/sshd",["-t",..arguments.Skip(2)]);
            Write(Override,"[Service]\nExecStart=\nExecStart=:/usr/sbin/sshd "+string.Join(' ',arguments.Select(Quote))+"\n");
            await Require("systemctl",["daemon-reload"]);
            await Require("systemctl",["restart","sshd.service"],30);
            await Require("systemctl",["is-active","sshd.service"]);
            if((await Run("systemctl",["is-active","firewalld"],5)).ExitCode==0 && (await Run("firewall-cmd",["--query-port=22/tcp"])).ExitCode!=0)
            {
                await Require("firewall-cmd",["--add-port=22/tcp"]);firewallAdded=true;
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
        Enabled=false;
        // Remove authorization first, including after an interrupted previous agent.
        File.Delete(Keys);
        if(File.Exists(Override))
        {
            await Require("systemctl",["stop","sshd.service"],30);
            File.Delete(Override);
            await Require("systemctl",["daemon-reload"]);
        }
        if(firewallAdded){await Require("firewall-cmd",["--remove-port=22/tcp"]);firewallAdded=false;}
    }
}
