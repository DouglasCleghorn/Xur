using Xur.Agent;
using Xur.Domain;
static class DisplayRecoveryTests
{
    sealed class Clock:TimeProvider{public DateTimeOffset Now=DateTimeOffset.UtcNow;public long Tick;public override DateTimeOffset GetUtcNow()=>Now;public override long GetTimestamp()=>Tick;public override long TimestampFrequency=>TimeSpan.TicksPerSecond;}
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../.build/evidence/display-recovery-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        try
        {
            var runtime=root+"/runtime";Directory.CreateDirectory(runtime);File.WriteAllText(runtime+"/kmscon","");File.WriteAllText(runtime+"/client","");
            var connector=root+"/sys/class/drm/card0-HDMI-A-1";Directory.CreateDirectory(connector);File.WriteAllText(connector+"/modes","");File.WriteAllText(connector+"/enabled","disabled");
            var gpu=new GpuDevice("0000:01:00.0","AMD","AMD test","amdgpu","",0,[],[],["/dev/dri/card0"],["card0-HDMI-A-1"]);
            var active=false;var starts=0;var stops=0;var environment="";var clock=new Clock();string kernel="";int journalReads=0;
            Task<ProcessResult> Run(string exe,string[] args,int timeout)
            {
                if(exe=="journalctl"){journalReads++;return Task.FromResult(new ProcessResult(0,kernel));}
                if(exe=="systemd-run"){check(args.Contains("--no-use-original-mode"),"Display startup selects the monitor mode instead of inheriting firmware timing");starts++;active=true;environment=string.Join(' ',args.Where(a=>a.StartsWith("--setenv=")).Select(a=>a[9..]))+" ";return Task.FromResult(new ProcessResult(0,""));}
                if(args[0]=="stop"){active=false;stops++;}
                return Task.FromResult(args[0]=="is-active"?new ProcessResult(active?0:3,""):new ProcessResult(0,args.Contains("--property=Environment")?environment:""));
            }
            var console=new DisplayConsoles(root+"/workloads",root,Run,()=>Task.FromResult(new[]{gpu}),runtime,root+"/sys",clock);
            await console.Refresh();check(starts==1,"Display console starts when a monitor is connected before its modes settle");
            File.WriteAllText(connector+"/modes","1920x1080\n");await console.Refresh();check(starts==2&&stops==1,"Late HDMI modes restart the affected console even when the font size is unchanged");
            File.WriteAllText(connector+"/enabled","enabled");await console.Refresh();check(starts==2,"A healthy unchanged display does not restart");
            File.WriteAllBytes(connector+"/edid",[1,2,3]);await console.Refresh();check(starts==3,"A changed monitor identity refreshes the console even at the same resolution");
            File.WriteAllText(connector+"/enabled","disabled");await console.Refresh();check(starts==3,"Display recovery allows a modeset grace period");
            for(var i=0;i<4;i++){clock.Tick+=TimeSpan.FromSeconds(31).Ticks;await console.Refresh();}
            check(starts==6&&console.Error!=null,"An active console with inactive HDMI output gets at most three recovery attempts");
            check(environment.Contains("XUR_CONSOLE_NO_DPMS=1"),"A recovered GPU retains its video signal during future idle cycles");
            File.WriteAllText(connector+"/enabled","enabled");await console.Refresh();check(console.Error==null&&starts==6,"Healthy HDMI output clears the recovery error without another restart");
            clock.Tick+=TimeSpan.FromSeconds(61).Ticks;await console.Refresh();
            var second=root+"/sys/class/drm/card0-HDMI-A-2";Directory.CreateDirectory(second);File.WriteAllText(second+"/enabled","disabled");File.WriteAllText(second+"/modes","1920x1080\n");
            gpu=gpu with{Displays=["card0-HDMI-A-1","card0-HDMI-A-2"]};await console.Refresh();var before=starts;
            clock.Tick+=TimeSpan.FromSeconds(16).Ticks;await console.Refresh();check(starts==before+1,"One healthy connector does not prevent recovery of another inactive output");
            File.WriteAllText(second+"/enabled","enabled");await console.Refresh();clock.Tick+=TimeSpan.FromSeconds(61).Ticks;await console.Refresh();before=starts;
            File.WriteAllText(connector+"/dpms","Off");await console.Refresh();clock.Tick+=TimeSpan.FromSeconds(16).Ticks;await console.Refresh();check(starts==before+1,"An enabled connector left in DPMS Off gets bounded recovery");
            before=starts;File.WriteAllText(root+"/console-sleep","");await console.Refresh();clock.Tick+=TimeSpan.FromSeconds(120).Ticks;await console.Refresh();
            check(starts==before&&console.Error==null,"Deliberate monitor sleep is excluded from display recovery even after its grace period");
            File.Delete(root+"/console-sleep");await console.Refresh();check(starts==before,"Keyboard wake gives the monitor a fresh recovery grace period");
            clock.Tick+=TimeSpan.FromSeconds(16).Ticks;await console.Refresh();check(starts==before+1,"A display that fails to wake still receives bounded recovery");
            before=starts;await console.Release(gpu.Pci);await console.Refresh();check(starts==before,"Display recovery never restarts a console on a GPU being handed to a workstation");
            gpu=gpu with{Displays=["card0-HDMI-A-1"]};File.WriteAllText(connector+"/status","connected");File.WriteAllText(connector+"/enabled","enabled");File.WriteAllText(connector+"/dpms","On");
            var faultRun=root+"/fault";Directory.CreateDirectory(faultRun);
            var faults=new DisplayConsoles(root+"/workloads",faultRun,Run,()=>Task.FromResult(new[]{gpu}),runtime,root+"/sys",clock);
            await faults.Refresh();await faults.Refresh();before=starts;
            var fault=Path.Combine(faultRun,"console-fault-"+Canonical.Hash(gpu.Pci)[..16]);
            File.WriteAllText(fault,"Display frame updates stalled.");await faults.Refresh();
            clock.Now=clock.Now.AddYears(-2);clock.Tick+=TimeSpan.FromSeconds(16).Ticks;await faults.Refresh();
            check(starts==before+1&&!File.Exists(fault)&&faults.LastRecovery!=null,"Renderer stalls recover despite enabled/DPMS On and a backwards wall-clock correction");
            check(environment.Contains("XUR_CONSOLE_NO_DPMS=1"),"A renderer stall switches subsequent idle to black with its signal retained");
            for(var i=0;i<3;i++)
            {
                await faults.Refresh(); // Briefly healthy immediately after restarting.
                File.WriteAllText(fault,"Display frame updates stalled.");
                clock.Tick+=TimeSpan.FromSeconds(31).Ticks;await faults.Refresh();
            }
            check(starts==before+3&&faults.Error!=null,"Briefly healthy flags between renderer stalls cannot create an unlimited restart loop");
            File.Delete(fault);await faults.Refresh();clock.Tick+=TimeSpan.FromSeconds(61).Ticks;await faults.Refresh();
            check(faults.Error==null,"Sustained healthy rendering clears the stall recovery error");
            var restartedFaults=new DisplayConsoles(root+"/workloads",faultRun,Run,()=>Task.FromResult(new[]{gpu}),runtime,root+"/sys",clock);
            before=starts;await restartedFaults.Refresh();
            check(starts==before&&environment.Contains("XUR_CONSOLE_NO_DPMS=1"),"Safe idle behavior survives an agent restart without restarting a healthy console");
            kernel="amdgpu 0000:01:00.0: [drm] REG_WAIT timeout 1us * 100000 tries - optc314_disable_crtc line:146";
            var bootRun=root+"/boot";
            var installer=new DisplayConsoles(root+"/workloads",bootRun,Run,()=>Task.FromResult(new[]{gpu}),runtime,root+"/sys",clock);
            await installer.Refresh();await installer.Refresh();before=starts;
            clock.Now=clock.Now.AddYears(-2);clock.Tick+=TimeSpan.FromSeconds(16).Ticks;
            await installer.Refresh();
            check(starts==before+1&&File.ReadAllText(connector+"/status").Trim()=="detect"&&installer.LastRecovery!=null,"AMD timeout triggers HDMI reprobe despite enabled/DPMS On, using monotonic time across clock correction");
            check(environment.Contains("XUR_CONSOLE_MODE=1920x1080 ")&&environment.Contains("XUR_CONSOLE_FONT=16 "),"AMD recovery selects supported 1080p with a font that fits the fallback mode");
            before=starts;clock.Tick+=TimeSpan.FromSeconds(60).Ticks;await installer.Refresh();
            var restarted=new DisplayConsoles(root+"/workloads",bootRun,Run,()=>Task.FromResult(new[]{gpu}),runtime,root+"/sys",clock);
            await restarted.Refresh();clock.Tick+=TimeSpan.FromSeconds(60).Ticks;await restarted.Refresh();
            check(starts==before,"Startup HDMI recovery runs once even across agent restarts");
            check(environment.Contains("XUR_CONSOLE_MODE=1920x1080 "),"The conservative mode survives agent restarts within the same boot");
            var fallback=new StartupDisplayRecovery(bootRun,clock);
            File.WriteAllText(connector+"/modes","1280x720\n");
            check(fallback.Mode(gpu,"/dev/dri/card0",root+"/sys")=="","A monitor without 1080p support keeps its preferred mode");
            File.WriteAllText(connector+"/modes","1920x1080\n");
            var healthy=new StartupDisplayRecovery(root+"/healthy",clock);
            kernel=kernel.Replace(gpu.Pci,"0000:02:00.0");await healthy.Due(gpu,Run);clock.Tick+=TimeSpan.FromSeconds(16).Ticks;
            check(!await healthy.Due(gpu,Run),"A different GPU's kernel timeout cannot trigger HDMI recovery");
            kernel="";var noTimeout=new StartupDisplayRecovery(root+"/no-timeout",clock);await noTimeout.Due(gpu,Run);clock.Tick+=TimeSpan.FromSeconds(16).Ticks;
            check(!await noTimeout.Due(gpu,Run),"Healthy boots do not get speculative HDMI resets");
            var reads=journalReads;
            var installed=new DisplayConsoles(root+"/workloads",root+"/installed",Run,()=>Task.FromResult(new[]{gpu}),runtime,root+"/sys",clock);
            await installed.Refresh();clock.Tick+=TimeSpan.FromSeconds(16).Ticks;await installed.Refresh();
            check(journalReads==reads+1,"Installed systems also inspect the affected GPU for the startup timeout");
            reads=journalReads;
            var handoff=new DisplayConsoles(root+"/workloads",root+"/handoff",Run,()=>Task.FromResult(new[]{gpu}),runtime,root+"/sys",clock);
            before=starts;await handoff.Release(gpu.Pci);await handoff.Refresh();
            check(starts==before&&journalReads==reads,"Startup HDMI recovery respects workstation GPU handoff");
        }
        finally{Directory.Delete(root,true);}
    }
}
