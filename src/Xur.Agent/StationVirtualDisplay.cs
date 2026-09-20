using System.Text.RegularExpressions;
using Xur.Domain;
namespace Xur.Agent;
public static class StationVirtualDisplay
{
    public static string Unit(string id)=>"xur-virtual-output-"+id+".service";
    static readonly object installation=new();
    static string Install() {lock(installation){return InstallLocked();}}
    static string InstallLocked()
    {
        var path="/var/lib/xur-virtual-display/"+ApplicationIdentity.Id;
        Directory.CreateDirectory(path);File.SetUnixFileMode("/var/lib/xur-virtual-display",(UnixFileMode)493);File.SetUnixFileMode(path,(UnixFileMode)493);
        var executable=path+"/xur-virtual-output";
        if(!File.Exists(executable))
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory,"virtual-display","xur-virtual-output"),executable+".tmp",true);
            File.SetUnixFileMode(executable+".tmp",(UnixFileMode)493);File.Move(executable+".tmp",executable,true);
        }
        return executable;
    }
    public static async Task<bool> TryStart(string id,string user,string uid)
    {
        if(!ProfilePolicy.EntityIdentifier(id)||!ProfilePolicy.UserName(user)||!int.TryParse(uid,out var number)||number<1000||number>=65534)throw new InvalidOperationException("Invalid workstation virtual monitor identity.");
        if((await Processes.Run("systemctl",["is-active",Unit(id)],5)).ExitCode==0)return true;
        await new StationUnits().Retire(Unit(id));
        var environment=await Processes.Run("runuser",["-u",user,"--","env","XDG_RUNTIME_DIR=/run/user/"+uid,"DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/"+uid+"/bus","systemctl","--user","show-environment"],5);
        var wayland=environment.Output.Split('\n').FirstOrDefault(l=>l.StartsWith("WAYLAND_DISPLAY="))?[16..];
        if(wayland==null)return false;
        if(!Regex.IsMatch(wayland,@"^[a-zA-Z0-9_-]+$"))throw new InvalidOperationException("Invalid workstation Wayland socket.");
        var executable=await Prepare(user);
        // Older KWin releases authorize this interface through the executable's
        // desktop entry. Write it as the user, without weakening global checks.
        await Check("runuser",["-u",user,"--","env","XDG_RUNTIME_DIR=/run/user/"+uid,"DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/"+uid+"/bus","kbuildsycoca6","--noincremental"]);
        // KWin receives the service database change asynchronously, even after
        // kbuildsycoca exits. Match Sunshine's permission propagation grace.
        await Task.Delay(3000);
        var label=await Processes.Run("semanage",["fcontext","-a","-t","bin_t","/var/lib/xur-virtual-display(/.*)?"],30);
        if(label.ExitCode!=0)await Check("semanage",["fcontext","-m","-t","bin_t","/var/lib/xur-virtual-display(/.*)?"]);
        await Check("restorecon",["-RF","/var/lib/xur-virtual-display"]);
        await Processes.Run("systemctl",["reset-failed",Unit(id)],5);
        try
        {
            await Check("systemd-run",["--unit="+Unit(id),"--collect","--property=Type=notify","--property=TimeoutStartSec=20","--property=User="+user,"--property=Slice=user-"+uid+".slice","--property=PartOf=xur-station-"+id+".service","--property=Restart=on-failure","--property=RestartSec=2","--property=StartLimitBurst=3","--property=StartLimitIntervalSec=60","--setenv=XDG_RUNTIME_DIR=/run/user/"+uid,"--setenv=WAYLAND_DISPLAY="+wayland,executable]);
        }
        catch(InvalidOperationException)
        {
            var logs=await Processes.Run("journalctl",["--no-pager","-b","-n","12","-u",Unit(id)],5);
            throw new InvalidOperationException("Could not prepare the workstation virtual monitor: "+Redaction.Logs(logs.Output));
        }
        return true;
    }
    public static async Task<string> Prepare(string user)
    {
        if(!ProfilePolicy.UserName(user))throw new InvalidOperationException("Invalid workstation user.");
        var executable=Install();
        await Check("runuser",["-u",user,"--","bash","-c",Authorize,"xur",executable,ApplicationIdentity.Id]);
        return executable;
    }
    const string Authorize="""
        set -eu
        mkdir -p "$HOME/.local/share/applications"
        printf '[Desktop Entry]\nType=Application\nName=Xur virtual monitor\nNoDisplay=true\nExec=%s\nX-KDE-Wayland-Interfaces=zkde_screencast_unstable_v1\n' "$1" > "$HOME/.local/share/applications/dev.xur.VirtualDisplay.$2.desktop"
        """;
    static async Task Check(string command,string[] args)
    {
        var result=await Processes.Run(command,args,30);
        if(result.ExitCode!=0)throw new InvalidOperationException("Could not prepare the workstation virtual monitor: "+Redaction.Logs(result.Output));
    }
    public static async Task Stop(string id)=>await Processes.Run("systemctl",["stop",Unit(id)],25);
}
