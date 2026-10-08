using System.Buffers.Binary;
using System.Text;
using Xur.Agent;
using Xur.Domain;

static class DiagnosticAccessTests
{
    public static async Task Run(Action<bool,string> check,bool validateOpenSsh=false)
    {
        var root=Path.GetFullPath(Path.Combine(".build/evidence","diagnostic-access-"+Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var blob=new byte[51];BinaryPrimitives.WriteUInt32BigEndian(blob,11);Encoding.ASCII.GetBytes("ssh-ed25519").CopyTo(blob,4);BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(15),32);
            System.Security.Cryptography.RandomNumberGenerator.Fill(blob.AsSpan(19));
            var key="ssh-ed25519 "+Convert.ToBase64String(blob);
            var calls=new List<(string Exe,string[] Args)>();bool active=false,socket=false,port=false,failStart=false,failStop=false;
            Task<ProcessResult> Run(string exe,string[] args,int seconds)
            {
                calls.Add((exe,args));var exit=0;
                if(exe=="systemctl" && args is ["is-active","sshd.service"])exit=active?0:3;
                if(exe=="systemctl" && args is ["is-active","sshd.socket"])exit=socket?0:3;
                if(args is ["restart","sshd.service"]){exit=failStart?1:0;active=!failStart;}
                if(args is ["stop","sshd.service"]){exit=failStop?1:0;if(!failStop)active=false;}
                if(args is ["--query-port=22/tcp"])exit=port?0:1;
                if(args is ["--add-port=22/tcp"])port=true;
                if(args is ["--remove-port=22/tcp"])port=false;
                return Task.FromResult(new ProcessResult(exit,failStart && args is ["restart","sshd.service"]?"password=fixture-secret\nPort 22 is unavailable.":""));
            }
            var runtime=root+"/run";Directory.CreateDirectory(runtime);var system=root+"/system";
            var core=new DiagnosticSsh(runtime,system,Run,selinux:true);var access=new InstalledDiagnosticSsh(core);
            await access.Initialize();
            check(!(await access.Read()).Enabled && calls.Count==0,"Basic installs leave SSH disabled without starting services or opening firewall ports");
            foreach(var invalid in new[]{null,"",key+"\n"+key,"command=sh "+key,"-----BEGIN OPENSSH PRIVATE KEY-----",key.Replace("ssh-ed25519","ssh-rsa"),new string('x',8193)})
            {
                bool rejected=false;try{await access.Set(new(true,invalid!));}catch(InvalidOperationException){rejected=true;}
                check(rejected && calls.Count==0,"Installed SSH rejects invalid authorization before changing services");
            }
            active=true;bool refused=false;
            try{await access.Set(new(true,key));}catch(InvalidOperationException){refused=true;}
            check(refused && active && !File.Exists(runtime+"/diagnostic-authorized_keys") && !calls.Any(c=>c.Args.Contains("restart")),"Existing SSH access is preserved instead of being replaced by diagnostic settings");
            active=false;socket=true;refused=false;
            try{await access.Set(new(true,key));}catch(InvalidOperationException){refused=true;}
            check(refused && socket && !calls.Any(c=>c.Args.Contains("restart")),"An active socket-activated SSH listener is also preserved");
            socket=false;calls.Clear();
            var status=await access.Set(new(true,key+" test key\n"));
            var keys=runtime+"/diagnostic-authorized_keys";var unit=system+"/sshd.service.d/99-xur-diagnostics.conf";
            check(status.Enabled && status.KeyCount==1 && active && port && File.ReadAllText(keys)==key+"\n","Explicit Settings opt-in enables only the submitted normalized public key");
            check(File.GetUnixFileMode(keys)==(UnixFileMode.UserRead|UnixFileMode.UserWrite) && File.ReadAllText(unit).Contains("KillMode=control-group\nRestart=no\n"),"Runtime keys stay private and disabling terminates diagnostic SSH sessions");
            var arguments=calls.Single(c=>c.Exe=="/usr/sbin/sshd").Args;
            check(arguments.Contains("/dev/null") && arguments.Contains("Port=22") && arguments.Contains("PasswordAuthentication=no") && arguments.Contains("AuthenticationMethods=publickey") && arguments.Contains("AuthorizedKeysCommand=none") && arguments.Contains("TrustedUserCAKeys=none") && arguments.Contains("DisableForwarding=yes"),"Installed diagnostic SSH uses an isolated config on port 22 with key-only login and no forwarding or alternate authorization sources");
            // The focused Linux suite validates OpenSSH itself; the general unit suite needs no sshd installation.
            if(validateOpenSsh)
            {
            var hostKey=root+"/host_ed25519";
            var generated=await Processes.Run("ssh-keygen",["-q","-t","ed25519","-N","","-f",hostKey],10);
            check(generated.ExitCode==0,"A disposable host key is available for OpenSSH configuration validation");
            var ordinary=root+"/ordinary-sshd_config";
            File.WriteAllText(ordinary,"Match User root\n PasswordAuthentication yes\n AuthenticationMethods any\n AuthorizedKeysFile /tmp/alternate\n");
            var effective=await Processes.Run("/usr/sbin/sshd",["-T","-f",ordinary,"-h",hostKey,"-C","user=root,host=test,addr=127.0.0.1",..arguments.Skip(1)],10);
            check(effective.ExitCode==0 && effective.Output.Contains("passwordauthentication no\n") && effective.Output.Contains("authenticationmethods publickey\n") && effective.Output.Contains("allowusers root\n") && effective.Output.Contains("disableforwarding yes\n") && effective.Output.Contains("authorizedkeysfile "+keys+"\n") && !effective.Output.Contains("/tmp/alternate"),"Real OpenSSH excludes ordinary Match rules and validates diagnostic restrictions without a listener");
            }
            active=false;
            check(!(await access.Read()).Enabled,"Settings report an externally stopped SSH service as disabled");
            active=true;failStop=true;bool failed=false;
            try{await access.Set(new(false));}catch(IOException){failed=true;}
            check(failed && !File.Exists(keys) && (await access.Read()) is {Enabled:true,Error:not null},"Failed shutdown revokes new logins and reports that the listener still needs stopping");
            failStop=false;await access.Set(new(false));
            check(!active && !port && !File.Exists(keys) && !File.Exists(unit),"Disabling removes authorization, the override and the firewall opening");
            await access.Set(new(true,key));
            var restarted=new InstalledDiagnosticSsh(new DiagnosticSsh(runtime,system,Run,selinux:true));
            await restarted.Initialize();
            check(!(await restarted.Read()).Enabled && !active && !port && !File.Exists(keys),"Agent restart revokes a previous session and cleans up its tracked firewall port");
            failStart=true;failed=false;string failure="";
            try{await restarted.Set(new(true,key));}catch(IOException e){failed=true;failure=e.Message;}
            check(failed && !active && !port && !File.Exists(keys) && !File.Exists(unit),"Failed SSH startup rolls back staged access");
            check(failure.Contains("Port 22 is unavailable.") && !failure.Contains("fixture-secret"),"SSH setup failures retain useful command details and redact credentials");
            failStart=false;port=true;calls.Clear();await restarted.Set(new(true,key));await restarted.Set(new(false));
            check(port && !calls.Any(c=>c.Args is ["--remove-port=22/tcp"]),"A firewall port opened before diagnostics is retained");
            await Reports(root,check);
        }
        finally{Directory.Delete(root,true);}
    }
    static async Task Reports(string root,Action<bool,string> check)
    {
        var proc=root+"/proc/42";Directory.CreateDirectory(proc+"/fd");File.CreateSymbolicLink(proc+"/fd/3","/dev/nvidia3");File.CreateSymbolicLink(proc+"/exe","/usr/bin/nvidia-persistenced");
        File.WriteAllText(proc+"/comm","nvidia-persiste");File.WriteAllText(proc+"/cgroup","0::/system.slice/nvidia-persistenced.service");File.WriteAllText(proc+"/status","Uid:\t0\t0\t0\t0\n");
        GpuOwner Owner(bool detailed=true)=>GpuOwnership.ObserveProcesses(["/dev/nvidia3"],procRoot:root+"/proc",includeIdentity:detailed).Single();
        check(Owner(false).Identity==null && Owner().Identity is {Executable:"/usr/bin/nvidia-persistenced",PersistenceAccount:true,PersistenceService:true,PersistenceExecutable:true,ComputeContext:false} && Owner().Identity!.Uids!.SequenceEqual([0u,0u,0u,0u]) && !Owner().Blocking,"Read-only diagnostics expose the exact UID and executable checks without changing ownership policy");
        File.Delete(proc+"/exe");File.CreateSymbolicLink(proc+"/exe","/usr/libexec/nvidia-persistenced");
        check(Owner() is {Blocking:true,Identity.PersistenceExecutable:false,Identity.PersistenceService:true,Identity.PersistenceAccount:true},"A mismatched daemon executable is explained instead of silently exempted");
        var gpu=new GpuDevice("0000:01:00.0","NVIDIA","Fixture GPU","nvidia","GPU-test",24576,[],[]);
        int commands=0;
        var diagnostics=new SystemDiagnostics((exe,args,seconds)=>{
            commands++;check(seconds==10 && exe is "journalctl" or "systemctl","System report commands are fixed and bounded");
            if(commands==2)throw new IOException("fixture failure");
            return Task.FromResult(new ProcessResult(commands==3?1:0,"password=fixture-secret\n"+new string('x',300000)));
        },()=>Task.FromResult(new[]{gpu}),_=>Task.FromResult(new[]{Owner()}));
        var report=await diagnostics.Collect();var text=report.ToJsonString();
        check(commands==4 && !text.Contains("fixture-secret") && text.Contains("[REDACTED]") && text.Contains("[truncated]"),"System reports redact credentials and bound each probe output");
        check(report["probes"]![1]!["exitCode"]!.GetValue<int>()==-1 && report["probes"]![2]!["exitCode"]!.GetValue<int>()==1 && report["gpuOwners"]![gpu.Pci]![0]!["identity"]!["executable"]!.GetValue<string>()=="/usr/libexec/nvidia-persistenced","Unavailable and failed probes stay visible while GPU identity details are retained");
        var entered=new TaskCompletionSource();var release=new TaskCompletionSource();
        var concurrent=new SystemDiagnostics(async(exe,args,seconds)=>{entered.TrySetResult();await release.Task;return new(0,"");},()=>Task.FromResult(Array.Empty<GpuDevice>()));
        var first=concurrent.Collect();await entered.Task;bool rejected=false;
        try{await concurrent.Collect();}catch(InvalidOperationException){rejected=true;}
        release.SetResult();await first;check(rejected,"Repeated system-report requests do not start overlapping root probes");
        // Exercise real pipe draining with disposable command fixtures, including multibyte output.
        var bin=root+"/bin";Directory.CreateDirectory(bin);
        foreach(var command in new[]{"journalctl","systemctl"})
        {
            var path=bin+"/"+command;
            File.WriteAllText(path,"#!/usr/bin/python3\nimport sys\nsys.stdout.buffer.write('🙂'.encode()*400000)\nsys.stderr.write('fixture stderr\\n')\n");
            File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        }
        var previous=Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH",bin+":"+previous);
            var bounded=await new SystemDiagnostics(inventory:()=>Task.FromResult(Array.Empty<GpuDevice>())).Collect();
            check(bounded["probes"]!.AsArray().All(p=>p!["exitCode"]!.GetValue<int>()==0 && p["truncated"]!.GetValue<bool>() && Encoding.UTF8.GetByteCount(p["output"]!.GetValue<string>())<=256*1024+32),"Real report probes drain oversized stdout/stderr without retaining more than 256 KiB");
        }
        finally{Environment.SetEnvironmentVariable("PATH",previous);}
    }
}
