using System.Text.Json;
using System.Globalization;
using Xur.Domain;
namespace Xur.Agent;
public static class StationDisplay
{
    public static string ExecutablePath=>StationUtility.ExecutablePath;
    public static Task Install()=>StationUtility.Install();
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
