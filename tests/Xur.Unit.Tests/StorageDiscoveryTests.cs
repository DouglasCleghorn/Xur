using System.Text.Json;
using Xur.Agent;
using Xur.Domain;

static class StorageDiscoveryTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../.build/evidence/discovery-"+Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            bool readOnlyFailure=false;
            async Task<Storage> Scan(string filesystem,params string[] answers)
            {
                var directory=Path.Combine(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
                var nodes=new[]{JsonSerializer.SerializeToElement(new{path="/dev/test",type="disk",fstype="",mountpoints=Array.Empty<string>(),
                    children=new[]{new{path="/dev/test3",type="part",fstype=filesystem,mountpoints=Array.Empty<string>()}}
                        .Concat(answers.Select((_,i)=>new{path="/dev/config"+i,type="part",fstype="ext4",mountpoints=Array.Empty<string>()})).ToArray()})};
                string current="";int answer=0;
                Task<ProcessResult> Run(string exe,string[] args)
                {
                    if(exe=="blockdev")return Task.FromResult(readOnlyFailure&&args[^1]=="/dev/test3"?new ProcessResult(1,"Read-only check failed"):new ProcessResult(0,args[0]=="--getro"?"1":""));
                    if(exe=="losetup")
                    {
                        if(args.Contains("--detach"))return Task.FromResult(new ProcessResult(0,""));
                        current=args[^1];
                        if(current=="/dev/test3"&&filesystem=="crypto_LUKS")throw new Exception("Encrypted container must not be mounted or unlocked");
                        return Task.FromResult(new ProcessResult(0,"/dev/loop987"));
                    }
                    if(exe=="mount")
                    {
                        if(current.StartsWith("/dev/config"))
                        {
                            var payload=answers[answer++];
                            File.WriteAllText(Path.Combine(args[^1],"xur.yml"),payload);
                        }
                        return Task.FromResult(new ProcessResult(0,""));
                    }
                    if(exe=="stat")return Task.FromResult(new ProcessResult(0,"regular file"));
                    if(exe=="umount")return Task.FromResult(new ProcessResult(0,""));
                    throw new Exception("Unexpected command: "+exe);
                }
                var storage=new Storage(()=>Task.FromResult(nodes),Run,directory);
                await storage.DiscoverAnswers();return storage;
            }
            var locked=await Scan("crypto_LUKS");
            check(locked.CanPlan&&locked.Scan.State=="NoAnswer"&&locked.Scan.Errors.Length==0&&locked.Scan.Skipped is {Length:1},"A locked LUKS partition does not block explicit installation and is recorded as unsearched");
            check(locked.Scan.ReadOnlyDevices.Contains("/dev/test3")&&locked.Scan.Mounts.Length==0,"Encrypted storage stays read-only and is never mounted during answer discovery");
            var configured=await Scan("crypto_LUKS","schemaVersion: 1"+Environment.NewLine+"bootstrapToken: ABCDEF"+Environment.NewLine);
            check(configured.CanPlan&&configured.BootstrapToken=="ABCDEF"&&configured.Scan.Answers.SequenceEqual(["/dev/config0"]),"A separate valid answer remains discoverable and protected beside encrypted storage");
            var invalid=await Scan("crypto_LUKS","unknown: invalid");
            check(!invalid.CanPlan&&invalid.Scan.State=="Incomplete"&&invalid.Scan.Answers.Length==1,"An invalid answer still locks installation and identifies its configuration media");
            var duplicate=await Scan("crypto_LUKS","bootstrapToken: ABCDEF","bootstrapToken: ABCDEF");
            check(!duplicate.CanPlan&&duplicate.Scan.State=="Ambiguous","Encrypted storage does not bypass ambiguous-answer protection");
            readOnlyFailure=true;var unsafeDisk=await Scan("crypto_LUKS");
            check(!unsafeDisk.CanPlan&&unsafeDisk.Scan.State=="Incomplete","Skipping a LUKS container never bypasses failed read-only protection");readOnlyFailure=false;
            var unknown=await Scan("unknown_fs");
            check(!unknown.CanPlan&&unknown.Scan.State=="Incomplete","Unknown filesystem errors still block installation");
        }
        finally{Directory.Delete(root,true);}
    }
}
