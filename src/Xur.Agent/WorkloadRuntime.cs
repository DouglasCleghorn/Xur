using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

public sealed class WorkloadRuntime(string directory,RecipeCatalog catalog,DisplayConsoles? consoles=null,DataStorage? dataStorage=null):IWorkloadRuntime
{
    readonly DataStorage storage=dataStorage??new DataStorage(Path.GetDirectoryName(directory)!);
    NetworkShares? networkShares;
    public NetworkShares NetworkShares=>LazyInitializer.EnsureInitialized(ref networkShares,()=>new(Path.GetDirectoryName(directory)!));
    public async Task<T> ChangeNetworkShares<T>(Func<Task<T>> action)
    {
        await gate.WaitAsync();try{return await action();}finally{gate.Release();}
    }
    T3Storage? t3Storage;
    public T3Storage T3Storage=>LazyInitializer.EnsureInitialized(ref t3Storage,()=>new(Path.GetDirectoryName(directory)!,storage));
    public async Task<T3StorageConfig> SetT3Storage(T3StorageConfig config)
    {
        await using var allocation=await Stops.Resources(["t3-code"]);
        return T3Storage.Save(config);
    }
    public Func<string,bool> PersistedOwner {get;set;} = _=>false;
    public bool IndependentT3 {get;set;}
    bool Persisted(Workload w)=>PersistedOwner(w.Id)||(IndependentT3&&T3CodeWorkload.Is(w.Recipe));
    public Workload[] SavedWorkloads()=>Definitions();
    public async Task DeleteModel(ModelLibrary library,ModelDeleteRequest request)
    {
        // Exclusive with starts, stops and storage migration: a model cannot
        // become mounted or downloaded between the usage check and deletion.
        if(!await gate.WaitAsync(0))throw new InvalidOperationException("Wait for the current workload or storage change before deleting a model.");
        try{await library.Delete(request,Definitions());}finally{gate.Release();}
    }
    Task? storageMove;
    public bool StorageMoveBusy=>storageMove is {IsCompleted:false};
    public async Task MoveStorage(DataStorageRequest request)
    {
        if(StorageMoveBusy)throw new InvalidOperationException("A storage move is already running.");
        if(!await gate.WaitAsync(0))throw new InvalidOperationException("Wait for the current workload change before moving storage.");
        try
        {
            if(Definitions().Length>0)throw new InvalidOperationException("Unload all profiles before moving storage.");
            if(File.Exists("/var/lib/xur/app/maintenance"))throw new InvalidOperationException("Wait for the application update before moving storage.");
            storageMove=Task.Run(async()=>{
                try{await storage.Move(request);}
                catch(Exception error){Console.Error.WriteLine("Storage move: "+Redaction.Logs(error.Message));}
                finally{gate.Release();}
            });
        }
        catch{gate.Release();throw;}
    }
    readonly RoboticsWebContainer robotWeb=new(Environment.GetEnvironmentVariable("XUR_RUN")??"/run/xur",stateDirectory:Path.Combine(Path.GetDirectoryName(directory)!,"robotics"));
    readonly SemaphoreSlim gate=new(1,1);
    ParallelStopGate? stopGate;
    ParallelStopGate Stops=>LazyInitializer.EnsureInitialized(ref stopGate,()=>new ParallelStopGate(gate));
    readonly StationRuntime station=new(consoles);
    readonly SemaphoreSlim receipts=new(1,1);
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
    async Task<RuntimeObservation> ObserveUnlocked(bool profilesOnly=false)
    {
        var gpus=await GpuInventory.Observe();var instances=new List<RuntimeInstance>();
        foreach(var w in Definitions().Where(w=>!profilesOnly||!Persisted(w)).OrderBy(w=>w.Id,StringComparer.Ordinal))
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
    public async Task Prepare(Workload[] workloads)
    {
        if(workloads.Any(Persisted))throw new InvalidOperationException("Persisted Compute services are managed independently of profiles.");
        ProfilePolicy.Validate(new("runtime","Runtime",1,workloads),await ObserveUnlocked());
        await StationSeats.SetIntent(workloads.Where(w=>w.Recipe.Kind=="Workstation").ToArray());
    }
    public Task<RuntimeObservation> Observe()=>ObserveUnlocked(profilesOnly:true);
    public Task<RuntimeObservation> ObserveAll()=>ObserveUnlocked();
    public async Task<UnslothStatus> UnslothStatus()
    {
        var definition=Definitions().SingleOrDefault(w=>UnslothWorkload.Is(w.Recipe));
        using var client=new HttpClient(new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false});
        return await UnslothRuntime.Status(definition!=null && (await Inspect(definition))?.State=="running",client);
    }
    public async Task<UnslothPassword> UnslothPassword()
    {
        await using var operation=await Stops.Enter();
        await using var allocation=await Stops.Resources(["unsloth"]);
        var definition=Definitions().SingleOrDefault(w=>UnslothWorkload.Is(w.Recipe));
        var instance=definition==null?null:await Inspect(definition);
        if(instance?.State!="running")throw new InvalidOperationException("Load a profile with Unsloth first.");
        var result=await Processes.Run("podman",["exec",instance.InstanceId,"cat",UnslothWorkload.StudioHome+"/auth/.bootstrap_password"],10);
        if(result.ExitCode!=0 || string.IsNullOrWhiteSpace(result.Output) || result.Output.Length>4096)
            throw new InvalidOperationException("The first-login password is unavailable. Wait for Studio to finish starting, or sign in with the password you already set.");
        return new(result.Output.TrimEnd('\r','\n'));
    }
    public async Task<T3CodeStatus> T3CodeStatus()
    {
        var definition=Definitions().SingleOrDefault(w=>T3CodeWorkload.Is(w.Recipe));
        if(definition==null)return new(null,"Stopped");
        var instance=await Inspect(definition);
        return new(definition.Id,instance?.State=="running"?"Running":"Stopped");
    }
    public async Task<T3CodePairing> PairT3Code()
    {
        await using var operation=await Stops.Enter();
        var definition=Definitions().SingleOrDefault(w=>T3CodeWorkload.Is(w.Recipe))
            ??throw new InvalidOperationException("Enable T3 Code first.");
        await using var allocation=await Stops.Resources(["workload:"+definition.Id]);
        var instance=await Inspect(definition);
        if(instance?.State!="running")throw new InvalidOperationException("Enable T3 Code first.");
        // Use the inspected immutable identity, never a caller-supplied command or container name.
        var result=await Processes.Run("podman",["exec","--user","1000:1000",instance.InstanceId,"sh","-c",T3CodeWorkload.Launcher,"t3-launcher","auth","pairing","create",
            "--base-dir",T3CodeWorkload.BaseDirectory,"--ttl","5m","--label","Xur connection","--json"],30);
        // Do not copy command output into logs/errors: it contains a pairing secret.
        if(result.ExitCode!=0)throw new InvalidOperationException("T3 Code could not create a pairing link. Check that the server is ready and retry.");
        try
        {
            using var data=JsonDocument.Parse(result.Output);
            var credential=data.RootElement.GetProperty("credential").GetString();
            var expires=data.RootElement.GetProperty("expiresAt").GetDateTimeOffset();
            if(string.IsNullOrWhiteSpace(credential)||credential.Length>4096||expires<=DateTimeOffset.UtcNow)throw new FormatException();
            return new(credential,expires);
        }
        catch(Exception e) when(e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {throw new InvalidOperationException("T3 Code returned an invalid pairing response. Check the server logs and update T3 Code before retrying.");}
    }
    public async Task<T> WithT3Code<T>(Func<string,Task<T>> action)
    {
        await using var operation=await Stops.Enter();
        var definition=Definitions().SingleOrDefault(w=>T3CodeWorkload.Is(w.Recipe))
            ??throw new InvalidOperationException("Enable T3 Code first.");
        await using var allocation=await Stops.Resources(["workload:"+definition.Id]);
        var instance=await Inspect(definition);
        if(instance?.State!="running")throw new InvalidOperationException("Enable T3 Code first.");
        return await action(instance.InstanceId);
    }
    public async Task InitializeRoboticsSetup()
    {
        try
        {
            await robotWeb.MigrateLegacy();
            foreach(var workload in Definitions().Where(w=>w.Recipe.Kind=="Robotics"))await robotWeb.RestoreOwnership(workload);
            await robotWeb.ReconcileOwnership();
        }
        catch
        {
            // Keep controller input away from the host console until the
            // uncertain container setup has been inspected and reconciled.
            robotWeb.ReserveForRecovery();throw;
        }
    }
    async Task<RuntimeInstance?> Inspect(Workload w)
    {
        if(w.Recipe.Kind=="Robotics")
            return await robotWeb.Inspect(w);
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
        var fingerprint=T3CodeWorkload.Is(w.Recipe)?T3Storage.Fingerprint(w,labels):w.Fingerprint;
        return new(w.Id,fingerprint,c.GetProperty("Id").GetString()!,c.GetProperty("State").GetProperty("Pid").GetInt32(),File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim(),endpoint,
            c.GetProperty("State").GetProperty("Status").GetString()!,w.Gpus);
    }
    public Task<RuntimeInstance> Start(Workload w)
    {
        if(Persisted(w))throw new InvalidOperationException("Enable this service through Persisted Compute.");
        return Start(w,false);
    }
    public Task<RuntimeInstance> StartPersisted(Workload w,CancellationToken cancellationToken)=>Start(w,false,cancellationToken);
    async Task<RuntimeInstance> Start(Workload w,bool restoring,CancellationToken cancellationToken=default)
    {
        await using var operation=await Stops.Enter();
        await using var allocation=await Stops.Resources(new[]{"workload:"+w.Id}.Concat(w.Gpus.Select(p=>"gpu:"+p)).Concat(w.Recipe.Kind=="Workstation"?new[]{"user:"+StationAccounts.Username(w)}:[]).Concat(T3CodeWorkload.Is(w.Recipe)?new[]{"t3-code"}:[]).Concat(UnslothWorkload.Is(w.Recipe)?new[]{"unsloth"}:[]));
        try
        {
            if(restoring)
            {
                var active=Path.Combine(directory,w.Id+".station-active");
                if(!File.Exists(active) || await File.ReadAllTextAsync(active)!=w.Fingerprint)
                    throw new InvalidOperationException("The workstation is no longer selected for boot restoration.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            catalog.Verify(w.Recipe);
            if(w.Recipe.Kind=="Model" || UnslothWorkload.Is(w.Recipe))await storage.Require("models");
            if(UnslothWorkload.Is(w.Recipe))await storage.Require("unsloth");
            var network=await NetworkShares.RequireMounts(w);
            Directory.CreateDirectory(directory);
            var observed=await ObserveUnlocked();ProfilePolicy.Validate(new("runtime","Runtime",1,[w]),observed);
            if((w.Recipe.Kind=="Robotics" && Definitions().Any(d=>d.Id!=w.Id && d.Recipe.Kind is "Workstation" or "Robotics")) ||
                (w.Recipe.Kind=="Workstation" && Definitions().Any(d=>d.Id!=w.Id && d.Recipe.Kind=="Robotics")))
                throw new InvalidOperationException("Robotics and workstations must release their peripherals before starting the other workload.");
            if(T3CodeWorkload.Is(w.Recipe) && Definitions().Any(d=>d.Id!=w.Id && T3CodeWorkload.Is(d.Recipe)))
                throw new InvalidOperationException("Stop the previous T3 Code server before starting another.");
            if(UnslothWorkload.Is(w.Recipe) && Definitions().Any(d=>d.Id!=w.Id && UnslothWorkload.Is(d.Recipe)))
                throw new InvalidOperationException("Stop the previous Unsloth server before starting another.");
            foreach(var pci in w.Gpus)if(observed.Instances.Any(i=>i.Id!=w.Id && i.Gpus.Contains(pci)))throw new InvalidOperationException($"GPU {pci} is still allocated.");
            if(w.Recipe.Kind=="Workstation" && Definitions().Any(d=>d.Id!=w.Id && d.Recipe.Kind=="Workstation" && StationAccounts.Username(d)==StationAccounts.Username(w)))throw new InvalidOperationException("Each running workstation needs a different user.");
            await receipts.WaitAsync();try {
            var saved=Definitions().SingleOrDefault(d=>d.Id==w.Id);
            if(saved!=null && saved.Fingerprint!=w.Fingerprint)throw new InvalidOperationException("Stop the previous workload before changing it.");
            if(saved==null)
            {
                // Receipt is written before creation so a crash can adopt only the intended instance.
                using(var f=new FileStream(ReceiptPath(w.Id)+".tmp",FileMode.Create,FileAccess.Write)) {JsonSerializer.Serialize(f,w);f.Flush(true);}
                File.Move(ReceiptPath(w.Id)+".tmp",ReceiptPath(w.Id));
            }
            }finally{receipts.Release();}
            var instance=await Inspect(w);
            if(T3CodeWorkload.Is(w.Recipe) && instance!=null && instance.Fingerprint!=w.Fingerprint)
                throw new InvalidOperationException("Disable and re-enable T3 Code to recreate it with shared GPU access. Its persistent storage will be retained.");
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
                await new EngineRestartPolicy().Set(Name(w.Id),instance.InstanceId,"no");
            }
            if(instance?.State!="running" && w.Recipe.Engine is "vLLM" or "vLLM-Omni")await new ModelCatalog(Path.GetDirectoryName(directory)!).ValidateEngineCheckpoint(w.Recipe);
            if(w.Recipe.Kind=="Robotics")
                return await robotWeb.Start(w);
            ModelImageSelection? engineImage=null;
            if(w.Recipe.Kind=="Model")
            {
                engineImage=await new ModelImageUpdater(buildRoot:Path.Combine(Path.GetDirectoryName(directory)!,"engine-builds")).Refresh(w,instance);
                if(engineImage?.Recreate==true)instance=null;
                var updateLog=Path.Combine(directory,w.Id+".update.log");
                if(engineImage?.Warning is {} warning)
                {await File.WriteAllTextAsync(updateLog,warning);Console.Error.WriteLine(w.Id+": "+warning);}
                else if(engineImage!=null)File.Delete(updateLog);
            }
            if(UnslothWorkload.Is(w.Recipe))
            {
                var unsloth=new UnslothRuntime();
                engineImage=await unsloth.Refresh(instance);
                if(engineImage?.Recreate==true)instance=null;
                if(instance==null && !storage.Selected("unsloth"))await unsloth.EnsureStorage();
            }
            if(instance==null)
            {
                var selected=observed.Gpus.Where(g=>w.Gpus.Contains(g.Pci)).ToArray();await GpuInventory.VerifyReleased(selected);
                string? model,modelFiles;
                await using(var cache=await Stops.Resources(new[]{"model-downloads"}.Concat((w.Recipe.Model is {} asset?new[]{asset.Sha256}:[]).Concat((w.Recipe.Files??[]).Select(f=>f.Asset.Sha256)).Select(hash=>"model:"+hash))))
                {model=await DownloadModel(w.Recipe.Model);modelFiles=await DownloadFiles(w.Recipe.Files);}
                if(w.Recipe.Kind=="Container" && !UnslothWorkload.Is(w.Recipe))
                {
                    var exists=await Processes.Run("podman",["image","exists",w.Recipe.Image],15);
                    if(exists.ExitCode!=0)throw Failure("Prepared container image is unavailable",exists);
                }
                var image=engineImage?.Image??w.Recipe.Image;
                var args=new List<string> {"create","--name",Name(w.Id),"--label","io.xur.fingerprint="+w.Fingerprint,"--label","io.xur.id="+w.Id,
                    "--pull=never","--cap-drop=ALL","--security-opt=no-new-privileges","--pids-limit=4096",(UnslothWorkload.Is(w.Recipe)||w.Recipe.Kind=="Model"&&w.Recipe.Engine is "vLLM" or "vLLM-Omni")?"--shm-size=4g":"--shm-size=1g",w.Recipe.Kind=="Model"||UnslothWorkload.Is(w.Recipe)||Persisted(w)||w.NetworkShares is {Length:>0}?"--restart=no":"--restart=unless-stopped"};
                args.AddRange(network.Arguments);
                if(w.Recipe.Port>0)args.AddRange(["--publish",T3CodeWorkload.Is(w.Recipe)?$"{T3CodeWorkload.HostPort}:{w.Recipe.Port}":UnslothWorkload.Is(w.Recipe)?UnslothWorkload.Publish:"127.0.0.1::"+w.Recipe.Port]);
                if(w.Recipe.Container is {} container)
                {
                    foreach(var mount in container.Mounts)
                    {
                        var source=mount.Volume;
                        if(UnslothWorkload.Is(w.Recipe)&&storage.Selected("unsloth"))source=storage.PathFor("unsloth",mount.Destination=="/workspace"?"workspace":"studio");
                        else await ContainerLibrary.VerifyVolume(mount.Volume);
                        args.AddRange(["--volume",source+":"+mount.Destination+":"+(mount.ReadOnly?"ro,z":"rw,z")]);
                    }
                    foreach(var pair in container.Environment.OrderBy(p=>p.Key,StringComparer.Ordinal))args.AddRange(["--env",pair.Key+"="+pair.Value]);
                }
                else args.AddRange(["--volume","xur-cache-"+w.Id+":/root/.cache:Z"]);
                if(UnslothWorkload.Is(w.Recipe))args.AddRange(UnslothRuntime.ModelArguments(Path.GetDirectoryName(directory)!,part=>storage.PathFor("models",part)));
                if(w.Recipe.Engine=="audio.cpp")args.AddRange(await AudioCppRuntime.Arguments(storage.PathFor("models","models"),w.Id,selected.SelectMany(g=>g.Nodes).Distinct().ToArray()));
                args.AddRange(TimezoneSettings.ContainerArguments());
                if(w.Recipe.Kind=="Model" && w.Recipe.Hub!=null)args.AddRange(new HuggingFaceCredentials(Path.GetDirectoryName(directory)!).ContainerArguments());
                args.AddRange(T3CodeWorkload.Is(w.Recipe)?await T3CodeGpuAccess.Arguments(observed.Gpus):ModelGpuArguments.For(w,observed.Gpus));
                if(model!=null)args.AddRange(["--volume",model+":/model.gguf:ro,z"]);
                if(modelFiles!=null)args.AddRange(["--volume",modelFiles+":/models:ro,z"]);
                if(w.Recipe.Engine is "vLLM" or "vLLM-Omni")args.AddRange(["--entrypoint","vllm"]);
                if(T3CodeWorkload.Is(w.Recipe))
                {
                    args.AddRange(T3CodeRuntime.ContainerArguments());
                    var access=await T3Storage.Arguments();
                    if(access.Contains("--privileged"))
                    {
                        args.Remove("--cap-drop=ALL");
                        args.Remove("--security-opt=no-new-privileges");
                    }
                    args.AddRange(access);
                }
                args.Add(image);args.AddRange(T3CodeWorkload.Is(w.Recipe)?T3CodeRuntime.Command(w.Recipe,args.Contains("--privileged")):engineImage?.Command(w.Recipe.Command)??w.Recipe.Command);
                var created=await Processes.Run("podman",args,60);
                if(created.ExitCode!=0)throw Failure("Container creation failed",created);
                instance=await Inspect(w) ?? throw new IOException();
            }
            var startupSince=DateTimeOffset.UtcNow.AddSeconds(-1).ToString("O");
            if(w.Recipe.Kind=="Model"||Persisted(w)||w.NetworkShares is {Length:>0})
            {
                await new EngineRestartPolicy().Set(Name(w.Id),instance.InstanceId,"no");
            }
            if(instance.State!="running")
            {
                // A detached conmon must not inherit xur-agent.service's lifetime.
                // This oneshot remains active after podman exits, owning conmon
                // independently of agent/control upgrades and crashes.
                await Processes.Run("systemctl",["stop","xur-container-"+w.Id+".service"],20);
                await Processes.Run("systemctl",["reset-failed","xur-container-"+w.Id+".service"],10);
                var started=await Processes.Run("systemd-run",["--unit=xur-container-"+w.Id,"--collect","--property=Type=oneshot",
                    "--property=RemainAfterExit=yes","--property=TimeoutStartSec=60",..Xur.Agent.NetworkShares.WorkloadUnitProperties(Name(w.Id),network.Units),"/usr/bin/podman","start",Name(w.Id)],75);
                if(started.ExitCode!=0)throw Failure("Container startup failed",started);
            }
            using var health=new HttpClient(new SocketsHttpHandler {AllowAutoRedirect=false}) {Timeout=TimeSpan.FromSeconds(3)};
            for(int n=0;n<600;n++)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                        // Automatic recovery calls the same checked startup path.
                        File.Delete(Path.Combine(directory,w.Id+".error.log"));
                        return instance;
                    }}catch(HttpRequestException){}catch(TaskCanceledException){}
                await Task.Delay(1000,cancellationToken);
            }
            throw new InvalidOperationException("Engine health check timed out. It remains available for inspection; resume to retry the check.");
        }
        catch(Exception error)
        {
            if(ProfilePolicy.EntityIdentifier(w.Id))
            {Directory.CreateDirectory(directory);await File.WriteAllTextAsync(Path.Combine(directory,w.Id+".error.log"),Redaction.Logs(error.Message));}
            throw;
        }
    }
    static InvalidOperationException Failure(string stage,ProcessResult result)=>new(stage+": "+Redaction.Logs(result.Output.Length>8192 ? result.Output[^8192..] : result.Output).Trim());
    public Task Stop(RuntimeStop request)=>Stop(request,false);
    public Task StopPersisted(RuntimeStop request)=>Stop(request,true);
    async Task Stop(RuntimeStop request,bool persistent)
    {
        if(!ProfilePolicy.EntityIdentifier(request.Id))throw new InvalidOperationException("Invalid workload ID");
        await using var operation=await Stops.Enter();

        {
            var saved=Definitions().SingleOrDefault(w=>w.Id==request.Id);if(saved==null)return;
            // Old profile journals must never override a migrated service's intent.
            if(!persistent&&Persisted(saved))return;
            await using var allocation=await Stops.Resources(new[]{"workload:"+request.Id}.Concat(saved.Gpus.Select(p=>"gpu:"+p)).Concat(saved.Recipe.Kind=="Workstation"?new[]{"user:"+StationAccounts.Username(saved)}:Array.Empty<string>()).Concat(T3CodeWorkload.Is(saved.Recipe)?new[]{"t3-code"}:[]).Concat(UnslothWorkload.Is(saved.Recipe)?new[]{"unsloth"}:[]));
            // A concurrent retry may have removed the receipt while this call waited.
            saved=Definitions().SingleOrDefault(w=>w.Id==request.Id);if(saved==null)return;
            var instance=await Inspect(saved);
            if(saved.Recipe.Kind=="Robotics")
            {
                await robotWeb.Stop(request);File.Delete(ReceiptPath(request.Id));return;
            }
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
        var models=storage.PathFor("models","models");Directory.CreateDirectory(models);
        var path=Path.Combine(models,model.Sha256+".gguf");
        if(File.Exists(path) && new FileInfo(path).Length==model.Bytes)
        {await using var existing=File.OpenRead(path);if(Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(existing))==model.Sha256)return path;}
        DataStorage.CheckCapacity(models,model.Bytes);
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
        var root=Path.Combine(storage.PathFor("models","model-sets"),Canonical.Hash(files));Directory.CreateDirectory(root);
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
    public async Task<byte[]> Screenshot(string id,CancellationToken cancellation)
    {
        if(!ProfilePolicy.EntityIdentifier(id))throw new InvalidOperationException("Invalid workstation ID.");
        await using var operation=await Stops.Enter();
        // Match starts/stops on this workstation; other desktops can capture in
        // parallel without racing a user/GPU handoff on the selected desktop.
        await using var allocation=await Stops.Resources(["workload:"+id]);
        cancellation.ThrowIfCancellationRequested();
        return await StationScreenshot.Capture(await RunningStation(id),cancellation);
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
        if(saved.Recipe.Kind=="Robotics")return await robotWeb.Logs();
        var r=await Processes.Run("podman",["logs","--tail=200",Name(id)],10);var error=Path.Combine(directory,id+".error.log");var update=Path.Combine(directory,id+".update.log");return Redaction.Logs((File.Exists(error) ? File.ReadAllText(error)+"\n" : "")+(File.Exists(update)?File.ReadAllText(update)+"\n":"")+r.Output);
    }
    public async Task RestoreStations()
    {
        using var slots=new SemaphoreSlim(4,4);
        await Task.WhenAll(Definitions().Where(w=>w.Recipe.Kind=="Workstation").Select(async w=>
        {
            var marker=Path.Combine(directory,w.Id+".station-active");
            if(!File.Exists(marker) || await File.ReadAllTextAsync(marker)!=w.Fingerprint)return;
            await slots.WaitAsync();try {
            // Resolve PCI identities again on every boot; never persist card indices.
            for(var attempt=0;attempt<12;attempt++)
            {
                if(!File.Exists(marker))break;
                try{await Start(w,true);break;}catch(Exception e){if(attempt==11){Console.Error.WriteLine("Workstation restoration failed for "+w.Id+": "+Redaction.Logs(e.Message));break;}await Task.Delay(5000);}
            }
            }finally{slots.Release();}
        }));
    }
}
