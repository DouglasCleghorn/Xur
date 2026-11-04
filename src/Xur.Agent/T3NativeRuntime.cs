using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

// Native systemd processes use host binaries and a filesystem namespace to
// retain the SAME home and project paths, including on an immutable host root. No copying/chowning databases, credentials, Git worktrees or runtime links.
public sealed class T3NativeRuntime(string directory,T3Storage storage,Func<string> mode,
    Func<string,string[],int,Task<ProcessResult>>? execute=null,Func<Task<string[]>>? gpuArguments=null)
{
    public const string Unit="xur-t3-code-native.service";
    readonly Func<string,string[],int,Task<ProcessResult>> run=execute??((exe,args,seconds)=>Processes.Run(exe,args,seconds));
    public const string ToolPath=T3CodeWorkload.Home+"/.local/share/xur-native/bin:"+T3CodeRuntime.ToolPath;
    string Root=>Path.Combine(directory,"t3-native-root");
    string Marker=>Path.Combine(directory,"t3-native-owner");
    string Policy(Workload workload)=>Canonical.Hash(new{workload.Fingerprint,storage.CurrentPolicy,Launcher=T3CodeWorkload.NativeLauncher,Mode="Native"});
    static string Description(Workload w)=>"Xur T3 native "+w.Id+" ";
    public async Task<RuntimeInstance?> Inspect(Workload w)
    {
        if(File.Exists(Marker)&&File.ReadAllText(Marker)!=w.Fingerprint)throw new InvalidOperationException("The native T3 service belongs to another installation.");
        var result=await run("systemctl",["show",Unit,"--property=LoadState,ActiveState,MainPID,InvocationID,Description"],10);
        var fields=result.Output.Split('\n').Where(l=>l.Contains('=')).Select(l=>l.Split('=',2)).ToDictionary(p=>p[0],p=>p[1]);
        if(fields.GetValueOrDefault("LoadState")=="not-found")return null;
        if(result.ExitCode!=0)
        {
            if(mode()=="Container"&&!File.Exists(Marker))return null;
            throw new IOException("Could not inspect the native T3 service.");
        }
        int.TryParse(fields.GetValueOrDefault("MainPID"),out var pid);
        var state=fields.GetValueOrDefault("ActiveState","");
        if(pid<=0&&state is not ("active" or "activating" or "deactivating"))return null;
        var description=fields.GetValueOrDefault("Description","");
        var invocation=fields.GetValueOrDefault("InvocationID","");
        if(!description.StartsWith(Description(w),StringComparison.Ordinal)||invocation.Length!=32||!invocation.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("The native T3 service identity conflicts with its saved workload.");
        var fingerprint=mode()=="Native"&&description==Description(w)+Policy(w)?w.Fingerprint:"stale-native:"+w.Fingerprint;
        return new(w.Id,fingerprint,"native:"+pid+":"+invocation,pid,File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim(),
            "http://127.0.0.1:3773/",state=="active"&&pid>0?"running":state=="deactivating"||pid<=0?"stopping":"starting",w.Gpus);
    }
    public async Task<string> Home(Workload w)
    {
        var volume=w.Recipe.Container!.Mounts.Single().Volume;
        await ContainerLibrary.VerifyVolume(volume,run);
        var inspect=await run("podman",["volume","inspect",volume],10);
        if(inspect.ExitCode!=0)throw new IOException("T3's persistent home is unavailable.");
        using var doc=JsonDocument.Parse(inspect.Output);
        var home=doc.RootElement[0].GetProperty("Mountpoint").GetString()!;
        if(!Path.IsPathFullyQualified(home)||home.Any(char.IsControl)||home.Contains(':')||!Directory.Exists(home))
            throw new InvalidOperationException("T3's existing home volume is unavailable. No replacement was created.");
        return home;
    }
    string[] HostPaths()
    {
        // A small service root supplies mount points such as /host on immutable
        // OS roots. It contains no OS image: every tool/library is the host's.
        Directory.CreateDirectory(Root);
        File.SetUnixFileMode(Root,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute|UnixFileMode.GroupRead|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherExecute);
        var binds=new List<string>();
        foreach(var part in new[]{"usr","etc","var","run","dev","proc","sys","tmp","boot","home","opt","srv","mnt","root","media","lib","lib64","bin","sbin"})
        {
            var source="/"+part;var destination=Path.Combine(Root,part);
            var target=new DirectoryInfo(source).LinkTarget;
            if(target!=null)
            {
                if(!Path.Exists(destination)&&new DirectoryInfo(destination).LinkTarget==null)Directory.CreateSymbolicLink(destination,target);
            }
            else if(Directory.Exists(source))
            {Directory.CreateDirectory(destination);binds.Add(Quote(source));}
        }
        foreach(var part in new[]{"host","models","model-sets"})Directory.CreateDirectory(Path.Combine(Root,part));
        return binds.ToArray();
    }
    static string Quote(string value)=>"\""+value.Replace("\\","\\\\").Replace("\"","\\\"").Replace("%","%%")+"\"";
    public async Task<string[]> Arguments(Workload w,string home,bool prepare=false)
    {
        var config=storage.Read();
        var args=new List<string>{"--unit="+(prepare?"xur-t3-code-prepare-native.service":Unit),"--collect","--service-type=exec","--expand-environment=no",
            "--property=Description="+Description(w)+Policy(w),"--property=User=1000","--property=Group=1000",
            "--property=RootDirectory="+Root,"--property=WorkingDirectory="+T3CodeWorkload.Home,"--property=UMask=0077","--property=KillMode=control-group",
            "--property=TimeoutStopSec=30","--property=TimeoutStartSec=30","--property=RuntimeMaxSec="+(prepare?"900":"infinity"),
            "--property=NoNewPrivileges="+(config.FullHostAccess&&!prepare?"no":"yes"),
            "--property=Environment=HOME="+T3CodeWorkload.Home+" PATH="+ToolPath+" XUR_T3_DOCKER=0"};
        var binds=new List<string>(HostPaths()){Quote(home+":"+T3CodeWorkload.Home)};
        if(prepare)args.AddRange(["--wait","--pipe"]);
        else
        {
            // Keep path aliases while using host processes and libraries. The
            // service also has the host user's ordinary filesystem access.
            var access=await storage.Arguments();
            var mounts=access.Select((value,index)=>(value,index)).Where(p=>p.value=="--volume").Select(p=>access[p.index+1].Split(':')).ToArray();
            binds.AddRange(mounts.Select(m=>Quote(m[0]+":"+m[1])));
            if(config.AllDrives||config.Models||config.Folders is {Length:>0})
                args.AddRange(["--property=AmbientCapabilities=CAP_DAC_OVERRIDE","--property=CapabilityBoundingSet=CAP_DAC_OVERRIDE"]);
            var gpu=gpuArguments!=null?await gpuArguments():await T3CodeGpuAccess.Arguments(await GpuInventory.Observe());
            var groups=gpu.Select((value,index)=>(value,index)).Where(p=>p.value=="--group-add").Select(p=>gpu[p.index+1]).ToArray();
            if(groups.Length>0)args.Add("--property=SupplementaryGroups="+string.Join(' ',groups));
            if(config.FullHostAccess)
            {
                var helper=Path.Combine(directory,"t3-native-host");Directory.CreateDirectory(helper);
                File.SetUnixFileMode(helper,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute|UnixFileMode.GroupRead|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherExecute);
                var binary=Path.Combine(helper,"chroot");
                File.Copy(File.Exists("/usr/bin/chroot")?"/usr/bin/chroot":"/usr/sbin/chroot",binary,true);
                File.SetUnixFileMode(binary,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute|UnixFileMode.GroupRead|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherExecute|UnixFileMode.SetUser);
                // This helper is root-owned outside the user-writable T3 home.
                File.WriteAllText(Path.Combine(helper,"xur-host"),"#!/bin/sh\nexec /run/xur-t3-host/chroot /host /usr/bin/nsenter --target 1 --mount --uts --ipc --net --pid --cgroup --root --wd=/ --setuid 0 --setgid 0 -- /usr/bin/env HOME=/root USER=root LOGNAME=root PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \"$@\"\n");
                File.SetUnixFileMode(Path.Combine(helper,"xur-host"),UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute|UnixFileMode.GroupRead|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherExecute);
                args.Add("--property=BindReadOnlyPaths="+Quote(helper+":/run/xur-t3-host"));
                args.RemoveAll(a=>a.StartsWith("--property=Environment=",StringComparison.Ordinal));
                args.Add("--property=Environment=HOME="+T3CodeWorkload.Home+" PATH=/run/xur-t3-host:"+ToolPath+" XUR_T3_DOCKER=0");
                // A root host helper needs its host capabilities after setuid.
                args.RemoveAll(a=>a.StartsWith("--property=CapabilityBoundingSet=",StringComparison.Ordinal));
            }
        }
        args.Add("--property=BindPaths="+string.Join(' ',binds));
        args.AddRange(["/bin/sh","-c",T3CodeWorkload.NativeLauncher,"t3-native",..(prepare?["xur-native-prepare"]:w.Recipe.Command.Skip(1))]);
        return args.ToArray();
    }
    public async Task Prepare(Workload w)
    {
        var home=await Home(w);
        await Arguments(w,home); // Validate GPU groups and selected aliases before stopping the live server.
        await run("systemctl",["stop","xur-t3-code-prepare-native.service"],30);
        await run("systemctl",["reset-failed","xur-t3-code-prepare-native.service"],10);
        var result=await run("systemd-run",await Arguments(w,home,prepare:true),930);
        if(result.ExitCode!=0)throw new InvalidOperationException("Native T3 prerequisites could not be prepared. "+Redaction.Logs(result.Output));
    }
    public async Task<RuntimeInstance> Start(Workload w,CancellationToken cancellationToken=default)
    {
        var current=await Inspect(w);
        if(current?.State!="running")
        {
            if(current!=null)await Stop(w,new(w.Id,current.InstanceId,current.Pid,current.BootId));
            var home=await Home(w);
            await run("systemctl",["reset-failed",Unit],10);
            NetworkShares.Write(Marker,w.Fingerprint);
            var started=await run("systemd-run",await Arguments(w,home),40);
            if(started.ExitCode!=0)throw new InvalidOperationException("Native T3 startup failed. "+Redaction.Logs(started.Output));
        }
        using var http=new HttpClient(new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(2)};
        for(var attempt=0;attempt<600;attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current=await Inspect(w);
            if(current==null)throw new InvalidOperationException("Native T3 exited before becoming ready. "+await Logs());
            try{using var response=await http.GetAsync(current.Endpoint);if(response.IsSuccessStatusCode)return current;}
            catch(Exception e)when(e is HttpRequestException or TaskCanceledException){}
            await Task.Delay(1000,cancellationToken);
        }
        throw new InvalidOperationException("Native T3 did not become ready. Check its workload logs.");
    }
    public async Task Stop(Workload w,RuntimeStop expected)
    {
        var current=await Inspect(w);if(current==null){File.Delete(Marker);return;}
        if(current.InstanceId!=expected.InstanceId||expected.Pid!=null&&current.Pid!=expected.Pid||expected.BootId!=null&&current.BootId!=expected.BootId)
            throw new InvalidOperationException("The native T3 instance changed outside this operation.");
        var stopped=await run("systemctl",["stop",Unit],45);
        if(stopped.ExitCode!=0||await Inspect(w)!=null)throw new InvalidOperationException("Native T3 has not stopped; refusing to start another server on its database.");
        File.Delete(Marker);
    }
    public async Task<string> Logs()=>(await run("journalctl",["--unit="+Unit,"--no-pager","--lines=200","--output=cat"],10)).Output;
}
