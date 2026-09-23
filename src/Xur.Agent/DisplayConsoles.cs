using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

// Independent 2D consoles on unassigned cards. Only the selected card's console
// is released for a desktop; compute allocations retain their display console.
public sealed class DisplayConsoles(string directory,string runDirectory,
    Func<string,string[],int,Task<ProcessResult>>? runner=null,Func<Task<GpuDevice[]>>? observer=null,
    string? runtimePath=null,string sysRoot="/sys",TimeProvider? clock=null)
{
    readonly SemaphoreSlim gate=new(1,1);
    readonly HashSet<string> handingOff=[];
    public string? Error {get;private set;}
    static string Unit(string pci)=>"xur-console-"+Canonical.Hash(pci)[..16]+".service";
    static string RuntimePath=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"console"));
    readonly Dictionary<string,(DateTimeOffset Next,int Attempts)> recovery=[];
    Task<ProcessResult> RunProcess(string exe,string[] args,int seconds)=>runner?.Invoke(exe,args,seconds)??Processes.Run(exe,args,seconds);
    static string Read(string file){try{return File.ReadAllText(file).Trim();}catch(IOException){return "";}catch(UnauthorizedAccessException){return "";}}
    public static string OutputSignature(string card,IEnumerable<string> displays,string sysRoot="/sys")=>Canonical.Hash(
        displays.Where(d=>d.StartsWith(Path.GetFileName(card)+"-",StringComparison.Ordinal)).Order().Select(d=>new{
            Connector=d,Modes=Read(Path.Combine(sysRoot,"class/drm",d,"modes")),
            Edid=ReadEdid(Path.Combine(sysRoot,"class/drm",d,"edid"))}).ToArray());
    static string ReadEdid(string file){try{return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));}catch(IOException){return "";}catch(UnauthorizedAccessException){return "";}}
    bool NeedsRecovery(GpuDevice gpu,string card)
    {
        var now=(clock??TimeProvider.System).GetUtcNow();
        var outputs=(gpu.Displays??[]).Where(d=>d.StartsWith(Path.GetFileName(card)+"-",StringComparison.Ordinal)).ToArray();
        if(outputs.Length==0 || !outputs.Any(d=>Read(Path.Combine(sysRoot,"class/drm",d,"enabled"))=="disabled" || Read(Path.Combine(sysRoot,"class/drm",d,"dpms")) is "Off" or "Standby" or "Suspend"))
        {recovery.Remove(gpu.Pci);return false;}
        if(!recovery.TryGetValue(gpu.Pci,out var state)){recovery[gpu.Pci]=(now.AddSeconds(15),0);return false;}
        if(state.Attempts>=3){Error="A connected display is still inactive after console recovery. Check display diagnostics.";return false;}
        if(now<state.Next)return false;
        recovery[gpu.Pci]=(now.AddSeconds(30),state.Attempts+1);return true;
    }
    // Unifont scales in whole 16px steps. Retain at least 120 columns and 45 rows
    // on larger displays; cloned outputs share the smallest connected screen.
    public static int FontSize(IEnumerable<string> modes)
    {
        var sizes=modes.Select(mode=>System.Text.RegularExpressions.Regex.Match(mode,@"^(\d+)x(\d+)$"))
            .Select(m=>m.Success && int.TryParse(m.Groups[1].Value,out var w) && int.TryParse(m.Groups[2].Value,out var h)?16*Math.Clamp(Math.Min(w/960,h/720),1,8):16).ToArray();
        return sizes.Length==0?16:sizes.Min();
    }
    public static int DisplayFontSize(string card,IEnumerable<string> displays,string sysRoot="/sys")
    {
        var modes=new List<string>();
        foreach(var display in displays.Where(d=>d.StartsWith(Path.GetFileName(card)+"-",StringComparison.Ordinal)))
        {
            try{modes.Add(File.ReadLines(Path.Combine(sysRoot,"class/drm",display,"modes")).FirstOrDefault()??"");}
            catch(IOException){modes.Add("");}catch(UnauthorizedAccessException){modes.Add("");}
        }
        return FontSize(modes);
    }
    async Task<HashSet<string>> DesktopCards()
    {
        var result=new HashSet<string>(handingOff);
        if(Directory.Exists(directory))foreach(var p in Directory.GetFiles(directory,"*.json"))
        {
            Workload? w;try{w=JsonSerializer.Deserialize<Workload>(await File.ReadAllTextAsync(p));}catch{continue;}
            if(w?.Recipe.Kind!="Workstation")continue;
            var state=await RunProcess("systemctl",["show","xur-station-"+w.Id+".service","--property=ActiveState","--value"],5);
            if(state.ExitCode==0 && state.Output.Trim() is "active" or "activating" or "deactivating")foreach(var pci in w.Gpus)result.Add(pci);
        }
        return result;
    }
    public async Task Release(string pci)
    {
        await gate.WaitAsync();try
        {
            handingOff.Add(pci);
            var r=await RunProcess("systemctl",["stop",Unit(pci)],20);
            var state=await RunProcess("systemctl",["show",Unit(pci),"--property=ActiveState","--value"],5);
            if(state.ExitCode==0 && state.Output.Trim() is not ("inactive" or "failed" or ""))throw new InvalidOperationException("The display console has not released the selected GPU.");
        }finally{gate.Release();}
    }
    public async Task FinishHandoff(string pci)
    {await gate.WaitAsync();try{handingOff.Remove(pci);}finally{gate.Release();}await Refresh();}
    public async Task Refresh()
    {
        await gate.WaitAsync();try
        {
            Error=null;
            var root=runtimePath??RuntimePath;
            if(!File.Exists(root+"/kmscon")||!File.Exists(root+"/client")){Error="Display console runtime is missing.";return;}
            var gpus=observer!=null?await observer():await GpuInventory.Observe(probeRuntime:false);var desktops=await DesktopCards();
            var wanted=gpus.Where(g=>!desktops.Contains(g.Pci)&&(g.Cards?.Length??0)>0&&(g.Displays?.Length??0)>0).ToArray();
            var units=await RunProcess("systemctl",["list-units","--all","--plain","--no-legend","xur-console-*.service"],5);
            foreach(var line in units.Output.Split('\n',StringSplitOptions.RemoveEmptyEntries))
            {
                var unit=line.Split(' ',StringSplitOptions.RemoveEmptyEntries)[0];
                if(unit.StartsWith("xur-console-")&&!wanted.Any(g=>Unit(g.Pci)==unit))await RunProcess("systemctl",["stop",unit],20);
            }
            foreach(var gpu in wanted)
            {
                var card=gpu.Cards!.FirstOrDefault(c=>(gpu.Displays??[]).Any(d=>d.StartsWith(Path.GetFileName(c)+"-",StringComparison.Ordinal)))??gpu.Cards[0];
                var fontSize=DisplayFontSize(card,gpu.Displays??[],sysRoot);var outputs=OutputSignature(card,gpu.Displays??[],sysRoot);
                if((await RunProcess("systemctl",["is-active",Unit(gpu.Pci)],5)).ExitCode==0)
                {
                    var environment=await RunProcess("systemctl",["show",Unit(gpu.Pci),"--property=Environment","--value"],5);
                    var recover=NeedsRecovery(gpu,card);
                    if(!recover&&environment.Output.Contains("XUR_CONSOLE_BUNDLE="+ApplicationIdentity.Id)&&environment.Output.Contains("XUR_CONSOLE_CARD="+card+" ")&&environment.Output.Contains("XUR_CONSOLE_FONT="+fontSize+" ")&&environment.Output.Contains("XUR_CONSOLE_OUTPUTS="+outputs+" "))continue;
                    await RunProcess("systemctl",["stop",Unit(gpu.Pci)],20);
                }
                await RunProcess("systemctl",["reset-failed",Unit(gpu.Pci)],5);
                var r=await RunProcess("systemd-run",[
                    "--unit="+Unit(gpu.Pci),"--collect","--property=Type=exec","--property=KillMode=control-group",
                    "--property=TimeoutStopSec=10","--property=StandardOutput=null","--property=StandardError=journal",
                    "--property=DevicePolicy=closed","--property=DeviceAllow="+card+" rw","--property=DeviceAllow=char-pts rw",
                    "--property=UMask=0077","--setenv=LANG=C.UTF-8","--setenv=LD_LIBRARY_PATH="+root+"/lib",
                    "--setenv=XUR_CONSOLE_OUTPUTS="+outputs,"--setenv=XUR_CONSOLE_FONT="+fontSize,"--setenv=XUR_CONSOLE_BUNDLE="+ApplicationIdentity.Id,"--setenv=XUR_CONSOLE_CARD="+card,"--setenv=XUR_CONSOLE_MODULES="+root+"/lib",
                    // Select the monitor's preferred mode instead of inheriting a stale or absent firmware mode.
                    root+"/kmscon","--no-use-original-mode","--vt=/dev/null","--no-libseat","--no-hwaccel","--font-engine=unifont","--font-size="+fontSize,
                    "--no-mouse","--no-blink","--dpms-timeout=0","--multi-monitor=clone","--session-max=1","--no-session-control","--no-issue",
                    "--login","--",root+"/client",Path.Combine(runDirectory,"control.sock")],15);
                if(r.ExitCode!=0)Error="Display console start failed: "+r.Output.Trim();
            }
        }finally{gate.Release();}
    }
    public async Task Run(CancellationToken stop)
    {
        while(!stop.IsCancellationRequested)
        {
            try{await Refresh();}catch(Exception e) when(e is IOException or InvalidOperationException or UnauthorizedAccessException){Error="Display console observation failed.";}
            try{await Task.Delay(3000,stop);}catch(OperationCanceledException){break;}
        }
    }
}
