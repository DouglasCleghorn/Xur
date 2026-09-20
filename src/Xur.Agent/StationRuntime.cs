using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

// One native local seat. The upstream desktop stays installed but dormant until
// a profile owns it; its dedicated Unix user owns every desktop child process.
public sealed class StationRuntime(DisplayConsoles? consoles=null)
{
    const string Config="/etc/plasmalogin.conf.d/90-xur-workstation.conf";
    static string Unit(Workload w)=>"xur-station-"+w.Id+".service";
    readonly StationAccounts accounts=new();
    static string User(Workload w)=>StationAccounts.Username(w);
    static async Task<string> Run(string command,string[] args,int timeout=30)
    {
        var r=await Processes.Run(command,args,timeout);
        if(r.ExitCode!=0)throw new InvalidOperationException(Redaction.Logs(command+": "+r.Output));
        return r.Output.Trim();
    }
    public async Task<RuntimeInstance?> Inspect(Workload w)
    {
        var r=await Processes.Run("systemctl",["show",Unit(w),"--property=LoadState,ActiveState,MainPID,InvocationID"],10);
        if(r.ExitCode!=0)return null;
        var fields=r.Output.Split('\n').Where(l=>l.Contains('=')).Select(l=>l.Split('=',2)).ToDictionary(p=>p[0],p=>p[1]);
        if(fields.GetValueOrDefault("LoadState")=="not-found")return null;
        int.TryParse(fields.GetValueOrDefault("MainPID"),out var pid);
        var invocation=fields.GetValueOrDefault("InvocationID","");if(invocation.Length==0)return null;
        return new(w.Id,w.Fingerprint,invocation,pid,File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim(),"",fields.GetValueOrDefault("ActiveState")=="active"?"running":"exited",w.Gpus);
    }
    public async Task<RuntimeInstance> Start(Workload w,GpuDevice gpu)
    {
        if(!File.Exists("/usr/bin/plasmalogin") || !File.Exists("/usr/bin/startplasma-wayland"))throw new InvalidOperationException("The installed OS does not contain the Plasma workstation runtime.");
        var existing=await Inspect(w);
        var headless=gpu.Displays is not {Length:>0};
        var user=User(w);var home="/var/home/"+user;
        if(existing?.State!="running")
        {
            var units=new StationUnits();
            await units.Retire("xur-stream-"+w.Id+".service",stopActive:true);
            await units.Retire(StationVirtualDisplay.Unit(w.Id),stopActive:true);
            await units.Retire(Unit(w));
            if(consoles!=null)await consoles.Release(gpu.Pci);
            foreach(var manager in new[]{"plasmalogin.service","sddm.service","gdm.service"})
                if((await Processes.Run("systemctl",["is-active",manager],10)).ExitCode==0)throw new InvalidOperationException("Another display manager already owns the local seat.");
            if((await Processes.Run("id",["-u",user],10)).ExitCode==0 && (await Processes.Run("pgrep",["-u",user],10)).ExitCode!=1)throw new InvalidOperationException("The selected user already has an active session. Sign out before loading this workstation.");
            var account=await accounts.Prepare(w);home=account.Home;
            new StationNetworkPolicy().Apply(w.Id,user);
            var uid=await Run("id",["-u",user]);
            await new StationDeviceAccess().GrantAccess(w.Id,int.Parse(uid),gpu);
            // All user-manager scopes inherit this boundary, including Steam,
            // games, PipeWire and apps launched by Plasma through systemd.
            var policy="/run/systemd/system/user-"+uid+".slice.d";Directory.CreateDirectory(policy);
            var devices=(gpu.Cards??[]).Concat(gpu.Nodes).ToList();
            if(gpu.Vendor=="NVIDIA")
            {
                devices.AddRange(await NvidiaDevice.WorkstationNodes(gpu));
            }
            await Run("modprobe",["uinput"]);
            await Run("udevadm",["settle","--timeout=10"]);
            if(!File.Exists("/dev/uinput"))throw new InvalidOperationException("Workstation input device /dev/uinput was not created.");
            await File.WriteAllTextAsync(policy+"/50-xur.conf","[Slice]\nDevicePolicy=closed\n"+string.Join('\n',devices.Select(d=>"DeviceAllow="+d+" rw"))+"\nDeviceAllow=/dev/uinput rw\nDeviceAllow=char-input rw\nDeviceAllow=char-alsa rw\nDeviceAllow=char-hidraw rw\nDeviceAllow=/dev/tty rw\nDeviceAllow=char-tty rw\n");
            // Write user configuration as that user, so symlinks in their home
            // cannot turn a profile change into privileged file writes.
            await Run("runuser",["-u",user,"--","/bin/bash","-c",UserConfiguration,"xur",home,gpu.Cards![0],headless?"headless":"local",string.Join("\n",StationGraphics.LaunchEnvironment(gpu))]);
            await StationPower.Apply(user,headless);
            await Run("restorecon",["-RF",home]);
            // Register the current bundle's helper before KWin starts reading
            // desktop permissions, including the first load after an update.
            if(headless)await StationVirtualDisplay.Prepare(user);
            Directory.CreateDirectory(Path.GetDirectoryName(Config)!);
            await File.WriteAllTextAsync(Config,"[Autologin]\nUser="+user+"\nSession=plasma.desktop\nRelogin=false\n");
            await Run("systemctl",["daemon-reload"]);
            await Processes.Run("systemctl",["reset-failed",Unit(w)],10);
            // Headless desktops need the same active logind/DRM input session
            // as local desktops. KWin --virtual omits the libinput backend.
            await Run("systemd-run",["--unit="+Unit(w),"--collect","--property=Type=exec","--property=KillMode=control-group","--property=TimeoutStopSec=30","--setenv=LANG=C.UTF-8","--setenv="+TimezoneSettings.StationEnvironment,"--setenv=KWIN_DRM_DEVICES="+gpu.Cards![0],"/usr/bin/plasmalogin"]);
        }
        if(existing?.State=="running")await StationPower.Apply(user,headless);
        for(var n=0;n<90;n++)
        {
            var instance=await Inspect(w);
            if(instance?.State!="running")throw new InvalidOperationException("The workstation exited. Open its logs for details.");
            if(headless && (await Processes.Run("pgrep",["-u",user,"-x","kwin_wayland"],5)).ExitCode==0)
            {
                var uid=await Run("id",["-u",user]);
                if(!await StationVirtualDisplay.TryStart(w.Id,user,uid)){await Task.Delay(1000);continue;}
            }
            if((await Processes.Run("pgrep",["-u",user,"-x","plasmashell"],10)).ExitCode==0 && (await Processes.Run("pgrep",["-u",user,"-x","kwin_wayland"],10)).ExitCode==0 && (await Processes.Run("pgrep",["-u",user,"-x","ksplashqml"],10)).ExitCode==1)
            {
                await StationStreaming.Start(w,gpu);
                return instance;
            }
            await Task.Delay(1000);
        }
        throw new InvalidOperationException("The desktop did not become ready. Open its logs, then resume or stop it.");
    }
    public async Task Stop(Workload w)
    {
        var user=User(w);StationAccounts.ValidateForStop(w);
        await StationStreaming.Stop(w.Id);
        await StationVirtualDisplay.Stop(w.Id);
        await Processes.Run("systemctl",["stop",Unit(w)],45);
        if((await Processes.Run("id",["-u",user],10)).ExitCode!=0){new StationNetworkPolicy().Remove(w.Id);return;}
        await Processes.Run("loginctl",["terminate-user",user],30);
        await Processes.Run("pkill",["-KILL","-u",user],10);
        for(var n=0;n<30;n++)
        {
            if((await Processes.Run("pgrep",["-u",user],10)).ExitCode==1)break;
            if(n==29)throw new InvalidOperationException("Desktop processes have not stopped.");
            await Task.Delay(500);
        }
        new StationNetworkPolicy().Remove(w.Id);
        if(File.Exists(Config) && (await File.ReadAllTextAsync(Config)).Contains("User="+user+"\n"))File.Delete(Config);
        var uid=await Run("id",["-u",user]);
        File.Delete("/run/systemd/system/user-"+uid+".slice.d/50-xur.conf");
        await Processes.Run("systemctl",["daemon-reload"],10);
        await Run("runuser",["-u",user,"--","/bin/bash","-c","rm -f -- \"$HOME/.config/systemd/user/plasma-kwin_wayland.service.d/90-xur-headless.conf\""]);
        await new StationDeviceAccess().Revoke(w.Id);
        await accounts.RemoveTemporary(w);
        await Processes.Run("chvt",["3"],10);
    }
    internal const string UserConfiguration="""
        set -eu
        umask 077
        mkdir -p "$1/.config/autostart" "$1/.config/environment.d"
        for app in bazzite-portal steam bazzite-announcement; do
          if ! test -e "$1/.config/autostart/$app.desktop"; then
            printf '[Desktop Entry]\nType=Application\nName=%s\nHidden=true\n' "$app" > "$1/.config/autostart/$app.desktop"
          fi
        done
        mkdir -p "$1/.config/systemd/user/plasma-kwin_wayland.service.d"
        if [ "$3" = headless ]; then
          printf '[Service]\nExecStart=\nExecStart=/usr/bin/kwin_wayland_wrapper --xwayland --drm --no-lockscreen\n' > "$1/.config/systemd/user/plasma-kwin_wayland.service.d/90-xur-headless.conf"
        else
          rm -f "$1/.config/systemd/user/plasma-kwin_wayland.service.d/90-xur-headless.conf"
        fi
        printf 'TZ=:/etc/localtime\nKWIN_DRM_DEVICES=%s\n' "$2" > "$1/.config/environment.d/90-xur-gpu.conf"
        printf '%s\n' "$4" >> "$1/.config/environment.d/90-xur-gpu.conf"
        printf '[Daemon]\nAutolock=false\nLockOnResume=false\n' > "$1/.config/kscreenlockerrc"
        """;
    public async Task<string> Logs(Workload w)=> (await Processes.Run("journalctl",["--no-pager","-n","200","-u",Unit(w),"-u","xur-stream-"+w.Id+".service","-u",StationVirtualDisplay.Unit(w.Id)],10)).Output;
}
