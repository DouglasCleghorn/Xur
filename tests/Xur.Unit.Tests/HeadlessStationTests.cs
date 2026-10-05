using Xur.Domain;
using System.Reflection;
using Xur.Agent;
static class HeadlessStationTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var home=Path.Combine(Path.GetTempPath(),"xur-headless-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(home);
        var script=(string)typeof(StationRuntime).GetField("UserConfiguration",BindingFlags.NonPublic|BindingFlags.Static)!.GetRawConstantValue()!;
        try
        {
            var result=await Processes.Run("bash",["-c",script,"xur",home,"/dev/dri/card7","headless","VK_LOADER_DRIVERS_SELECT=*nvidia*","seat-xur-test","TZ=America/Denver"],10);
            var directory=home+"/.config/systemd/user/plasma-kwin_wayland.service.d";
            var file=directory+"/90-xur-headless.conf";
            check(result.ExitCode==0 && File.ReadAllText(file).Contains("--drm") && !File.ReadAllText(file).Contains("--virtual"),"Headless desktop retains KWin DRM/libinput instead of the inputless virtual backend");
            check(File.ReadAllText(home+"/.config/environment.d/90-xur-gpu.conf").Contains("KWIN_DRM_DEVICES=/dev/dri/card7"),"Headless desktop stays on its assigned GPU");
            check(File.ReadAllLines(home+"/.config/environment.d/90-xur-gpu.conf").Contains("TZ=America/Denver"),"Headless desktop publishes a named timezone for Steam and other ICU apps");
            File.WriteAllText(directory+"/user.conf","# unrelated settings");
            result=await Processes.Run("bash",["-c",script,"xur",home,"/dev/dri/card7","local","","seat-xur-test","TZ=Asia/Kolkata"],10);
            check(result.ExitCode==0 && !File.Exists(file) && File.Exists(directory+"/user.conf"),"Returning to local display removes only Xur's headless override");
            check(File.ReadAllLines(home+"/.config/environment.d/90-xur-gpu.conf").Contains("TZ=Asia/Kolkata")&&!File.ReadAllText(home+"/.config/environment.d/90-xur-gpu.conf").Contains("America/Denver"),"Reload replaces the desktop's previous timezone with the current selection");
        }
        finally {Directory.Delete(home,true);}
    }
}
