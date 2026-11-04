using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

public static class T3CodeRuntime
{
    public const string Label="io.xur.t3-bootstrap";
    public const string ToolPath=T3CodeWorkload.Home+"/.local/share/xur-docker/current/usr/bin:"+T3CodeWorkload.Home+"/.local/bin:"+T3CodeWorkload.Home+"/.opencode/bin:"+T3CodeWorkload.Home+"/.local/share/xur-tools/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
    public static string Policy {get;}=Canonical.Hash(new{Launcher=T3CodeWorkload.Launcher,Hostname=Environment.MachineName,Path=ToolPath,HostBootstrap,DockerBootstrap,RuntimePolicy="rootless-dind-v1"});
    // Saved recipes/images keep their volume and identity. Replace an older
    // container once so its entrypoint also gets the current startup behavior.
    public static string Fingerprint(Workload workload,JsonElement labels)=>
        labels.TryGetProperty(Label,out var bootstrap)&&bootstrap.GetString()==Policy
            ?T3CodeGpuAccess.Fingerprint(workload,labels)
            :Canonical.Hash(new{Workload=workload.Fingerprint,Bootstrap="legacy"});
    public static string[] Command(Recipe recipe,bool fullHostAccess=false,bool sharedStorage=false)
    {
        string[] command=["sh","-c",T3CodeWorkload.Launcher,"t3-launcher",..recipe.Command.Skip(1)];
        if(fullHostAccess)command=["python3","-c",HostBootstrap,..command];
        return ["python3","-c",DockerBootstrap,fullHostAccess?"host":sharedStorage?"shared":"private",..command];
    }
    // Fixed commands enter the inspected runtime; native setup joins only its
    // mount namespace, then drops root before touching user data or credentials.
    public static string[] Exec(string instance,string[] command,bool interactive=false)
    {
        if(!instance.StartsWith("native:",StringComparison.Ordinal))
            return ["podman","exec",..(interactive?new[]{"--interactive"}:[]),"--user","1000:1000","--workdir",T3CodeWorkload.Home,instance,..command];
        var fields=instance.Split(':');
        if(fields.Length!=3||!int.TryParse(fields[1],out var pid)||pid<=0||fields[2].Length!=32||!fields[2].All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("Invalid native T3 instance.");
        return ["nsenter","--target",fields[1],"--mount","--root","--wdns="+T3CodeWorkload.Home,"--","/usr/bin/setpriv","--reuid=1000","--regid=1000","--clear-groups",
            "/usr/bin/env","HOME="+T3CodeWorkload.Home,"PATH="+T3NativeRuntime.ToolPath,"XUR_T3_DOCKER=0",..command];
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
    // Root only prepares the disposable image's namespace helpers. Docker and
    // T3 then run as node; signed Docker packages live in its persistent home.
    public const string DockerBootstrap="""
        import os, shutil, subprocess, sys
        if not all(shutil.which(tool) for tool in ("newuidmap", "newgidmap", "iptables", "slirp4netns", "fuse-overlayfs", "setpriv")):
            subprocess.run(["apt-get", "update"], check=True, timeout=180)
            subprocess.run(["apt-get", "install", "-y", "--no-install-recommends", "uidmap", "iptables", "slirp4netns", "fuse-overlayfs", "util-linux"], check=True, timeout=300)
        for path in ("/etc/subuid", "/etc/subgid"):
            entries = open(path).read() if os.path.exists(path) else ""
            if not any(line.startswith(("node:", "1000:")) for line in entries.splitlines()):
                with open(path, "a") as stream:
                    stream.write("\nnode:100000:65536\n")
        os.makedirs("/run/user/1000", mode=0o700, exist_ok=True)
        os.chown("/run/user/1000", 1000, 1000)
        os.chmod("/run/user/1000", 0o700)
        mode = sys.argv[1]
        command = sys.argv[2:]
        if mode != "host":
            os.setgroups(sorted(set(os.getgroups()) - {0} | {1000}))
            caps = "-all,+dac_override" if mode == "shared" else "-all"
            command = ["setpriv", "--reuid=1000", "--regid=1000", "--keep-groups", "--inh-caps=" + caps, "--ambient-caps=" + caps, *command]
        os.execvp(command[0], command)
        """;
    public static string[] ContainerArguments()=>["--tmpfs","/run/user/1000:rw,nosuid,nodev,noexec,mode=0700,uid=1000,gid=1000","--privileged","--userns=host","--user","0:0","--env","XUR_T3_DOCKER=1","--env","XDG_RUNTIME_DIR=/run/user/1000","--env","DOCKER_HOST=unix:///run/user/1000/docker.sock","--hostname",Environment.MachineName,"--label",Label+"="+Policy,"--env","PATH="+ToolPath];
}
