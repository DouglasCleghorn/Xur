using System.Text.Json;
using Xur.Agent;
using Xur.Domain;
static class EngineRestartPolicyTests
{
    public static async Task Run(Action<bool,string> check)
    {
        string current="no",identity="fixture",error="updating device access list not supported when using BPFProgram";
        bool save=true,replace=false;int updates=0;
        Task<ProcessResult> Run(string exe,string[] args,int timeout)
        {
            if(args[0]=="update")
            {
                updates++;if(save)current=args[1].Split('=')[1];if(replace)identity="replacement";
                return Task.FromResult(new ProcessResult(1,error));
            }
            return Task.FromResult(new ProcessResult(0,JsonSerializer.Serialize(new[]{new{Id=identity,HostConfig=new{RestartPolicy=new{Name=current}}}})));
        }
        var policy=new EngineRestartPolicy(Run);
        await policy.Set("xur-workload-1","fixture","no");
        check(updates==0,"Unchanged engine restart policy does not trigger a resource update");
        await policy.Set("xur-workload-1","fixture","unless-stopped");
        check(updates==1&&current=="unless-stopped","Known crun BPF error is accepted only after restart policy is saved");
        await policy.Set("xur-workload-1","fixture","no");
        check(current=="no","Startup retries can be suspended despite known crun resource error");
        save=false;
        async Task<bool> Rejected(){try{await policy.Set("xur-workload-1","fixture","unless-stopped");return false;}catch(InvalidOperationException){return true;}}
        check(await Rejected(),"BPF error with unsaved policy remains a failure");
        save=true;error="permission denied";
        check(await Rejected(),"Unrelated restart update errors are not suppressed even if metadata changed");
        current="no";error="updating device access list not supported when using BPFProgram";replace=true;
        check(await Rejected(),"Container replacement during policy update is rejected");
    }
}
