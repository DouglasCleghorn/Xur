using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

// Independent 2D consoles on unassigned cards. Only the selected card's console
// is released for a desktop; compute allocations retain their display console.
public sealed class DisplayConsoles(string directory,string runDirectory)
{
    readonly SemaphoreSlim gate=new(1,1);
    readonly HashSet<string> handingOff=[];
    public string? Error {get;private set;}
    static string Unit(string pci)=>"xur-console-"+Canonical.Hash(pci)[..16]+".service";
    static string RuntimePath=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"console"));
    async Task<HashSet<string>> DesktopCards()
    {
        var result=new HashSet<string>(handingOff);
        if(Directory.Exists(directory))foreach(var p in Directory.GetFiles(directory,"*.json"))
        {
            Workload? w;try{w=JsonSerializer.Deserialize<Workload>(await File.ReadAllTextAsync(p));}catch{continue;}
            if(w?.Recipe.Kind!="Workstation")continue;
            var state=await Processes.Run("systemctl",["show","xur-station-"+w.Id+".service","--property=ActiveState","--value"],5);
            if(state.ExitCode==0 && state.Output.Trim() is "active" or "activating" or "deactivating")foreach(var pci in w.Gpus)result.Add(pci);
        }
        return result;
    }
    public async Task Release(string pci)
    {
        await gate.WaitAsync();try
        {
            handingOff.Add(pci);
            var r=await Processes.Run("systemctl",["stop",Unit(pci)],20);
            var state=await Processes.Run("systemctl",["show",Unit(pci),"--property=ActiveState","--value"],5);
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
            var root=RuntimePath;
            if(!File.Exists(root+"/kmscon")||!File.Exists(root+"/client")){Error="Display console runtime is missing.";return;}
            var gpus=await GpuInventory.Observe(probeRuntime:false);var desktops=await DesktopCards();
            var wanted=gpus.Where(g=>!desktops.Contains(g.Pci)&&(g.Cards?.Length??0)>0&&(g.Displays?.Length??0)>0).ToArray();
            var units=await Processes.Run("systemctl",["list-units","--all","--plain","--no-legend","xur-console-*.service"],5);
            foreach(var line in units.Output.Split('\n',StringSplitOptions.RemoveEmptyEntries))
            {
                var unit=line.Split(' ',StringSplitOptions.RemoveEmptyEntries)[0];
                if(unit.StartsWith("xur-console-")&&!wanted.Any(g=>Unit(g.Pci)==unit))await Processes.Run("systemctl",["stop",unit],20);
            }
            foreach(var gpu in wanted)
            {
                var card=gpu.Cards![0];
                if((await Processes.Run("systemctl",["is-active",Unit(gpu.Pci)],5)).ExitCode==0)
                {
                    var environment=await Processes.Run("systemctl",["show",Unit(gpu.Pci),"--property=Environment","--value"],5);
                    if(environment.Output.Contains("XUR_CONSOLE_BUNDLE="+ApplicationIdentity.Id)&&environment.Output.Contains("XUR_CONSOLE_CARD="+card+" "))continue;
                    await Processes.Run("systemctl",["stop",Unit(gpu.Pci)],20);
                }
                await Processes.Run("systemctl",["reset-failed",Unit(gpu.Pci)],5);
                var r=await Processes.Run("systemd-run",[
                    "--unit="+Unit(gpu.Pci),"--collect","--property=Type=exec","--property=KillMode=control-group",
                    "--property=TimeoutStopSec=10","--property=StandardOutput=null","--property=StandardError=journal",
                    "--property=DevicePolicy=closed","--property=DeviceAllow="+card+" rw","--property=DeviceAllow=char-pts rw",
                    "--property=UMask=0077","--setenv=LANG=C.UTF-8","--setenv=LD_LIBRARY_PATH="+root+"/lib",
                    "--setenv=XUR_CONSOLE_BUNDLE="+ApplicationIdentity.Id,"--setenv=XUR_CONSOLE_CARD="+card,"--setenv=XUR_CONSOLE_MODULES="+root+"/lib",
                    root+"/kmscon","--vt=/dev/null","--no-libseat","--no-hwaccel","--font-engine=unifont","--font-size=16",
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
