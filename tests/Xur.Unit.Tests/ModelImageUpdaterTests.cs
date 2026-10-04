using System.Text.Json;
using Xur.Agent;
using Xur.Domain;
static class ModelImageUpdaterTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var latest="sha256:"+new string('a',64);var old="sha256:"+new string('b',64);
        var recipe=new Recipe("refresh","Refresh","mirror.gcr.io/vllm/vllm-omni@sha256:"+new string('c',64),[],8000,"/health","NVIDIA",1,0,"",Engine:"vLLM-Omni");
        var workload=new Workload("refresh","Refresh",recipe,[],"workload-refresh");
        var stopped=new RuntimeInstance(workload.Id,workload.Fingerprint,"container-id",0,"boot","","exited",[]);
        var calls=new List<string[]>();string current=old,status="exited",identity=stopped.InstanceId,fingerprint=workload.Fingerprint;
        bool pullFails=false,removeFails=false,invalid=false,cached=false,timeout=false;
        Task<ProcessResult> Run(string exe,string[] args,int seconds)
        {
            calls.Add(args);
            if(args[0]=="pull"&&timeout)throw new OperationCanceledException();
            return Task.FromResult(args[0] switch {
                "pull"=>new ProcessResult(pullFails?1:0,pullFails?"mirror cache miss":invalid?"not an image":latest+"\nWarning: fixture\n"),
                "container"=>new ProcessResult(0,JsonSerializer.Serialize(new[]{new{Id=identity,Image=current,Config=new{Labels=new Dictionary<string,string>{{"io.xur.fingerprint",fingerprint}}},State=new{Status=status,Pid=status=="running"?123:0}}})),
                "image" when args[1]=="ls"=>new ProcessResult(0,cached?latest+" mirror.gcr.io/vllm/vllm-omni:latest\n":""),
                "image"=>new ProcessResult(cached?0:1,cached?JsonSerializer.Serialize(new[]{new{Id=latest,Created="2026-10-01T00:00:00Z",Architecture="amd64",Os="linux"}}):""),
                "rm"=>new ProcessResult(removeFails?1:0,"fixture removal"),_=>throw new Exception("Unexpected updater command")});
        }
        var updater=new ModelImageUpdater(Run);
        var fresh=await updater.Refresh(workload,null);
        check(fresh?.Image==latest&&fresh.Recreate==false&&calls.Single()[^1]=="mirror.gcr.io/vllm/vllm-omni:latest","New model starts pull the latest mirrored channel even for a previously pinned saved recipe");
        check(calls.Single().Contains("--quiet")&&calls.Single().Contains("--arch=amd64")&&calls.Single().Contains("--policy=always"),"Engine pulls resolve an explicit architecture and return the downloaded image identity");
        calls.Clear();var changed=await updater.Refresh(workload,stopped);
        check(changed?.Recreate==true&&calls.Last().SequenceEqual(new[]{"rm",stopped.InstanceId}),"A stopped container using an old image is removed by verified instance ID for recreation");
        check(!calls.Any(a=>a.Contains("--force")),"Latest-image replacement never forcibly removes a concurrently started engine");
        current=latest;calls.Clear();var unchanged=await updater.Refresh(workload,stopped);
        check(unchanged?.Recreate==false&&calls.Any(a=>a[0]=="pull")&&!calls.Any(a=>a[0]=="rm"),"A stopped container already using latest is checked again and reused");
        calls.Clear();check(await updater.Refresh(workload,stopped with{State="running"})==null&&calls.Count==0,"Running model containers are kept intact without downloading or replacing their engine");
        current=old;calls.Clear();var prepared=false;
        var fish=await updater.Refresh(workload,stopped,image=>{prepared=image==latest;return Task.FromResult(old);});
        check(prepared&&fish?.Recreate==false&&!calls.Any(a=>a[0]=="rm"),"Fish refresh compares the prepared image after resolving the latest upstream base");
        async Task<bool> Rejected(){try{await updater.Refresh(workload,stopped);return false;}catch(InvalidOperationException){return true;}}
        pullFails=true;calls.Clear();
        var offline=await updater.Refresh(workload,stopped);
        check(offline?.Image==old&&offline.Recreate==false&&offline.Warning?.Contains("mirror cache miss")==true&&!calls.Any(a=>a[0]=="rm"),"Failed pull reuses a stopped engine when its image is the only locally available copy");
        prepared=false;
        await updater.Refresh(workload,stopped,image=>{prepared=true;return Task.FromResult(image);});
        check(!prepared,"Offline reuse of an existing Fish runtime does not build its codec layer twice");
        bool missing=false;try{await updater.Refresh(workload,null);}catch(InvalidOperationException e){missing=e.Message.Contains("no cached image");}
        check(missing,"A failed first pull with no local engine produces a clear startup error");
        cached=true;calls.Clear();offline=await updater.Refresh(workload,stopped);
        check(offline?.Image==latest&&offline.Recreate&&offline.Warning!=null,"Failed pull uses the newest locally cached upstream image rather than an older stopped container");
        calls.Clear();offline=await updater.Refresh(workload,stopped,_=>throw new InvalidOperationException("Codec package download unavailable"));
        check(offline?.Image==old&&offline.Recreate==false&&offline.Warning?.Contains("Codec package download unavailable")==true,"Offline Fish startup can reuse the retained prepared runtime when cached-base preparation needs unavailable packages");
        calls.Clear();offline=await updater.Refresh(workload,null);
        check(offline?.Image==latest&&offline.Warning!=null&&!calls.Any(a=>a[0]=="container"),"A fresh offline container starts from a previously downloaded engine");
        timeout=true;calls.Clear();offline=await updater.Refresh(workload,null);
        check(offline?.Image==latest&&offline.Warning?.Contains("timed out")==true,"Pull timeouts also use the latest locally available engine");
        timeout=false;cached=false;pullFails=false;
        invalid=true;calls.Clear();check(await Rejected()&&calls.Count==1,"Invalid pull identity cannot be treated as a usable latest image");invalid=false;
        status="running";calls.Clear();check(await Rejected()&&!calls.Any(a=>a[0]=="rm"),"A stopped engine that starts during the download is not removed");status="exited";
        identity="replacement";calls.Clear();check(await Rejected()&&!calls.Any(a=>a[0]=="rm"),"Engine replacement during the download is rejected");identity=stopped.InstanceId;
        fingerprint="different";calls.Clear();check(await Rejected()&&!calls.Any(a=>a[0]=="rm"),"An unrelated container cannot be removed during an engine update");fingerprint=workload.Fingerprint;
        removeFails=true;check(await Rejected(),"Removal failure prevents starting an old engine after a new image was downloaded");
        foreach(var (engine,vendor,id) in new[]{("llama.cpp","CPU","server"),("llama.cpp","NVIDIA","server-cuda"),("llama.cpp","AMD","server-rocm"),("llama.cpp","Intel","server-vulkan"),("vLLM","NVIDIA","vllm"),("vLLM-Omni","NVIDIA","omni")})
            check(EngineImages.For(recipe with{Engine=engine,Vendor=vendor})==EngineImages.Image(id),"Latest startup selects the correct engine channel for "+engine+" / "+vendor);
    }
}
