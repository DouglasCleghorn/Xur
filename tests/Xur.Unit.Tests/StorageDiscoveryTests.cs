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
            bool readOnlyFailure=false;string fileName="xur.yml";bool symlink=false;bool btrfsMountFailure=false;
            async Task<Storage> Scan(string filesystem,params string[] answers)
            {
                var directory=Path.Combine(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
                var nodes=new[]{JsonSerializer.SerializeToElement(new{path="/dev/test",type="disk",fstype="",size="68719476736",serial="fixture",wwn="",model="Fixture",ro="True",mountpoints=Array.Empty<string>(),
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
                        if(current=="/dev/test3"&&filesystem=="btrfs")
                        {
                            // Current installer kernels reject the old standalone alias.
                            var options=args[Array.IndexOf(args,"-o")+1].Split(',');
                            if(options.Contains("nologreplay"))return Task.FromResult(new ProcessResult(32,"btrfs: Unknown parameter 'nologreplay'"));
                            if(!options.Contains("ro")||!options.Contains("rescue=nologreplay"))throw new Exception("Btrfs discovery must disable writes and log replay");
                            if(btrfsMountFailure)return Task.FromResult(new ProcessResult(32,"Btrfs fixture mount failure"));
                        }
                        if(current.StartsWith("/dev/config"))
                        {
                            var payload=answers[answer++];
                            if(symlink)File.CreateSymbolicLink(Path.Combine(args[^1],fileName),"missing-config");
                            else File.WriteAllText(Path.Combine(args[^1],fileName),payload);
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
            var btrfs=await Scan("btrfs");
            check(btrfs.CanPlan&&btrfs.Scan.State=="NoAnswer"&&btrfs.Scan.Mounts is [{Filesystem:"btrfs",BlockReadOnly:true}],"An existing Btrfs installation is discoverable on kernels that reject the standalone nologreplay alias");
            btrfsMountFailure=true;btrfs=await Scan("btrfs");btrfsMountFailure=false;
            check(!btrfs.CanPlan&&btrfs.Scan.State=="Incomplete"&&btrfs.Scan.Errors.Any(e=>e.Contains("Btrfs fixture mount failure")),"Btrfs mount failure still locks installation without retrying an unsafe mount");
            check(locked.Diagnostics==null,"No diagnostic configuration means no diagnostic listener is configured");
            check(locked.CanPlan&&locked.Scan.State=="NoAnswer"&&locked.Scan.Errors.Length==0&&locked.Scan.Skipped is {Length:1},"A locked LUKS partition does not block explicit installation and is recorded as unsearched");
            check(locked.Scan.ReadOnlyDevices.Contains("/dev/test3")&&locked.Scan.Mounts.Length==0,"Encrypted storage stays read-only and is never mounted during answer discovery");
            var configured=await Scan("crypto_LUKS","schemaVersion: 1"+Environment.NewLine+"bootstrapToken: ABCDEF"+Environment.NewLine);
            check(configured.CanPlan&&configured.BootstrapToken=="ABCDEF"&&configured.Scan.Answers.SequenceEqual(["/dev/config0"]),"A separate valid answer remains discoverable and protected beside encrypted storage");
            var invalid=await Scan("crypto_LUKS","unknown: invalid");
            check(!invalid.CanPlan&&invalid.Scan.State=="Incomplete"&&invalid.Scan.Answers.Length==1,"An invalid answer still locks installation and identifies its configuration media");
            var duplicate=await Scan("crypto_LUKS","bootstrapToken: ABCDEF","bootstrapToken: ABCDEF");
            check(!duplicate.CanPlan&&duplicate.Scan.State=="Ambiguous","Encrypted storage does not bypass ambiguous-answer protection");
            fileName="xur-diagnostics.yml";
            var diagnosticYaml="schemaVersion: 1\napiKey: "+new string('a',64)+"\nallowControl: true\n";
            var diagnostic=await Scan("crypto_LUKS",diagnosticYaml);
            check(diagnostic.Diagnostics?.AllowControl==true&&diagnostic.CanPlan&&diagnostic.DiagnosticMedia.SetEquals(["/dev/config0"]),"Diagnostic media is discovered independently of answers and protected from erasure");
            check((await diagnostic.Observe()).Disks.Single().Blocked.Any(b=>b.Contains("configuration media")),"Installation inventory blocks the whole ancestor disk of diagnostic configuration media");
            diagnostic=await Scan("crypto_LUKS",diagnosticYaml,diagnosticYaml);
            check(diagnostic.Diagnostics==null&&diagnostic.DiagnosticError.Contains("Multiple"),"Duplicate diagnostic configurations disable the API");
            diagnostic=await Scan("crypto_LUKS","apiKey: short");
            check(diagnostic.Diagnostics==null&&diagnostic.DiagnosticMedia.Count==1&&diagnostic.DiagnosticError.Length>0,"Invalid diagnostic media stays protected while its API remains disabled");
            symlink=true;diagnostic=await Scan("crypto_LUKS",diagnosticYaml);symlink=false;
            check(diagnostic.Diagnostics==null&&diagnostic.DiagnosticError.Length>0,"Diagnostic configuration symlinks are rejected");
            fileName="xur.yml";
            readOnlyFailure=true;var unsafeDisk=await Scan("crypto_LUKS");
            check(!unsafeDisk.CanPlan&&unsafeDisk.Scan.State=="Incomplete","Skipping a LUKS container never bypasses failed read-only protection");readOnlyFailure=false;
            var unknown=await Scan("unknown_fs");
            check(!unknown.CanPlan&&unknown.Scan.State=="Incomplete","Unknown filesystem errors still block installation");
        }
        finally{Directory.Delete(root,true);}
    }
}
