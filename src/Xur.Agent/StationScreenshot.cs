using System.Text;
using System.Text.RegularExpressions;
using Xur.Domain;
using Xur.IO;
namespace Xur.Agent;

public static class StationScreenshot
{
    // Capture in a private runtime directory, then remove the image and isolated
    // Spectacle settings. Never change the desktop user's clipboard or settings.
    public const string CaptureScript="""
        set -euo pipefail
        test -x /usr/bin/spectacle
        temporary=$(mktemp -d "$RUNTIME_DIRECTORY/capture.XXXXXXXX")
        trap 'rm -rf -- "$temporary"' EXIT
        mkdir "$temporary/config" "$temporary/data" "$temporary/cache"
        printf '[General]\nclipboardGroup=PostScreenshotDoNothing\nautoSaveImage=false\n' > "$temporary/config/spectaclerc"
        XDG_CONFIG_HOME="$temporary/config" XDG_DATA_HOME="$temporary/data" XDG_CACHE_HOME="$temporary/cache" /usr/bin/spectacle --new-instance --background --nonotify --fullscreen --output "$temporary/desktop.png" >&2
        test "$(stat -c %s -- "$temporary/desktop.png")" -le 33554432
        cat -- "$temporary/desktop.png"
        """;

    public static async Task<byte[]> Capture(Workload w,CancellationToken cancellation=default,
        Func<string,string[],int,CancellationToken,Task<CommandResult>>? runner=null)
    {
        Task<CommandResult> Run(string exe,string[] args,int seconds)=>runner!=null?runner(exe,args,seconds,cancellation):CommandRunner.Run(exe,args,seconds,cancellation);
        StationAccounts.ValidateForStop(w);
        var user=StationAccounts.Username(w);
        if(!ProfilePolicy.UserName(user))throw new InvalidOperationException("Invalid workstation user.");
        var identity=await Run("id",["-u",user],5);
        if(identity.ExitCode!=0||!int.TryParse(Encoding.UTF8.GetString(identity.Output).Trim(),out var uid)||uid<1000||uid>=65534||w.User is {Temporary:false} account&&account.Uid!=uid)
            throw new InvalidOperationException("The workstation user session is unavailable.");
        var environment=await Run("runuser",["-u",user,"--","env","XDG_RUNTIME_DIR=/run/user/"+uid,"DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/"+uid+"/bus","systemctl","--user","show-environment"],5);
        var wayland=Encoding.UTF8.GetString(environment.Output).Split('\n').FirstOrDefault(l=>l.StartsWith("WAYLAND_DISPLAY="))?[16..];
        if(environment.ExitCode!=0||wayland==null||!Regex.IsMatch(wayland,@"\A[a-zA-Z0-9_-]+\z"))
            throw new InvalidOperationException("The workstation Wayland session is unavailable.");
        // The existing user slice retains its GPU/device boundary. A service
        // deadline also cleans up the capture if the HTTP client disconnects.
        var unit="xur-preview-"+Guid.NewGuid().ToString("N");
        var result=await Run("systemd-run",["--quiet","--wait","--collect","--pipe","--service-type=exec","--unit="+unit,
            "--property=User="+user,"--property=Slice=user-"+uid+".slice","--property=RuntimeMaxSec=15","--property=TimeoutStopSec=2","--property=UMask=0077","--property=NoNewPrivileges=true",
            "--property=RuntimeDirectory="+unit,"--property=RuntimeDirectoryMode=0700",
            "--setenv=HOME=/var/home/"+user,"--setenv=XDG_RUNTIME_DIR=/run/user/"+uid,"--setenv=DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/"+uid+"/bus",
            "--setenv=WAYLAND_DISPLAY="+wayland,"--setenv=QT_QPA_PLATFORM=wayland","--setenv=XDG_CURRENT_DESKTOP=KDE","/bin/bash","-c",CaptureScript],20);
        if(result.ExitCode!=0||result.Output.Length is <8 or >33554432||!result.Output.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))
            throw new InvalidOperationException("Could not capture the desktop. Check that Spectacle is installed and the workstation display is ready, then refresh.");
        return result.Output;
    }
}
