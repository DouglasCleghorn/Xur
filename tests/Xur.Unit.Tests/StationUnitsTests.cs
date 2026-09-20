using Xur.Agent;
using Xur.Domain;

static class StationUnitsTests
{
    public static async Task Run(Action<bool,string> check)
    {
        const string unit="xur-station-10.service";
        var mutations=0;
        foreach(var state in new[]{"active","activating","deactivating","reloading"})
        {
            var helper=new StationUnits((exe,args,seconds)=>{
                if(args[0]!="show")mutations++;
                return Task.FromResult(new ProcessResult(0,"LoadState=loaded\nActiveState="+state+"\nTransient=yes\n"));
            });
            var refused=false;try{await helper.Retire(unit);}catch(InvalidOperationException){refused=true;}
            check(refused&&mutations==0,"Unit replacement preserves existing desktop state: "+state);
        }
        var foreign=new StationUnits((exe,args,seconds)=>{
            if(args[0]!="show")mutations++;
            return Task.FromResult(new ProcessResult(0,"LoadState=loaded\nActiveState=inactive\nTransient=no\nFragmentPath=/etc/systemd/system/"+unit));
        });
        var rejected=false;try{await foreign.Retire(unit,true);}catch(InvalidOperationException){rejected=true;}
        check(rejected&&mutations==0,"Unit cleanup preserves an administrator's non-transient fragment");
        var operations=new List<string>();var cleared=false;
        var failed=new StationUnits((exe,args,seconds)=>{
            operations.Add(args[0]);if(args[0]=="reset-failed")cleared=true;
            return Task.FromResult(new ProcessResult(0,cleared?"LoadState=not-found\n":"LoadState=loaded\nActiveState=failed\nTransient=yes\n"));
        });
        await failed.Retire(unit);
        check(operations.SequenceEqual(new[]{"show","stop","reset-failed","show"}),"Failed unit is stopped, reset and confirmed unloaded before recreation");
    }

    public static async Task Smoke()
    {
        // Real systemd regression, scoped to this user's throwaway units.
        var suffix=Guid.NewGuid().ToString("N");
        var parent="xur-unit-probe-parent-"+suffix+".service";
        var child="xur-unit-probe-child-"+suffix+".service";
        async Task<ProcessResult> Run(string exe,string[] args,int seconds)=>await Processes.Run(exe,["--user",..args],seconds);
        async Task Check(string exe,string[] args)
        {var r=await Run(exe,args,20);if(r.ExitCode!=0)throw new Exception(r.Output);}
        var helper=new StationUnits(Run);
        try
        {
            for(var cycle=0;cycle<2;cycle++)
            {
                await Check("systemd-run",["--unit="+parent,"--property=Type=exec","/usr/bin/false"]);
                await Check("systemd-run",["--unit="+child,"--property=Type=exec","--property=PartOf="+parent,"/usr/bin/false"]);
                for(var n=0;n<50;n++)
                {
                    var status=await Run("systemctl",["show",child,"--property=ActiveState","--value"],5);
                    if(status.Output.Trim()=="failed")break;
                    if(n==49)throw new Exception("Probe service did not fail as expected");
                    await Task.Delay(100);
                }
                var duplicate=await Run("systemd-run",["--unit="+parent,"--property=Type=exec","/usr/bin/false"],10);
                if(duplicate.ExitCode==0||!duplicate.Output.Contains("already loaded"))throw new Exception("Expected the reported transient-unit collision: "+duplicate.Output);
                await helper.Retire(child,true);
                await helper.Retire(parent);
            }
            Console.WriteLine("Passed: real systemd failed child/PartOf cleanup and same-name desktop recreation, two cycles");
        }
        finally
        {
            await Run("systemctl",["stop",child,parent],20);
            await Run("systemctl",["reset-failed",child,parent],10);
        }
    }
}
