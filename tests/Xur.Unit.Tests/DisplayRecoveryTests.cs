using Xur.Agent;
using Xur.Domain;
static class DisplayRecoveryTests
{
    sealed class Clock:TimeProvider{public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../.build/evidence/display-recovery-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        try
        {
            var runtime=root+"/runtime";Directory.CreateDirectory(runtime);File.WriteAllText(runtime+"/kmscon","");File.WriteAllText(runtime+"/client","");
            var connector=root+"/sys/class/drm/card0-HDMI-A-1";Directory.CreateDirectory(connector);File.WriteAllText(connector+"/modes","");File.WriteAllText(connector+"/enabled","disabled");
            var gpu=new GpuDevice("0000:01:00.0","AMD","AMD test","amdgpu","",0,[],[],["/dev/dri/card0"],["card0-HDMI-A-1"]);
            var active=false;var starts=0;var stops=0;var environment="";var clock=new Clock();
            Task<ProcessResult> Run(string exe,string[] args,int timeout)
            {
                if(exe=="systemd-run"){starts++;active=true;environment=string.Join(' ',args.Where(a=>a.StartsWith("--setenv=")).Select(a=>a[9..]))+" ";return Task.FromResult(new ProcessResult(0,""));}
                if(args[0]=="stop"){active=false;stops++;}
                return Task.FromResult(args[0]=="is-active"?new ProcessResult(active?0:3,""):new ProcessResult(0,args.Contains("--property=Environment")?environment:""));
            }
            var console=new DisplayConsoles(root+"/workloads",root,Run,()=>Task.FromResult(new[]{gpu}),runtime,root+"/sys",clock);
            await console.Refresh();check(starts==1,"Display console starts when a monitor is connected before its modes settle");
            File.WriteAllText(connector+"/modes","1920x1080\n");await console.Refresh();check(starts==2&&stops==1,"Late HDMI modes restart the affected console even when the font size is unchanged");
            File.WriteAllText(connector+"/enabled","enabled");await console.Refresh();check(starts==2,"A healthy unchanged display does not restart");
            File.WriteAllBytes(connector+"/edid",[1,2,3]);await console.Refresh();check(starts==3,"A changed monitor identity refreshes the console even at the same resolution");
            File.WriteAllText(connector+"/enabled","disabled");await console.Refresh();check(starts==3,"Display recovery allows a modeset grace period");
            for(var i=0;i<4;i++){clock.Now=clock.Now.AddSeconds(31);await console.Refresh();}
            check(starts==6&&console.Error!=null,"An active console with inactive HDMI output gets at most three recovery attempts");
            File.WriteAllText(connector+"/enabled","enabled");await console.Refresh();check(console.Error==null&&starts==6,"Healthy HDMI output clears the recovery error without another restart");
            await console.Release(gpu.Pci);await console.Refresh();check(starts==6,"Display recovery never restarts a console on a GPU being handed to a workstation");
        }
        finally{Directory.Delete(root,true);}
    }
}
