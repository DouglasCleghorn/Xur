using System.Text.Json;
using Xur.Agent;
using Xur.Domain;
static class DataStorageTests
{
    public static async Task Run(Action<bool,string> check)
    {
        using(var f=new Fixture())
        {
            var storage=f.Storage;
            check(storage.PathFor("models","models")==f.Root+"/state/models"&&!storage.Selected("unsloth"),"Unconfigured storage keeps existing models and Unsloth volumes");
            await storage.Move(new("default",Fixture.Uuid));
            var config=storage.Config();await storage.Require("unsloth");await storage.Require("models");
            check(config.Locations.Count==2&&config.DefaultUuid==Fixture.Uuid,"The default drive moves both inheriting data groups together");
            check(File.ReadAllText(storage.PathFor("unsloth","workspace")+"/dataset.txt")=="training data"&&File.ReadAllText(storage.PathFor("unsloth","studio")+"/settings.json")=="studio settings","Unsloth downloads, datasets and Studio state survive relocation");
            check(new FileInfo(storage.PathFor("unsloth","workspace")+"/model-link").LinkTarget=="dataset.txt","Relative Hugging Face-style cache links survive relocation");
            var inode=await Processes.Run("stat",["-c","%i",storage.PathFor("models","models")+"/blob.gguf",storage.PathFor("models","model-sets")+"/snapshot/model.gguf"]);
            check(inode.Output.Split('\n',StringSplitOptions.RemoveEmptyEntries).Distinct().Count()==1,"Model blobs and snapshot hard links remain deduplicated across directories");
            check(File.Exists(f.Root+"/state/models/blob.gguf")&&File.Exists(f.Workspace+"/dataset.txt"),"Successful storage migration retains every original");
            var status=await storage.Status();check(status.Job?.Stage=="Complete"&&status.Job.RetainedPaths.Length==4&&!status.Busy,"Completed migrations expose their retained sources");
            f.Present=false;
            await Reject(()=>storage.Require("unsloth"),"A missing selected drive fails closed",check);
            check((await storage.Status()).Groups.All(g=>g.Available==null&&g.Error!=null),"A missing data drive never reports system disk space as available");
            f.Present=true;f.WrongIdentity=true;
            await Reject(()=>storage.Require("models"),"A different filesystem at the selected mount is rejected",check);
            f.WrongIdentity=false;f.ReadOnly=true;
            await Reject(()=>storage.Require("unsloth"),"A read-only selected drive cannot start a workload",check);
            f.ReadOnly=false;
            await storage.Move(new("unsloth","system"));
            check(storage.Config().Overrides["unsloth"]=="system"&&storage.Config().Locations["unsloth"].Uuid==null&&storage.Config().Locations["models"].Uuid==Fixture.Uuid,"A workload override moves only its data while other workloads retain the default");
            await storage.Move(new("default","system"));
            check(storage.Config().Locations.Values.All(l=>l.Uuid==null)&&Directory.GetFiles(f.Root+"/state/storage-moves").Length==3,"Returning to system storage preserves migration history and explicit overrides");
            await storage.Move(new("unsloth","inherit"));
            check(!storage.Config().Overrides.ContainsKey("unsloth"),"A workload can return to inheriting the default without rewriting its data");
        }
        using(var f=new Fixture())
        {
            await f.Storage.Move(new("unsloth","system"));
            await f.Storage.Move(new("default",Fixture.Uuid));
            check(!f.Storage.Selected("unsloth")&&f.Storage.Selected("models"),"Default changes respect an explicit system-disk override");
        }
        using(var f=new Fixture())
        {
            f.FailCopy=true;
            await Reject(()=>f.Storage.Move(new("default",Fixture.Uuid)),"Copy errors abort the storage change",check);
            check(!f.Storage.Selected("models")&&!f.Storage.Selected("unsloth")&&File.Exists(f.Root+"/state/models/blob.gguf"),"A later group failing cannot partially commit an earlier verified group");
            check((await f.Storage.Status()).Job?.Stage=="Failed","Copy failures remain visible after the HTTP request finishes");
            f.FailCopy=false;await f.Storage.Move(new("default",Fixture.Uuid));
            check((await f.Storage.Status()).Job?.Stage=="Complete","Retry uses a fresh destination without destroying the partial copy");
        }
        using(var f=new Fixture())
        {
            f.Corrupt=true;
            await Reject(()=>f.Storage.Move(new("models",Fixture.Uuid)),"A same-length checksum mismatch aborts switching",check);
            check(!f.Storage.Selected("models"),"Unverified model copies are never selected");
        }
        using(var f=new Fixture())
        {
            f.Free=100;
            await Reject(()=>f.Storage.Move(new("default",Fixture.Uuid)),"Migration capacity includes headroom before copying",check);
            check(f.Copies==0&&!f.Storage.Selected("models"),"Insufficient space does not begin the copy");
            f.Free=100L<<30;f.Block=new(TaskCreationOptions.RunContinuationsAsynchronously);f.Reached=new(TaskCreationOptions.RunContinuationsAsynchronously);
            var move=f.Storage.Move(new("models",Fixture.Uuid));await f.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Reject(()=>f.Storage.Move(new("default","system")),"Concurrent migrations cannot interleave configuration writes",check);
            check(f.Storage.Busy,"Long copies expose busy state for update coordination");
            f.Block.SetResult();await move;
        }
        using(var f=new Fixture())
        {
            f.WrongVolume=true;
            await Reject(()=>f.Storage.Move(new("default",Fixture.Uuid)),"Unexpected legacy volume locations are not silently replaced with empty folders",check);
            check(f.Copies==0&&!f.Storage.Selected("unsloth"),"Volume identity is verified before any source is copied");
        }
        using(var f=new Fixture())
        {
            f.Free=20L<<30;f.Measured=3L<<30;
            await Reject(()=>f.Storage.Move(new("default",Fixture.Uuid)),"Capacity preflight sums both groups on the same drive",check);
            check(f.Copies==0,"The entire move fits before copying its first group");
        }
        using(var f=new Fixture())
        {
            Directory.CreateSymbolicLink(f.Root+"/mounts/"+Fixture.Uuid,f.Root+"/state");
            await Reject(()=>f.Storage.Move(new("models",Fixture.Uuid)),"A symlink cannot redirect the managed mount point",check);
        }
        using(var f=new Fixture())
        {
            await File.WriteAllTextAsync(f.Root+"/state/storage-move.json",JsonSerializer.Serialize(new DataStorageJob("old","Verifying","copying",DateTimeOffset.UtcNow,[f.Workspace])));
            check((await f.Storage.Status()).Job?.Stage=="Interrupted"&&!f.Storage.Selected("models"),"An interrupted process reports the unfinished move and keeps original locations");
            await Reject(()=>f.Storage.Move(new("default","../../wrong")),"Storage requests cannot supply arbitrary paths",check);
        }
        await Reject(()=>{DataStorage.CheckAvailable(20L<<30,19L<<30);return Task.CompletedTask;},"Download capacity reserves working space",check);
    }
    static async Task Reject(Func<Task> action,string name,Action<bool,string> check)
    {var failed=false;try{await action();}catch(InvalidOperationException){failed=true;}check(failed,name);}
    sealed class Fixture:IDisposable
    {
        public const string Uuid="a111-b222";
        public string Root=Path.GetFullPath(".build/evidence/storage/"+Guid.NewGuid().ToString("N"));
        public DataStorage Storage;public bool Present=true,Mounted,WrongIdentity,ReadOnly,FailCopy,Corrupt,WrongVolume;public int Copies;public long Free=100L<<30;public long? Measured;
        public TaskCompletionSource? Block,Reached;
        public string Workspace=>Root+"/volumes/"+UnslothWorkload.Mounts[0].Volume+"/_data";
        public Fixture()
        {
            Directory.CreateDirectory(Root+"/state/models");Directory.CreateDirectory(Root+"/state/model-sets/snapshot");Directory.CreateDirectory(Root+"/mounts");
            File.WriteAllText(Root+"/state/models/blob.gguf","model bytes");
            var link=Processes.Run("ln",[Root+"/state/models/blob.gguf",Root+"/state/model-sets/snapshot/model.gguf"]).GetAwaiter().GetResult();if(link.ExitCode!=0)throw new Exception(link.Output);
            foreach(var mount in UnslothWorkload.Mounts)Directory.CreateDirectory(Root+"/volumes/"+mount.Volume+"/_data");
            File.WriteAllText(Workspace+"/dataset.txt","training data");File.CreateSymbolicLink(Workspace+"/model-link","dataset.txt");
            File.WriteAllText(Root+"/volumes/"+UnslothWorkload.Mounts[1].Volume+"/_data/settings.json","studio settings");
            Storage=new(Root+"/state",Run,Root+"/mounts",Root+"/volumes",_=>Free);
        }
        async Task<ProcessResult> Run(string exe,string[] args,int seconds)
        {
            if(exe=="findmnt")return new(0,JsonSerializer.Serialize(new{filesystems=Present&&Mounted?new[]{new Dictionary<string,object>{{"source","/dev/fake"},{"target",Root+"/mounts/"+Uuid},{"fstype","ext4"},{"size",200L<<30},{"used",10L<<30},{"avail",Free},{"options",ReadOnly?"ro":"rw"},{"maj:min","9:1"}}}:[]}));
            if(exe=="lsblk")return new(0,JsonSerializer.Serialize(new{blockdevices=new[]{new Dictionary<string,object>{{"path","/dev/fake"},{"uuid",WrongIdentity?"c333-d444":Uuid},{"rota",false},{"disc-max",1000},{"maj:min","9:1"}}}}));
            if(exe=="mount"){if(!Present)return new(1,"Drive missing");Mounted=true;return new(0,"");}
            if(exe=="podman"&&args[1]=="inspect")return new(0,JsonSerializer.Serialize(new[]{new{Labels=new Dictionary<string,string>{{"io.xur.volume",args[^1]}},Mountpoint=WrongVolume?Root+"/elsewhere":Root+"/volumes/"+args[^1]+"/_data"}}));
            if(exe=="podman")return new(0,"");
            if(exe=="du"&&Measured is {} bytes)return new(0,bytes+"\t"+args[^1]);
            if(exe=="cp")
            {
                Copies++;if(Block!=null){Reached!.TrySetResult();await Block.Task;}
                if(FailCopy&&args.Any(a=>a.Contains("volumes")))return new(1,"Injected copy failure");
                var result=await Processes.Run(exe,args,seconds);
                if(Corrupt)File.WriteAllText(args[^1]+"/models/blob.gguf","model wrong");
                return result;
            }
            return await Processes.Run(exe,args,seconds);
        }
        public void Dispose(){if(Directory.Exists(Root))Directory.Delete(Root,true);}
    }
}
