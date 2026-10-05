namespace Xur.Domain;

public static class ProfileSwitcherTransport
{
    public const string DefaultSocket="/run/xur-profile-switcher/switcher.sock";
    public static string SocketPath(string runDirectory)=>runDirectory=="/run/xur"?DefaultSocket:Path.Combine(runDirectory,"profile-switcher","switcher.sock");
}

public record ProfileSwitcherSession(int Uid,string User,string WorkloadId,string Seat);
public record ProfileSwitchOrigin(string Trigger,string User,int? Uid=null,string? Workstation=null,string? Seat=null)
{
    public static bool ValidTrigger(string? trigger)=>trigger is "keyboard" or "controller" or "plasma-menu" or "web-menu" or "web-keyboard" or "web-controller" or "web-route" or "console-menu" or "console-keyboard" or "console-controller" or "terminal" or "api" or "web-form" or "internal";
}
public record ProfileSwitchEvent(DateTimeOffset Time,string Operation,string Action,string ProfileId,string ProfileName,string Result,ProfileSwitchOrigin Origin);
