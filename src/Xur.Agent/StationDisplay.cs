using System.Text.Json;
using System.Globalization;
using Xur.Domain;
namespace Xur.Agent;
public static class StationDisplay
{
    public static string ExecutablePath=>"/var/lib/xur-virtual-display/"+ApplicationIdentity.Id+"/xurutil";
    static readonly SemaphoreSlim installation=new(1,1);
    public static async Task Install()
    {
        await installation.WaitAsync();try
        {
            var directory=Path.GetDirectoryName(ExecutablePath)!;
            if(Directory.Exists(directory))return;
            var source=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../host/xurutil"));
            if(!File.Exists(source))throw new InvalidOperationException("The virtual display utility is missing from the application bundle.");
            var parent=Path.GetDirectoryName(directory)!;
            Directory.CreateDirectory(parent);File.SetUnixFileMode(parent,(UnixFileMode)493);
            var stage=directory+".tmp";
            if(Directory.Exists(stage))Directory.Delete(stage,true);
            Directory.CreateDirectory(stage);File.SetUnixFileMode(stage,(UnixFileMode)493);
            File.Copy(source,stage+"/xurutil");File.SetUnixFileMode(stage+"/xurutil",(UnixFileMode)493);
            Directory.CreateDirectory(stage+"/licenses");File.SetUnixFileMode(stage+"/licenses",(UnixFileMode)493);
            foreach(var notice in Directory.EnumerateFiles(Path.Combine(Path.GetDirectoryName(source)!,"licenses")))
            {var target=Path.Combine(stage,"licenses",Path.GetFileName(notice));File.Copy(notice,target);File.SetUnixFileMode(target,(UnixFileMode)420);}
            // The bundle is private to root. Publish a root-owned executable for
            // the station user, with a persistent SELinux executable label.
            var label=await Processes.Run("semanage",["fcontext","-a","-t","bin_t","/var/lib/xur-virtual-display(/.*)?"],30);
            if(label.ExitCode!=0&& (await Processes.Run("semanage",["fcontext","-m","-t","bin_t","/var/lib/xur-virtual-display(/.*)?"],30)).ExitCode!=0)
                throw new InvalidOperationException("Could not label the virtual display utility.");
            if((await Processes.Run("restorecon",["-RF",stage],30)).ExitCode!=0)throw new InvalidOperationException("Could not restore virtual display labels.");
            Directory.Move(stage,directory);
        }finally{installation.Release();}
    }
    public static async Task<JsonElement> Run(Workload w,StationDisplayRequest? request)
    {
        var user=StationAccounts.Username(w);
        var uid=(await Processes.Run("id",["-u",user],5)).Output.Trim();
        if(!int.TryParse(uid,out var number)||number<1000||number>=65534)throw new InvalidOperationException("Invalid workstation user.");
        var session=await Processes.Run("runuser",["-u",user,"--","env","XDG_RUNTIME_DIR=/run/user/"+uid,"DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/"+uid+"/bus","systemctl","--user","show-environment"],5);
        var wayland=session.Output.Split('\n').FirstOrDefault(l=>l.StartsWith("WAYLAND_DISPLAY="))?[16..];
        if(wayland==null||!System.Text.RegularExpressions.Regex.IsMatch(wayland,@"^[a-zA-Z0-9_-]+$"))throw new InvalidOperationException("Workstation Wayland session is unavailable.");
        await Install();
        var args=request==null?new[]{"status"}:new[]{"resize",request.Width.ToString(CultureInfo.InvariantCulture),request.Height.ToString(CultureInfo.InvariantCulture),request.Fps.ToString(CultureInfo.InvariantCulture)};
        var result=await Processes.Run("runuser",["-u",user,"--","env","XDG_RUNTIME_DIR=/run/user/"+uid,"DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/"+uid+"/bus","WAYLAND_DISPLAY="+wayland,ExecutablePath,"display",..args],65);
        if(result.ExitCode!=0)throw new InvalidOperationException(Redaction.Logs(result.Output));
        return JsonSerializer.Deserialize<JsonElement>(result.Output);
    }
}
