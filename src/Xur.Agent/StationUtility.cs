using Xur.Domain;
namespace Xur.Agent;

// Shared versioned helper for station-user operations; the application bundle is root-private.
public static class StationUtility
{
    public static string SourcePath=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../host/xurutil"));
    public static string ExecutablePath=>"/var/lib/xur-virtual-display/"+ApplicationIdentity.Id+"/utility/xurutil";
    static readonly SemaphoreSlim installation=new(1,1);
    public static async Task Install()
    {
        await installation.WaitAsync();try
        {
            var directory=Path.GetDirectoryName(ExecutablePath)!;
            if(Directory.Exists(directory))return;
            var source=SourcePath;
            if(!File.Exists(source))throw new InvalidOperationException("The station utility is missing from the application bundle.");
            var parent=Path.GetDirectoryName(directory)!;
            Directory.CreateDirectory(parent);
            File.SetUnixFileMode("/var/lib/xur-virtual-display",(UnixFileMode)493);
            File.SetUnixFileMode(parent,(UnixFileMode)493);
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
                throw new InvalidOperationException("Could not label the station utility.");
            if((await Processes.Run("restorecon",["-RF",stage],30)).ExitCode!=0)throw new InvalidOperationException("Could not restore station utility labels.");
            Directory.Move(stage,directory);
        }finally{installation.Release();}
    }
}
