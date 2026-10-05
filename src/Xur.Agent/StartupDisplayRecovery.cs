using Xur.Domain;
namespace Xur.Agent;

// A boot-time AMD modeset timeout can leave connected/enabled/DPMS On with no signal.
// Reprobe once on installed systems and installer media after the monitor has
// had time to settle; never loop on those flags.
public sealed class StartupDisplayRecovery(string runDirectory,TimeProvider? clock=null)
{
    readonly TimeProvider time=clock??TimeProvider.System;
    readonly Dictionary<string,(long Started,bool Needed)> observations=[];
    public string? LastAttempt {get;private set;}
    string Marker(string pci)=>Path.Combine(runDirectory,"display-startup-recovery-"+Canonical.Hash(pci)[..16]);
    public string Mode(GpuDevice gpu,string card,string sysRoot)
    {
        if(!File.Exists(Marker(gpu.Pci)))return "";
        var outputs=(gpu.Displays??[]).Where(d=>d.StartsWith(Path.GetFileName(card)+"-",StringComparison.Ordinal)).ToArray();
        try{return outputs.Length>0 && outputs.All(d=>File.ReadLines(Path.Combine(sysRoot,"class/drm",d,"modes")).Contains("1920x1080"))?"1920x1080":"";}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException){return "";}
    }
    public async Task<bool> Due(GpuDevice gpu,Func<string,string[],int,Task<ProcessResult>> run)
    {
        if(gpu.Driver!="amdgpu" || !(gpu.Displays??[]).Any(d=>d.Contains("-HDMI-A-",StringComparison.Ordinal)) || File.Exists(Marker(gpu.Pci)))return false;
        if(!observations.TryGetValue(gpu.Pci,out var state))
        {
            var journal=await run("journalctl",["--boot","--dmesg","--no-pager","--grep=amdgpu.*REG_WAIT timeout","-n","100"],5);
            // Match the affected PCI function as well as the display-engine failure.
            var needed=journal.ExitCode==0 && journal.Output.Split('\n').Any(line=>line.Contains("amdgpu "+gpu.Pci+":",StringComparison.Ordinal) && line.Contains("REG_WAIT timeout",StringComparison.Ordinal) && line.Contains("disable_crtc",StringComparison.Ordinal));
            observations[gpu.Pci]=(time.GetTimestamp(),needed);return false;
        }
        return state.Needed && time.GetElapsedTime(state.Started)>=TimeSpan.FromSeconds(15);
    }
    public void Consume(GpuDevice gpu)
    {
        Directory.CreateDirectory(runDirectory);
        using var file=new FileStream(Marker(gpu.Pci),new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite});
        LastAttempt="One-time HDMI reprobe with 1080p fallback when supported after AMD startup display timeout on "+gpu.Pci;
        using var writer=new StreamWriter(file);writer.WriteLine(LastAttempt);
        Console.Error.WriteLine(LastAttempt);
    }
    public static void Reprobe(string card,IEnumerable<string> displays,string sysRoot="/sys")
    {
        foreach(var name in displays.Where(d=>d.StartsWith(Path.GetFileName(card)+"-HDMI-A-",StringComparison.Ordinal) && Path.GetFileName(d)==d))
        {
            var status=Path.Combine(sysRoot,"class/drm",name,"status");
            if(File.ReadAllText(status).Trim()=="connected")File.WriteAllText(status,"detect\n");
        }
    }
}
