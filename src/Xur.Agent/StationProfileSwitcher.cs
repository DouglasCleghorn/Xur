using Xur.Domain;
namespace Xur.Agent;

// Install read-only, versioned application files outside Xur's private state.
// Per-user launch/config files are always written as that workstation's user.
public static class StationProfileSwitcher
{
    static readonly object installation=new();
    public static async Task Prepare(string user,string home)
    {
        if(!ProfilePolicy.UserName(user))throw new InvalidOperationException("Invalid workstation user.");
        var source=Path.Combine(AppContext.BaseDirectory,"profile-switcher");
        if(!Directory.Exists(source))throw new InvalidOperationException("The application bundle is missing the profile switcher runtime.");
        string root;
        lock(installation)
        {
            root="/var/lib/xur-profile-switcher/"+ApplicationIdentity.Id;
            Directory.CreateDirectory(root);File.SetUnixFileMode("/var/lib/xur-profile-switcher",(UnixFileMode)493);File.SetUnixFileMode(root,(UnixFileMode)493);
            foreach(var file in Directory.GetFiles(source,"*",SearchOption.AllDirectories))
            {
                var destination=Path.Combine(root,Path.GetRelativePath(source,file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if(!File.Exists(destination)){File.Copy(file,destination+".tmp",true);File.SetUnixFileMode(destination+".tmp",(UnixFileMode)420);File.Move(destination+".tmp",destination,true);}
            }
            foreach(var directory in Directory.GetDirectories(root,"*",SearchOption.AllDirectories))File.SetUnixFileMode(directory,(UnixFileMode)493);
            File.SetUnixFileMode(root+"/xur-profile-switcher",(UnixFileMode)493);
            File.SetUnixFileMode(root+"/launch",(UnixFileMode)493);
        }
        var connection=System.Text.Json.JsonSerializer.Serialize(new{socket=ProfileSwitcherTransport.SocketPath(Environment.GetEnvironmentVariable("XUR_RUN")??"/run/xur")});
        await Check("runuser",["-u",user,"--","/bin/bash","-c",Configuration,"xur",home,root,connection]);
        var label=await Processes.Run("semanage",["fcontext","-a","-t","bin_t","/var/lib/xur-profile-switcher(/.*)?"],30);
        if(label.ExitCode!=0)await Check("semanage",["fcontext","-m","-t","bin_t","/var/lib/xur-profile-switcher(/.*)?"]);
        await Check("restorecon",["-RF","/var/lib/xur-profile-switcher"]);
    }
    static async Task Check(string command,string[] args)
    {
        var result=await Processes.Run(command,args,30);
        if(result.ExitCode!=0)throw new InvalidOperationException("Could not prepare the profile switcher: "+Redaction.Logs(result.Output));
    }
    internal const string Configuration="""
        set -eu
        umask 077
        mkdir -p "$1/.local/share/applications" "$1/.config/autostart" "$1/.config/xur-profile-switcher"
        printf '%s\n' "$3" > "$1/.config/xur-profile-switcher/connection.json"
        printf '[Desktop Entry]\nType=Application\nName=Xur profile switcher\nComment=Choose and review a saved Xur profile\nExec=%s/launch\nIcon=preferences-system\nTerminal=false\nCategories=System;\nStartupWMClass=dev.xur.ProfileSwitcher\n' "$2" > "$1/.local/share/applications/dev.xur.ProfileSwitcher.desktop"
        printf '[Desktop Entry]\nType=Application\nName=Xur profile switcher shortcuts\nExec=%s/launch --background\nOnlyShowIn=KDE;\nNoDisplay=true\n' "$2" > "$1/.config/autostart/dev.xur.ProfileSwitcher.desktop"
        """;
}
