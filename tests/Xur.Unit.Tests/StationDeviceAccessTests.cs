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
            var calls=new List<string>();var existing=false;var granted=new HashSet<string>();
            Task<ProcessResult> Run(string exe,string[] args,int timeout)
            {
                calls.Add(exe+" "+string.Join(' ',args));
                if(exe=="setfacl") {if(args.Contains("--remove"))granted.Remove(args[^1]);else granted.Add(args[^1]);}
                var user=granted.Contains(args[^1])?"user:1001:rw-\n":existing?"user:1001:r--\n":"";
                return Task.FromResult(new ProcessResult(0,exe=="stat"?"e2:1\n":exe=="getfacl"?"user::rw-\n"+user+"user:1002:rw-\ngroup::r--\nmask::rw-\nother::---\n":""));
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
            async Task<ProcessResult> Actual(string exe,string[] args,int timeout)=>await Processes.Run(exe,args.Select(a=>a.StartsWith("/dev/")?device:a).ToArray(),timeout);
            var real=new StationDeviceAccess(Path.Combine(root,"real"),Actual,boot);
            await real.GrantAccess("station",1001,gpu with{Nodes=[]});
            var observed=await Processes.Run("getfacl",["-cn",device],10);
            check(observed.Output.Contains("user:1001:rw-"),"Real ACL tools grant per-user access on an owned test file");
            await real.Revoke("station");observed=await Processes.Run("getfacl",["-cn",device],10);
            check(!observed.Output.Contains("user:1001:"),"Real ACL tools remove the temporary grant after teardown");
            await Processes.Run("setfacl",["-m","u:1002:rw-,m::r--",device],10);
            var restrictive=false;try{await real.GrantAccess("station",1001,gpu with{Nodes=[]});}catch(InvalidOperationException e){restrictive=e.Message.Contains("/dev/dri/card2")&&e.Message.Contains("r--")&&e.Message.Contains("user:1002:rw-");}
            check(restrictive,"Grant refuses to widen another user's restrictive ACL mask");
            async Task Set(string acl)
            {
                var r=await Processes.Run("setfacl",["--set",acl,device],10);check(r.ExitCode==0,"Set real ACL fixture: "+acl);
            }
            async Task<string> Acl()=>(await Processes.Run("getfacl",["-cn",device],10)).Output;
            // Reproduce logind removing the last session ACL and recalculating
            // the mask to the group's empty permissions. Exercise repeated loads.
            await Set("u::rw-,g::---,m::---,o::---");
            for(var i=0;i<3;i++)
            {
                await real.GrantAccess("station",1001,gpu with{Nodes=[]});
                check((await Acl()).Contains("user:1001:rw-")&&(await Acl()).Contains("mask::rw-"),"A cleaned-up session mask allows the assigned user to start");
                await real.Revoke("station");
                check(!(await Acl()).Contains("user:1001:")&&(await Acl()).Contains("mask::---"),"Stop restores the safe original mask across repeated profile switches");
            }
            await Set("u::rw-,u:1001:rw-,u:1002:r--,g::r--,m::r--,o::---");
            var before=await Acl();await real.GrantAccess("station",1001,gpu with{Nodes=[]});await real.GrantAccess("station",1001,gpu with{Nodes=[]});await real.Revoke("station");
            check(await Acl()==before,"Retry preserves the original masked user entry and restores its exact ACL");
            await real.GrantAccess("station",1001,gpu with{Nodes=[]});await Processes.Run("setfacl",["-m","m::r--",device],10);await real.Revoke("station");
            check(await Acl()==before,"Cleanup preserves the original ACL when logind has already restored its mask");
            await Set("u::rw-,u:1001:rw-,g::---,m::---,o::---");await real.GrantAccess("station",1001,gpu with{Nodes=[]});
            await Processes.Run("setfacl",["-m","u:1002:rw-",device],10);await real.Revoke("station");
            var concurrent=await Acl();
            check(concurrent.Contains("user:1002:rw-")&&concurrent.Contains("user:1001:---")&&concurrent.Contains("mask::rw-"),"Cleanup preserves concurrent grants without exposing the original user's masked permissions");
            foreach(var entry in new[]{"g::rw-","g:1002:rw-","u:1002:rw-"})
            {
                await Set("u::rw-,g::---,o::---,"+entry+",m::r--");var original=await Acl();
                var blocked=false;try{await real.GrantNodes("station",1001,["/dev/input/event7"]);}catch(InvalidOperationException e){blocked=e.Message.Contains("/dev/input/event7")&&!e.Message.Contains("DRM");}
                check(blocked&&await Acl()==original,"Restrictive "+entry+" remains unchanged and reports the actual peripheral");
            }
            await Set("u::rw-,g::--x,o::---");await real.GrantAccess("station",1001,gpu with{Nodes=[]});
            check((await Acl()).Contains("mask::rwx"),"Adding an ACL preserves pre-existing group execute access");await real.Revoke("station");
            await Set("u::rw-,g::---,m::---,o::---");await real.GrantAccess("station",1001,gpu with{Nodes=[]});
            await Processes.Run("setfacl",["-x","u:1001",device],10);await real.Revoke("station");
            check(!File.Exists(Path.Combine(root,"real","station.json")),"Cleanup tolerates a user ACL already removed by logind");
            var denied=false;try{StationDeviceAccess.Nodes(gpu with{Nodes=["/dev/dri/*"]});}catch(InvalidOperationException){denied=true;}
            check(denied,"Device permission grant rejects wildcard nodes");
        }
        finally{Directory.Delete(root,true);}
    }
}
