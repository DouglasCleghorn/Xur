using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Xur.Agent;
using Xur.Control;
using Xur.Domain;

static class InstallerDiagnosticSshTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var blob=new byte[51];BinaryPrimitives.WriteUInt32BigEndian(blob,11);Encoding.ASCII.GetBytes("ssh-ed25519").CopyTo(blob,4);BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(15),32);
        System.Security.Cryptography.RandomNumberGenerator.Fill(blob.AsSpan(19));
        var publicKey="ssh-ed25519 "+Convert.ToBase64String(blob);
        var yaml="schemaVersion: 1\napiKey: "+new string('a',64)+"\nallowControl: true\n";
        var sshYaml=yaml+"sshAuthorizedKeys:\n  - "+publicKey+" fixture-comment\n";
        var config=DiagnosticsConfiguration.Parse(sshYaml);
        check(config.SshAuthorizedKeys.SequenceEqual([publicKey]),"Diagnostic SSH accepts Ed25519 public keys and drops their comments");
        foreach(var bad in new[]{sshYaml.Replace("allowControl: true","allowControl: false"),sshYaml.Replace(publicKey,"command=sh "+publicKey),sshYaml.Replace("ssh-ed25519","ssh-rsa"),sshYaml.Replace(Convert.ToBase64String(blob),"not-base64"),sshYaml+"  - "+publicKey, yaml+"sshAuthorizedKeys: true",yaml+"sshAuthorizedKeys: [42]",yaml+"sshAuthorizedKeys: [[nested]]",yaml+"sshAuthorizedKeys:\n  - \""+publicKey+"\\nextra\"",yaml+"sshAuthorizedKeys:\n"+string.Concat(Enumerable.Repeat("  - "+publicKey+"\n",9))})
        {
            bool rejected=false;try{DiagnosticsConfiguration.Parse(bad);}catch{rejected=true;}
            check(rejected,"Unsafe, invalid or ambiguous diagnostic SSH configuration is rejected");
        }
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../.build/evidence/diagnostic-ssh-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        try
        {
            var calls=new List<(string Exe,string[] Args)>();bool failValidation=false,failStart=false;
            Task<ProcessResult> Run(string exe,string[] args,int timeout)
            {
                calls.Add((exe,args));
                var failed=failValidation&&exe=="/usr/sbin/sshd" || failStart&&exe=="systemctl"&&args.SequenceEqual(["restart","sshd.service"]);
                return Task.FromResult(new ProcessResult(failed?1:exe=="systemctl"&&args.SequenceEqual(["is-active","firewalld"])?3:0,""));
            }
            var run=Path.Combine(root,"run");Directory.CreateDirectory(run);var system=Path.Combine(root,"system");
            var ssh=new InstallerDiagnosticSsh(run,system,Run,selinux:true);
            await ssh.Configure(DiagnosticsConfiguration.Parse(yaml));
            check(calls.Count==0&&!ssh.Enabled,"A diagnostic API without SSH keys makes no SSH or firewall changes");
            await ssh.Configure(config);
            var keys=Path.Combine(run,"diagnostic-authorized_keys");var unit=Path.Combine(system,"sshd.service.d","99-xur-diagnostics.conf");
            check(ssh.Enabled&&File.ReadAllText(keys)==publicKey+"\n"&&File.GetUnixFileMode(keys)==(UnixFileMode.UserRead|UnixFileMode.UserWrite),"Opt-in SSH keys are written only to a root-private runtime file");
            check(calls.Any(c=>c.Exe=="chcon"&&c.Args.SequenceEqual(["-t","ssh_home_t",keys])),"Runtime authorization receives the SSH SELinux label without disabling enforcement");
            var arguments=calls.Single(c=>c.Exe=="/usr/sbin/sshd").Args;
            check(arguments.Contains("AuthenticationMethods=publickey")&&arguments.Contains("PasswordAuthentication=no")&&arguments.Contains("KbdInteractiveAuthentication=no")&&arguments.Contains("AllowUsers=root")&&arguments.Contains("AuthorizedKeysFile="+keys)&&arguments.Contains("AuthorizedKeysCommand=none")&&arguments.Contains("TrustedUserCAKeys=none"),"Diagnostic SSH uses only configured keys for root and rejects passwords, helpers and certificate authorities");
            check(File.ReadAllText(unit).Contains("ExecStart=:/usr/sbin/sshd")&&calls.FindIndex(c=>c.Exe=="/usr/sbin/sshd")<calls.FindIndex(c=>c.Args.SequenceEqual(["restart","sshd.service"])),"The boot-only service override is activated only after sshd validates its settings");
            await ssh.Configure(null);
            check(!ssh.Enabled&&!File.Exists(keys)&&!File.Exists(unit)&&calls.Any(c=>c.Args.SequenceEqual(["stop","sshd.service"])),"Removing diagnostic configuration revokes runtime keys and stops the diagnostic SSH listener");
            calls.Clear();failValidation=true;bool failed=false;
            try{await ssh.Configure(config);}catch(IOException){failed=true;}
            check(failed&&!ssh.Enabled&&!File.Exists(keys)&&!File.Exists(unit)&&!calls.Any(c=>c.Args.SequenceEqual(["restart","sshd.service"])),"Failed sshd validation never starts access and removes staged authorization");
            calls.Clear();failValidation=false;failStart=true;failed=false;
            try{await ssh.Configure(config);}catch(IOException){failed=true;}
            check(failed&&!ssh.Enabled&&!File.Exists(keys)&&!File.Exists(unit),"An SSH service failure removes the runtime override and authorization");
            File.WriteAllText(Path.Combine(run,"diagnostics-status.json"),JsonSerializer.Serialize(new InstallerDiagnosticsStatus(ApiEnabled:true,AllowControl:true)));
            check(InstallerDiagnosticWarning.Read(run).Active,"A running diagnostic API enables the installer warning even without SSH");
            File.WriteAllText(Path.Combine(run,"diagnostics-status.json"),JsonSerializer.Serialize(new InstallerDiagnosticsStatus(SshEnabled:true)));
            check(InstallerDiagnosticWarning.Read(run).Active,"A diagnostic SSH listener enables the installer warning even if HTTPS fails");
            File.WriteAllText(Path.Combine(run,"diagnostics-status.json"),"{");
            check(!InstallerDiagnosticWarning.Read(run).Active,"An unreadable diagnostic status cannot break the console");
            File.Delete(Path.Combine(run,"diagnostics-status.json"));
            check(!InstallerDiagnosticWarning.Read(run).Active,"Normal installer boots do not display an active diagnostic warning");
            foreach(var size in new[]{(40,12),(80,25),(160,60)})foreach(var page in new[]{0,1})
            {
                var frame=LocalConsole.Frame("Installation logs",string.Join('\n',Enumerable.Range(0,100).Select(i=>"Log line "+i)),size.Item1,size.Item2,page,logWindow:true,diagnosticsActive:true);
                check(LocalConsole.Clean(frame).Contains(InstallerDiagnosticWarning.Banner),"The active diagnostics warning stays pinned at all terminal sizes and log pages");
            }
            check(!LocalConsole.Frame("Setup","Ready",80,25).Contains(InstallerDiagnosticWarning.Banner),"Console frames omit the diagnostic warning when diagnostics are inactive");
        }
        finally{Directory.Delete(root,true);}
    }
}
