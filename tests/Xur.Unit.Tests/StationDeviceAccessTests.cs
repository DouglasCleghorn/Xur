using Xur.Agent;
using Xur.Domain;
static class StationDeviceAccessTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"xur-acl-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var boot=Path.Combine(root,"boot");File.WriteAllText(boot,"boot-one");
            var calls=new List<string>();var existing=false;
            Task<ProcessResult> Run(string exe,string[] args,int timeout)
            {
                calls.Add(exe+" "+string.Join(' ',args));
                return Task.FromResult(new ProcessResult(0,exe=="stat"?"e2:1\n":exe=="getfacl"?(existing?"user:1001:r--\nuser:1002:rw-\n":"user::rw-\nuser:1002:rw-\n"):""));
            }
            var gpu=new GpuDevice("0000:81:00.0","NVIDIA","RTX 3090","nvidia","",24576,["/dev/dri/renderD129"],[],["/dev/dri/card2"],[]);
            var access=new StationDeviceAccess(root,Run,boot);
            await access.GrantAccess("station",1001,gpu);await access.GrantAccess("station",1001,gpu);
            check(calls.Count(c=>c.StartsWith("getfacl"))==4,"Retry re-observes ACL policy without replacing original receipts");
            check(calls.Where(c=>c.StartsWith("setfacl")).All(c=>c.Contains("user:1001:rw-")&&(c.EndsWith("/dev/dri/card2")||c.EndsWith("/dev/dri/renderD129"))),"Headless access grants only the selected user's assigned DRM nodes");
            calls.Clear();await access.Revoke("station");
            check(calls.Count(c=>c.StartsWith("setfacl --no-mask --remove user:1001"))==2,"Stop removes only the temporary user's ACL entry");
            existing=true;await access.GrantAccess("station",1001,gpu);calls.Clear();await access.Revoke("station");
            check(calls.Count(c=>c.StartsWith("setfacl --no-mask --modify user:1001:r--"))==2,"Stop restores pre-existing access for the assigned user");
            await access.GrantAccess("station",1001,gpu);File.WriteAllText(boot,"boot-two");calls.Clear();await access.Revoke("station");
            check(calls.Count==0,"Old boot receipts never touch potentially renumbered devices");
            // Exercise the actual ACL tools on owned temporary files. No host
            // GPU or other user's permissions are touched by this test.
            var device=Path.Combine(root,"device");File.WriteAllText(device,"");
            async Task<ProcessResult> Actual(string exe,string[] args,int timeout)=>await Processes.Run(exe,args.Select(a=>a.StartsWith("/dev/dri/")?device:a).ToArray(),timeout);
            var real=new StationDeviceAccess(Path.Combine(root,"real"),Actual,boot);
            await real.GrantAccess("station",1001,gpu with{Nodes=[]});
            var observed=await Processes.Run("getfacl",["-cn",device],10);
            check(observed.Output.Contains("user:1001:rw-"),"Real ACL tools grant per-user access on an owned test file");
            await real.Revoke("station");observed=await Processes.Run("getfacl",["-cn",device],10);
            check(!observed.Output.Contains("user:1001:"),"Real ACL tools remove the temporary grant after teardown");
            await Processes.Run("setfacl",["-m","u:1002:rw-,m::r--",device],10);
            var restrictive=false;try{await real.GrantAccess("station",1001,gpu with{Nodes=[]});}catch(InvalidOperationException){restrictive=true;}
            check(restrictive,"Grant refuses to widen another user's restrictive ACL mask");
            var denied=false;try{StationDeviceAccess.Nodes(gpu with{Nodes=["/dev/dri/*"]});}catch(InvalidOperationException){denied=true;}
            check(denied,"Device permission grant rejects wildcard nodes");
        }
        finally{Directory.Delete(root,true);}
    }
}
