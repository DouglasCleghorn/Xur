using Xur.Agent;
using Xur.Domain;

public static class StationRevokeFaultTests
{
    public static async Task Run(Action<bool,string> check)
    {
        foreach(var failure in new[]{"permission","io","timeout","missing","missing-parent","not-directory","loop","replaced"})
        {
            var root=Path.Combine(Path.GetTempPath(),"xur-revoke-fault-"+Guid.NewGuid());Directory.CreateDirectory(root);
            try
            {
                var boot=Path.Combine(root,"boot");File.WriteAllText(boot,"fixture-boot");
                var parent=Path.Combine(root,"devices");Directory.CreateDirectory(parent);
                var device=Path.Combine(parent,"card1");File.WriteAllText(device,"");
                bool revoking=false,retry=false;int restores=0;
                Task<ProcessResult> Run(string exe,string[] args,int seconds)
                {
                    if(exe is not ("stat" or "getfacl" or "setfacl"))throw new Exception("Unexpected command "+exe);
                    if(revoking && !retry && exe=="stat")
                    {
                        if(failure=="timeout")throw new OperationCanceledException("Injected stat timeout");
                        if(failure=="replaced")return Task.FromResult(new ProcessResult(0,"e2:1:999\n"));
                        return Task.FromResult(new ProcessResult(1,"Injected "+failure+" stat failure token=private-fixture-secret"));
                    }
                    if(revoking && exe=="setfacl")restores++;
                    return Task.FromResult(new ProcessResult(0,exe=="stat"?"e2:1:123\n":exe=="getfacl"?
                        "user::rw-\nuser:1001:rw-\ngroup::---\nmask::rw-\nother::---\n":""));
                }
                var grants=Path.Combine(root,"grants");var receipt=Path.Combine(grants,"station.json");
                var access=new StationDeviceAccess(grants,Run,boot);
                await access.GrantNodes("station",1001,[device]);var original=File.ReadAllText(receipt);
                if(failure is "missing" or "missing-parent" or "not-directory" or "loop")File.Delete(device);
                if(failure is "missing-parent" or "not-directory")Directory.Delete(parent);
                if(failure=="not-directory")File.WriteAllText(parent,"parent replaced with a file");
                if(failure=="loop")File.CreateSymbolicLink(device,device);
                revoking=true;Exception? error=null;try{await access.Revoke("station");}catch(Exception e){error=e;}
                if(failure is "missing" or "missing-parent" or "not-directory" or "replaced")
                {
                    check(error==null && !File.Exists(receipt) && restores==0,
                        failure+": confirmed missing or replaced target retires its receipt without touching another device");
                }
                else
                {
                    check(error!=null && File.ReadAllText(receipt)==original && restores==0,
                        failure+": uncertain stat outcome retains exact receipt and makes cleanup fail");
                    if(failure=="timeout")check(error is OperationCanceledException,"Stat timeout propagates without weakening device ownership");
                    else check(error is InvalidOperationException && error.Message.Contains(device)
                        && error.Message.Contains("receipt retained") && error.Message.Contains("retry stop")
                        && !error.Message.Contains("private-fixture-secret"),
                        failure+": uncertain release reports actionable redacted device diagnostics");
                    if(failure=="loop"){File.Delete(device);File.WriteAllText(device,"");}
                    retry=true;await access.Revoke("station");
                    check(!File.Exists(receipt) && restores==1,failure+": resolved uncertainty retries ACL restoration before retiring receipt");
                }
            }
            finally{Directory.Delete(root,true);}
        }
    }
}
