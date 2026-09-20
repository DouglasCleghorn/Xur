using Xur.Domain;
namespace Xur.Agent;
public static class StationPower
{
    public static async Task Apply(string user,bool headless)
    {
        var uid=(await Processes.Run("id",["-u",user],5)).Output.Trim();
        if(!ProfilePolicy.UserName(user)||!int.TryParse(uid,out var number)||number<1000||number>=65534)throw new InvalidOperationException("Invalid workstation user.");
        var result=await Processes.Run("runuser",["-u",user,"--","env","XDG_RUNTIME_DIR=/run/user/"+uid,"DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/"+uid+"/bus","bash","-c",Configuration,"xur",headless?"headless":"local"],20);
        if(result.ExitCode!=0)throw new InvalidOperationException("Could not configure workstation idle behavior: "+Redaction.Logs(result.Output));
    }
    public const string Configuration="""
        set -eu
        for profile in AC Battery LowBattery; do
          kwriteconfig6 --file powerdevilrc --group "$profile" --group SuspendAndShutdown --key AutoSuspendAction --type uint --notify 0
          if [ "$1" = headless ]; then
            kwriteconfig6 --file powerdevilrc --group "$profile" --group Display --key DimDisplayWhenIdle --type bool --notify false
            kwriteconfig6 --file powerdevilrc --group "$profile" --group Display --key TurnOffDisplayWhenIdle --type bool --notify false
          else
            # A user may have previously run headless. Restore physical display
            # idle sleep without allowing the host to suspend.
            kwriteconfig6 --file powerdevilrc --group "$profile" --group Display --key DimDisplayWhenIdle --type bool --notify true
            kwriteconfig6 --file powerdevilrc --group "$profile" --group Display --key TurnOffDisplayWhenIdle --type bool --notify true
          fi
        done
        """;
}
