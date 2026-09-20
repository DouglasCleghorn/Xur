using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

public sealed class WorkloadRuntime(string directory,RecipeCatalog catalog,DisplayConsoles? consoles=null):IWorkloadRuntime
{
    readonly SemaphoreSlim gate=new(1,1);
    ParallelStopGate? stopGate;
    ParallelStopGate Stops=>LazyInitializer.EnsureInitialized(ref stopGate,()=>new ParallelStopGate(gate));
    readonly StationRuntime station=new(consoles);
    static readonly JsonSerializerOptions json=new(JsonSerializerDefaults.Web);
    static string Name(string id)=>"xur-workload-"+id;
    string ReceiptPath(string id)=>Path.Combine(directory,id+".json");
    Workload[] Definitions()
    {
        var all=new List<Workload>();
        if(Directory.Exists(directory))foreach(var path in Directory.GetFiles(directory,"*.json"))
            try {all.Add(JsonSerializer.Deserialize<Workload>(File.ReadAllText(path))!);}catch(FileNotFoundException) { /* A completed stop removed its receipt during observation. */ }
        return all.ToArray();
    }
    async Task<RuntimeObservation> ObserveUnlocked()
    {
        var gpus=await GpuInventory.Observe();var instances=new List<RuntimeInstance>();
        foreach(var w in Definitions().OrderBy(w=>w.Id,StringComparer.Ordinal))
            instances.Add(await Inspect(w) ?? new(w.Id,w.Fingerprint,"pending:"+w.Fingerprint,0,File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim(),"","pending",w.Gpus));
        var all=instances.ToArray();return new(Canonical.Hash(new {gpus,instances=all}),gpus,all);
    }
    public async Task<object> Graphics(string id)
    {
        if(!ProfilePolicy.EntityIdentifier(id))throw new InvalidOperationException("Invalid workstation ID.");
        var w=Definitions().SingleOrDefault(w=>w.Id==id&&w.Recipe.Kind=="Workstation")??throw new InvalidOperationException("Load the workstation before checking graphics.");
        if((await station.Inspect(w))?.State!="running")throw new InvalidOperationException("Load the workstation before checking graphics.");
        var gpu=(await GpuInventory.Observe()).SingleOrDefault(g=>w.Gpus.Contains(g.Pci))??throw new InvalidOperationException("The assigned GPU is unavailable.");
        return await StationGraphics.Collect(w,gpu);
    }
    public Task<RuntimeObservation> Observe()=>ObserveUnlocked();
    async Task<RuntimeInstance?> Inspect(Workload w)
    {
        if(w.Recipe.Kind=="Workstation")return await station.Inspect(w);
        ProcessResult r=new(1,"");
        for(int attempt=0;attempt<3;attempt++)
        {
            r=await Processes.Run("podman",["container","inspect",Name(w.Id)],20);
            if(r.ExitCode==0)break;
            var exists=await Processes.Run("podman",["container","exists",Name(w.Id)],10);
            if(exists.ExitCode==1)return null;
            if(exists.ExitCode!=0)throw new IOException("Container observation failed");
            // A create may finish between inspect and exists. Re-observe its
            // completed metadata instead of treating this normal race as failure.
            await Task.Delay(25);
        }
        if(r.ExitCode!=0)throw new IOException("Container observation did not stabilize");
        using var doc=JsonDocument.Parse(r.Output);var c=doc.RootElement[0];var labels=c.GetProperty("Config").GetProperty("Labels");
        if(labels.GetProperty("io.xur.fingerprint").GetString()!=w.Fingerprint)throw new InvalidOperationException("Container identity conflicts with its saved definition.");
        var ports=c.TryGetProperty("NetworkSettings",out var network) && network.ValueKind==JsonValueKind.Object && network.TryGetProperty("Ports",out var mappings) ? mappings : default;
        string endpoint="";
        if(ports.ValueKind==JsonValueKind.Object && ports.TryGetProperty(w.Recipe.Port+"/tcp",out var bindings) && bindings.ValueKind==JsonValueKind.Array && bindings.GetArrayLength()>0)
            endpoint="http://127.0.0.1:"+bindings[0].GetProperty("HostPort").GetString()+"/";
        return new(w.Id,w.Fingerprint,c.GetProperty("Id").GetString()!,c.GetProperty("State").GetProperty("Pid").GetInt32(),File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim(),endpoint,
            c.GetProperty("State").GetProperty("Status").GetString()!,w.Gpus);
    }
    public Task<RuntimeInstance> Start(Workload w)=>Start(w,false);
    async Task<RuntimeInstance> Start(Workload w,bool restoring)
    {
        await gate.WaitAsync();try
        {
            if(restoring)
            {
                var active=Path.Combine(directory,w.Id+".station-active");
                if(!File.Exists(active) || await File.ReadAllTextAsync(active)!=w.Fingerprint)
                    throw new InvalidOperationException("The workstation is no longer selected for boot restoration.");
            }
            catalog.Verify(w.Recipe);
            Directory.CreateDirectory(directory);
            var observed=await ObserveUnlocked();ProfilePolicy.Validate(new("runtime","Runtime",1,[w]),observed);
            foreach(var pci in w.Gpus)if(observed.Instances.Any(i=>i.Id!=w.Id && i.Gpus.Contains(pci)))throw new InvalidOperationException($"GPU {pci} is still allocated.");
            if(w.Recipe.Kind=="Workstation" && Definitions().Any(d=>d.Id!=w.Id && d.Recipe.Kind=="Workstation"))throw new InvalidOperationException("The local workstation is already allocated.");
            var saved=Definitions().SingleOrDefault(d=>d.Id==w.Id);
            if(saved!=null && saved.Fingerprint!=w.Fingerprint)throw new InvalidOperationException("Stop the previous workload before changing it.");
            if(saved==null)
            {
                // Receipt is written before creation so a crash can adopt only the intended instance.
                using(var f=new FileStream(ReceiptPath(w.Id)+".tmp",FileMode.Create,FileAccess.Write)) {JsonSerializer.Serialize(f,w);f.Flush(true);}
                File.Move(ReceiptPath(w.Id)+".tmp",ReceiptPath(w.Id));
            }
            var instance=await Inspect(w);
            if(w.Recipe.Kind=="Workstation")
            {
                RuntimeInstance started;
                try
                {
                    if(instance?.State!="running")
                    {
                        if(consoles!=null)foreach(var pci in w.Gpus)await consoles.Release(pci);
                        await GpuInventory.VerifyReleased(observed.Gpus.Where(g=>w.Gpus.Contains(g.Pci)).ToArray());
                    }
                    started=await station.Start(w,observed.Gpus.Single(g=>w.Gpus.Contains(g.Pci)));
                }
                finally{if(consoles!=null)foreach(var pci in w.Gpus)await consoles.FinishHandoff(pci);}
                var marker=Path.Combine(directory,w.Id+".station-active");
                using(var f=new FileStream(marker+".tmp",FileMode.Create,FileAccess.Write)){f.Write(System.Text.Encoding.UTF8.GetBytes(w.Fingerprint));f.Flush(true);}
                File.Move(marker+".tmp",marker,true);
                return started;
            }
            if(instance!=null&&w.Recipe.Kind=="Model")
            {
                var policy=await Processes.Run("podman",["update","--restart=no",Name(w.Id)],10);
                if(policy.ExitCode!=0)throw Failure("Could not suspend engine startup retries",policy);
            }
            if(w.Recipe.Engine is "vLLM" or "vLLM-Omni")await new ModelCatalog(Path.GetDirectoryName(directory)!).ValidateEngineCheckpoint(w.Recipe);
            if(instance==null)
            {
                var selected=observed.Gpus.Where(g=>w.Gpus.Contains(g.Pci)).ToArray();await GpuInventory.VerifyReleased(selected);
                var model=await DownloadModel(w.Recipe.Model);
                var modelFiles=await DownloadFiles(w.Recipe.Files);
                var pull=await Processes.Run("podman",w.Recipe.Kind=="Container"?["image","exists",w.Recipe.Image]:["pull","--arch=amd64",w.Recipe.Image],900);
                if(pull.ExitCode!=0)throw Failure("Engine image download failed",pull);
                if(w.Recipe.Engine=="vLLM-Omni"&&w.Recipe.Hub?.Repository=="fishaudio/s2-pro")
                {
                    var dependency=await Processes.Run("podman",["run","--rm","--network=none","--cap-drop=ALL","--security-opt=no-new-privileges","--entrypoint","python3",w.Recipe.Image,"-c","from vllm_omni.model_executor.models.fish_speech.dac_utils import build_dac_codec; build_dac_codec()"],120);
                    if(dependency.ExitCode!=0)throw new InvalidOperationException(EngineStartup.Failure(dependency.Output)??"The Fish engine failed its codec dependency check. Open workload logs before retrying.");
                }
                var args=new List<string> {"create","--name",Name(w.Id),"--label","io.xur.fingerprint="+w.Fingerprint,"--label","io.xur.id="+w.Id,
                    "--cap-drop=ALL","--security-opt=no-new-privileges","--pids-limit=4096","--shm-size=1g",w.Recipe.Kind=="Model"?"--restart=no":"--restart=unless-stopped"};
                if(w.Recipe.Port>0)args.AddRange(["--publish","127.0.0.1::"+w.Recipe.Port]);
                if(w.Recipe.Container is {} container)
                {
                    args.Add("--pull=never");
                    foreach(var mount in container.Mounts){await ContainerLibrary.VerifyVolume(mount.Volume);args.AddRange(["--volume",mount.Volume+":"+mount.Destination+":"+(mount.ReadOnly?"ro,z":"rw,z")]);}
                    foreach(var pair in container.Environment.OrderBy(p=>p.Key,StringComparer.Ordinal))args.AddRange(["--env",pair.Key+"="+pair.Value]);
                }
                else args.AddRange(["--volume","xur-cache-"+w.Id+":/root/.cache:Z"]);
                args.AddRange(TimezoneSettings.ContainerArguments());
                if(w.Recipe.Kind=="Model" && w.Recipe.Hub!=null)args.AddRange(new HuggingFaceCredentials(Path.GetDirectoryName(directory)!).ContainerArguments());
                foreach(var g in selected)
                {
                    if(g.Vendor=="NVIDIA")args.AddRange(["--device","nvidia.com/gpu="+g.RuntimeId]);
                    else foreach(var node in g.Nodes)args.AddRange(["--device",node]);
                }
                if(selected.Any(g=>g.Vendor=="NVIDIA"))args.AddRange(["--env","CUDA_VISIBLE_DEVICES="+string.Join(',',w.Gpus.Select(pci=>selected.Single(g=>g.Pci==pci).RuntimeId))]);
                if(selected.Any(g=>g.Vendor=="AMD"))args.AddRange(["--device","/dev/kfd"]);
                if(model!=null)args.AddRange(["--volume",model+":/model.gguf:ro,z"]);
                if(modelFiles!=null)args.AddRange(["--volume",modelFiles+":/models:ro,z"]);
                if(w.Recipe.Engine is "vLLM" or "vLLM-Omni")args.AddRange(["--entrypoint","vllm"]);
                args.Add(w.Recipe.Image);args.AddRange(w.Recipe.Command);
                var created=await Processes.Run("podman",args,60);
                if(created.ExitCode!=0)throw Failure("Container creation failed",created);
                instance=await Inspect(w) ?? throw new IOException();
            }
            var startupSince=DateTimeOffset.UtcNow.AddSeconds(-1).ToString("O");
            if(w.Recipe.Kind=="Model")
            {
                var policy=await Processes.Run("podman",["update","--restart=no",Name(w.Id)],10);
                if(policy.ExitCode!=0)throw Failure("Could not suspend engine startup retries",policy);
            }
            if(instance.State!="running")
            {
                // A detached conmon must not inherit xur-agent.service's lifetime.
                // This oneshot remains active after podman exits, owning conmon
                // independently of agent/control upgrades and crashes.
                await Processes.Run("systemctl",["stop","xur-container-"+w.Id+".service"],20);
                await Processes.Run("systemctl",["reset-failed","xur-container-"+w.Id+".service"],10);
                var started=await Processes.Run("systemd-run",["--unit=xur-container-"+w.Id,"--collect","--property=Type=oneshot",
                    "--property=RemainAfterExit=yes","--property=TimeoutStartSec=60","/usr/bin/podman","start",Name(w.Id)],75);
                if(started.ExitCode!=0)throw Failure("Container startup failed",started);
            }
            using var health=new HttpClient(new SocketsHttpHandler {AllowAutoRedirect=false}) {Timeout=TimeSpan.FromSeconds(3)};
            for(int n=0;n<600;n++)
            {
                instance=await Inspect(w) ?? throw new IOException();
                if(instance.State!="running")
                {
                    var log=await Processes.Run("podman",["logs","--tail=200",Name(w.Id)],10);
                    throw new InvalidOperationException(EngineStartup.Failure(log.Output)??"The engine exited before becoming ready. Open the workload logs.");
                }
                if(w.Recipe.Kind=="Model"&&n%5==0)
                {
                    var log=await Processes.Run("podman",["logs","--since",startupSince,"--tail=200",Name(w.Id)],10);
                    var failure=EngineStartup.Failure(log.Output);if(failure!=null)throw new InvalidOperationException(failure);
                }
                if(w.Recipe.Kind=="Container" && w.Recipe.Port==0)return instance;
                try {using var response=await health.GetAsync(instance.Endpoint.TrimEnd('/')+w.Recipe.HealthPath);if(response.IsSuccessStatusCode)
                    {
                        if(w.Recipe.Kind=="Model")
                        {
                            var policy=await Processes.Run("podman",["update","--restart=unless-stopped",Name(w.Id)],10);
                            if(policy.ExitCode!=0)throw Failure("Could not enable healthy engine restart policy",policy);
                        }
                        return instance;
                    }}catch(HttpRequestException){}catch(TaskCanceledException){}
                await Task.Delay(1000);
            }
            throw new InvalidOperationException("Engine health check timed out. It remains available for inspection; resume to retry the check.");
        }
        catch(Exception error)
        {
            if(ProfilePolicy.EntityIdentifier(w.Id))
            {Directory.CreateDirectory(directory);await File.WriteAllTextAsync(Path.Combine(directory,w.Id+".error.log"),Redaction.Logs(error.Message));}
            throw;
        }
        finally{gate.Release();}
    }
    static InvalidOperationException Failure(string stage,ProcessResult result)=>new(stage+": "+Redaction.Logs(result.Output.Length>8192 ? result.Output[^8192..] : result.Output).Trim());
    public async Task Stop(RuntimeStop request)
    {
        if(!ProfilePolicy.EntityIdentifier(request.Id))throw new InvalidOperationException("Invalid workload ID");
        await using var operation=await Stops.Enter();

        {
            var saved=Definitions().SingleOrDefault(w=>w.Id==request.Id);if(saved==null)return;
            await using var allocation=await Stops.Resources(new[]{"workload:"+request.Id}.Concat(saved.Gpus.Select(p=>"gpu:"+p)).Concat(saved.Recipe.Kind=="Workstation"?new[]{"station"}:Array.Empty<string>()));
            // A concurrent retry may have removed the receipt while this call waited.
            saved=Definitions().SingleOrDefault(w=>w.Id==request.Id);if(saved==null)return;
            var instance=await Inspect(saved);
            if(saved.Recipe.Kind=="Workstation")
            {
                if(instance!=null && instance.InstanceId!=request.InstanceId)throw new InvalidOperationException("The workstation instance changed outside this operation.");
                File.Delete(Path.Combine(directory,saved.Id+".station-active"));
                if(consoles!=null)foreach(var pci in saved.Gpus)await consoles.Release(pci);
                await station.Stop(saved);
                await GpuInventory.VerifyReleased((await GpuInventory.Observe()).Where(g=>saved.Gpus.Contains(g.Pci)).ToArray());
                File.Delete(ReceiptPath(request.Id));if(consoles!=null)foreach(var pci in saved.Gpus)await consoles.FinishHandoff(pci);return;
            }
            if(instance!=null)
            {
                if(instance.InstanceId!=request.InstanceId || instance.State=="running" && (request.Pid!=null && instance.Pid!=request.Pid || request.BootId!=null && instance.BootId!=request.BootId))throw new InvalidOperationException("The running instance changed outside this operation.");
                var stop=await Processes.Run("podman",["stop","--time=30",Name(request.Id)],45);
                if(stop.ExitCode!=0)throw new InvalidOperationException("Container stop failed.");
                var after=await Inspect(saved);
                if(after is {State:"running"} || after?.Pid>0)throw new InvalidOperationException("Container processes have not stopped.");
            }
            var gpus=await GpuInventory.Observe();if(saved.Gpus.Any(pci=>!gpus.Any(g=>g.Pci==pci)))throw new InvalidOperationException("A GPU disappeared during release verification.");await GpuInventory.VerifyReleased(gpus.Where(g=>saved.Gpus.Contains(g.Pci)).ToArray());
            if(instance!=null)
            {var remove=await Processes.Run("podman",["rm",Name(request.Id)],20);if(remove.ExitCode!=0)throw new IOException("Container removal failed");}
            await Processes.Run("systemctl",["stop","xur-container-"+request.Id+".service"],20);
            File.Delete(ReceiptPath(request.Id));
        }
    }
    async Task<string?> DownloadModel(ModelAsset? model)
    {
        if(model==null)return null;
        var models=Path.Combine(Path.GetDirectoryName(directory)!,"models");Directory.CreateDirectory(models);
        var path=Path.Combine(models,model.Sha256+".gguf");
        if(File.Exists(path) && new FileInfo(path).Length==model.Bytes)
        {await using var existing=File.OpenRead(path);if(Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(existing))==model.Sha256)return path;}
        using var timeout=new CancellationTokenSource(TimeSpan.FromHours(6));
        using var http=new HttpClient {Timeout=TimeSpan.FromHours(6)};
        using var request=new HuggingFaceCredentials(Path.GetDirectoryName(directory)!).Request(model.Url);
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);response.EnsureSuccessStatusCode();
        using(var file=new FileStream(path+".partial",FileMode.Create,FileAccess.Write))
        {
            using var hash=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            await using var input=await response.Content.ReadAsStreamAsync();var buffer=new byte[131072];long total=0;int n;
            while((n=await input.ReadAsync(buffer,timeout.Token))>0) {total+=n;if(total>model.Bytes)throw new InvalidOperationException("Model download size mismatch.");hash.AppendData(buffer,0,n);await file.WriteAsync(buffer.AsMemory(0,n));}
            if(total!=model.Bytes || Convert.ToHexStringLower(hash.GetHashAndReset())!=model.Sha256)throw new InvalidOperationException("Model download checksum mismatch.");
            file.Flush(true);
        }
        File.Move(path+".partial",path,true);return path;
    }
    async Task<string?> DownloadFiles(ModelFile[]? files)
    {
        if(files==null)return null;
        var root=Path.Combine(Path.GetDirectoryName(directory)!,"model-sets",Canonical.Hash(files));Directory.CreateDirectory(root);
        foreach(var file in files)
        {
            var target=Path.Combine(root,file.Name);var source=await DownloadModel(file.Asset);
            if(!File.Exists(target))
            {var r=await Processes.Run("ln",[source!,target],30);if(r.ExitCode!=0)throw new IOException("Could not link the model snapshot.");}
        }
        return root;
    }
    public async Task<StationStreamStatus[]> StreamingStatus()
    {var result=new List<StationStreamStatus>();foreach(var w in Definitions().Where(w=>w.Recipe.Kind=="Workstation"))result.Add(await StationStreaming.Status(w));return result.ToArray();}
    public async Task<System.Text.Json.JsonElement> Pairings(string id)
    {await RunningStation(id);return await StationStreaming.Pending(id);}
    public async Task Pair(string id,StationPairRequest request)
    {await RunningStation(id);await StationStreaming.Pair(id,request);}
    async Task<Workload> RunningStation(string id)
    {var w=Definitions().SingleOrDefault(w=>w.Id==id&&w.Recipe.Kind=="Workstation")??throw new InvalidOperationException("Workstation is not loaded.");if((await Inspect(w))?.State!="running")throw new InvalidOperationException("Load the workstation profile first.");return w;}
    public async Task<JsonElement> Display(string id,StationDisplayRequest? request)
    {
        await gate.WaitAsync();try{return await StationDisplay.Run(await RunningStation(id),request);}finally{gate.Release();}
    }
    public async Task StartStreaming(string id,bool restart=false)
    {
        await gate.WaitAsync();try
        {
            var w=await RunningStation(id);var g=(await GpuInventory.Observe()).Single(g=>w.Gpus.Contains(g.Pci));
            if(restart)await StationStreaming.Stop(id);
            // An active desktop may be left behind after virtual-output startup
            // failed. Reuse readiness preparation before retrying Sunshine.
            await station.Start(w,g);
        }finally{gate.Release();}
    }
    public async Task<string> Logs(string id)
    {
        if(!ProfilePolicy.EntityIdentifier(id) || !File.Exists(ReceiptPath(id)))throw new InvalidOperationException("Unknown workload.");
        var saved=Definitions().Single(w=>w.Id==id);
        if(saved.Recipe.Kind=="Workstation")return Redaction.Logs(await station.Logs(saved));
        var r=await Processes.Run("podman",["logs","--tail=200",Name(id)],10);var error=Path.Combine(directory,id+".error.log");return Redaction.Logs((File.Exists(error) ? File.ReadAllText(error)+"\n" : "")+r.Output);
    }
    public async Task RestoreStations()
    {
        foreach(var w in Definitions().Where(w=>w.Recipe.Kind=="Workstation"))
        {
            var marker=Path.Combine(directory,w.Id+".station-active");
            if(!File.Exists(marker) || await File.ReadAllTextAsync(marker)!=w.Fingerprint)continue;
            // Resolve PCI identities again on every boot; never persist card indices.
            for(var attempt=0;attempt<12;attempt++)
            {
                if(!File.Exists(marker))break;
                try{await Start(w,true);break;}catch(Exception){if(attempt==11)break;await Task.Delay(5000);}
            }
        }
    }
}
