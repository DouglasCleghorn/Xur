using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

public static class T3CodeRuntime
{
    public const string Label="io.xur.t3-bootstrap";
    public const string ToolPath=T3CodeWorkload.Home+"/.local/bin:"+T3CodeWorkload.Home+"/.opencode/bin:"+T3CodeWorkload.Home+"/.local/share/xur-tools/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
    public static string Policy {get;}=Canonical.Hash(new{Launcher=T3CodeWorkload.Launcher,Hostname=Environment.MachineName,Path=ToolPath,HostBootstrap});
    // Saved recipes/images keep their volume and identity. Replace an older
    // container once so its entrypoint also gets the current startup behavior.
    public static string Fingerprint(Workload workload,JsonElement labels)=>
        labels.TryGetProperty(Label,out var bootstrap)&&bootstrap.GetString()==Policy
            ?T3CodeGpuAccess.Fingerprint(workload,labels)
            :Canonical.Hash(new{Workload=workload.Fingerprint,Bootstrap="legacy"});
    public static string[] Command(Recipe recipe,bool fullHostAccess=false)
    {
        string[] command=["sh","-c",T3CodeWorkload.Launcher,"t3-launcher",..recipe.Command.Skip(1)];
        return fullHostAccess?["python3","-c",HostBootstrap,..command]:command;
    }
    // Keep T3, native updates and credentials owned by node in either mode.
    // Only the explicitly enabled container receives this root-owned setuid tool.
    public const string HostBootstrap="""
        import os, shutil, sys
        os.makedirs("/usr/local/libexec", exist_ok=True)
        binary = "/usr/local/libexec/xur-host-chroot"
        shutil.copyfile(shutil.which("chroot"), binary)
        os.chown(binary, 0, 0)
        os.chmod(binary, 0o4755)
        with open("/usr/local/bin/xur-host", "w") as helper:
            helper.write('#!/bin/sh\nexec /usr/local/libexec/xur-host-chroot /host /usr/bin/nsenter --target 1 --mount --uts --ipc --net --pid --cgroup --root --wd=/ --setuid 0 --setgid 0 -- /usr/bin/env HOME=/root USER=root LOGNAME=root PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin "$@"\n')
        os.chown("/usr/local/bin/xur-host", 0, 0)
        os.chmod("/usr/local/bin/xur-host", 0o755)
        os.setgroups(sorted(set(os.getgroups()) - {0} | {1000}))
        os.setgid(1000)
        os.setuid(1000)
        os.execvp(sys.argv[1], sys.argv[1:])
        """;
    public static string[] ContainerArguments()=>["--hostname",Environment.MachineName,"--label",Label+"="+Policy,"--env","PATH="+ToolPath];
}
