using Xur.Agent;
using Xur.Domain;
static class StationNetworkPolicyTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"xur-polkit-"+Guid.NewGuid().ToString("N"));
        try
        {
            var policy=new StationNetworkPolicy(root);policy.Apply("test","xurtest");
            var rule=File.ReadAllText(Directory.GetFiles(root,"*.rules").Single());
            var script="const rules=[];const polkit={Result:{NO:'no'},addRule:r=>rules.push(r)};"+rule+
                "const r=rules[0];console.log(JSON.stringify([r({id:'org.freedesktop.NetworkManager.network-control'},{user:'xurtest'}),r({id:'org.freedesktop.NetworkManager.network-control'},{user:'other'}),r({id:'org.freedesktop.login1.reboot'},{user:'xurtest'})]));";
            var result=await Processes.Run("node",["-e",script],10);
            check(result.ExitCode==0 && result.Output.Trim()=="[\"no\",null,null]","Network policy declines only the managed user's host-network action without requesting authentication");
            Directory.CreateDirectory(".build/fast");File.WriteAllText(".build/fast/station-network-test.rules",rule);
            var denied=false;try{policy.Apply("test","user\"; malicious()");}catch(InvalidOperationException){denied=true;}
            check(denied,"Network policy rejects invalid Unix account names");
            policy.Remove("test");check(!Directory.GetFiles(root).Any(),"Stopping a workstation removes its network policy");
        }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
