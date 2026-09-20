using System.Text.RegularExpressions;
namespace Xur.Domain;

public record Recipe(string Id,string Name,string Image,string[] Command,int Port,string HealthPath,string Vendor,int GpuCount,long MemoryMiB,string Description,ModelAsset? Model=null,string Kind="Model",string Engine="llama.cpp",ModelFile[]? Files=null,HubModel? Hub=null,string? SettingsSource=null,ContainerConfiguration? Container=null);
public record ModelAsset(string Url,string Sha256,long Bytes,string License,string Upstream,string Revision);
public record ModelFile(string Name,ModelAsset Asset);
public record HubModel(string Repository,string Revision,string License);
public record Workload(string Id,string Name,Recipe Recipe,string[] Gpus,string Route,StationUser? User=null,StationDevices? Devices=null)
{
    // Display names and routes do not change the runtime. GPU order defines tensor rank.
    public string Fingerprint => Recipe.Kind=="Container" ? Canonical.Hash(new{Recipe.Image,Recipe.Command,Recipe.Port,Recipe.HealthPath,Recipe.Container,Gpus}) : Recipe.Kind=="Workstation" ? Devices!=null ? Canonical.Hash(new{Recipe.Kind,Recipe.Image,Gpus,User,Devices=new{Devices.Primary,Usb=(Devices.Usb??[]).Order(StringComparer.Ordinal).ToArray()}}) : User==null ? Canonical.Hash(new{Recipe.Kind,Recipe.Image,Gpus}) : Canonical.Hash(new{Recipe.Kind,Recipe.Image,Gpus,User}) : Recipe.Files==null && Recipe.Hub==null
        ? Canonical.Hash(new { Recipe.Image,Recipe.Command,Recipe.Port,Recipe.HealthPath,Recipe.Vendor,Recipe.GpuCount,Recipe.MemoryMiB,Recipe.Model,Gpus })
        : Canonical.Hash(new {Recipe.Image,Recipe.Command,Recipe.Port,Recipe.HealthPath,Recipe.Vendor,Recipe.GpuCount,Recipe.MemoryMiB,Files=Recipe.Files?.Select(f=>new{f.Name,f.Asset.Sha256,f.Asset.Bytes}).ToArray(),Hub=Recipe.Hub==null?null:new{Recipe.Hub.Repository,Recipe.Hub.Revision},Gpus});
}
public record StationDefinition(string Id,string Name,StationUser? User);
public record StationUser(string Username,int Uid,bool Temporary=false);
public record StationAccount(string Username,int Uid,string Name,string Home);
public record StationUserCreate(string Name);
public record Profile(string Id,string Name,long Revision,Workload[] Workloads);
public record GpuDevice(string Pci,string Vendor,string Name,string Driver,string RuntimeId,long MemoryMiB,string[] Nodes,string[] Problems,string[]? Cards=null,string[]? Displays=null,string? ShortId=null)
{
    public string DisplayName=>string.IsNullOrEmpty(ShortId)?Name:ShortId+" · "+Name;
}
public record RuntimeInstance(string Id,string Fingerprint,string InstanceId,int Pid,string BootId,string Endpoint,string State,string[] Gpus);
public record RuntimeObservation(string Generation,GpuDevice[] Gpus,RuntimeInstance[] Instances);
public record RuntimeStart(Workload Workload);
public record RuntimeStop(string Id,string InstanceId,int? Pid=null,string? BootId=null);
public record BackendRoute(string Name,string WorkloadId,string Endpoint);
public record TransitionStep(string Kind,string WorkloadId,string Description);
public record ProfilePlan(string Id,string Digest,string ObservationGeneration,string SourceEpoch,Profile Target,TransitionStep[] Steps,DateTimeOffset Expires,bool Unload=false);
public record Transition(string Id,string ProfileName,string Stage,int Completed,int Total,string? Error,DateTimeOffset Updated,bool Unload=false,TransitionStep[]? CompletedActions=null,string? CurrentAction=null);
public record ProfileState(Profile[] Profiles,Profile? Active,RuntimeObservation Runtime,Transition? Operation);
public interface IWorkloadRuntime
{
    Task Prepare(Workload[] workloads)=>Task.CompletedTask;
    Task<RuntimeObservation> Observe();
    Task<RuntimeInstance> Start(Workload workload);
    Task Stop(RuntimeStop request);
}
public interface IWorkloadGateway
{
    Task Drain(string id);
    Task Publish(BackendRoute[] routes);
}
public static class ProfilePolicy
{
    public static bool UserName(string? value)=>value!=null && Regex.IsMatch(value,@"^[a-z_][a-z0-9_-]{0,31}$");
    public static bool Identifier(string? value) => value!=null && Regex.IsMatch(value,@"^[a-z][a-z0-9-]{0,47}$");
    public static bool EntityIdentifier(string? value)=>Identifier(value) || value!=null && Regex.IsMatch(value,@"^[1-9][0-9]{0,18}$") && long.TryParse(value,out _);
    public static void Validate(Profile profile,RuntimeObservation observation)
    {
        if(!EntityIdentifier(profile.Id) || string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length>80 || profile.Workloads.Length>32)
            throw new InvalidOperationException("Use a profile name and at most 32 workloads.");
        StationDevicePolicy.Validate(profile.Workloads);
        var ids=new HashSet<string>();var routes=new HashSet<string>();var allocations=new HashSet<string>();
        foreach(var w in profile.Workloads)
        {
            if(!EntityIdentifier(w.Id) || !ids.Add(w.Id) || !Identifier(w.Route) || !routes.Add(w.Route) || string.IsNullOrWhiteSpace(w.Name) || w.Name.Length>80)
                throw new InvalidOperationException("Workload IDs and routes must be unique lowercase names.");
            ValidateRecipe(w.Recipe);
            if(w.User is {} user && (w.Recipe.Kind!="Workstation" || (user.Temporary ? user.Username!="temporary" || user.Uid!=0 : !UserName(user.Username) || user.Uid<1000 || user.Uid>=65534)))throw new InvalidOperationException("Select a workstation user.");
            if(w.Gpus.Length!=w.Recipe.GpuCount)throw new InvalidOperationException($"{w.Name} needs {w.Recipe.GpuCount} GPUs.");
            foreach(var pci in w.Gpus)
            {
                if(!allocations.Add(pci))throw new InvalidOperationException($"GPU {pci} is assigned more than once. Shared GPU memory is not enabled yet.");
                var gpu=observation.Gpus.SingleOrDefault(g=>g.Pci==pci) ?? throw new InvalidOperationException($"GPU {pci} is no longer present.");
                if(w.Recipe.Kind=="Workstation")
                {if(gpu.Cards is not {Length:>0})throw new InvalidOperationException("The selected GPU has no display device.");}
                else if(gpu.Vendor!=w.Recipe.Vendor || gpu.Problems.Length>0 || gpu.MemoryMiB<w.Recipe.MemoryMiB)
                    throw new InvalidOperationException($"GPU {pci} cannot run {w.Name}: {string.Join("; ",gpu.Problems.DefaultIfEmpty("vendor or memory mismatch"))}");
            }
        }
    }
    public static void ValidateRecipe(Recipe r)
    {
        if(r.Kind=="Workstation")
        {
            if(r.Id!="gaming-workstation" || r.Image!="host:plasma" || r.GpuCount!=1 || r.Vendor!="Display" || r.Command.Length!=0 || r.Files!=null || r.Model!=null || r.Hub!=null || r.Container!=null)
                throw new InvalidOperationException("Invalid workstation recipe.");
            return;
        }
        if(r.Kind=="Container")
        {
            if(!Identifier(r.Id) || string.IsNullOrWhiteSpace(r.Name) || r.Name.Length>80 || r.Engine!="Podman" || !Regex.IsMatch(r.Image??"",@"^sha256:[0-9a-f]{64}$") || r.Container==null || r.Port is <0 or >65535 || r.Vendor is not ("CPU" or "NVIDIA" or "AMD" or "Intel") || r.GpuCount is <0 or >16 || (r.Vendor=="CPU")!=(r.GpuCount==0)
                || r.Command.Length>128 || r.Command.Any(a=>a==null||a.Length>4096||a.Contains('\0')) || !Regex.IsMatch(r.HealthPath??"",@"^/[a-zA-Z0-9/_.-]*$") || r.Container.Mounts.Length>16 || r.Container.Environment.Count>64 || r.Model!=null || r.Files!=null || r.Hub!=null)
                throw new InvalidOperationException("Invalid container configuration.");
            foreach(var mount in r.Container.Mounts)
                if(!Regex.IsMatch(mount.Volume,@"^xur-volume-[a-f0-9]{32}$") || !Regex.IsMatch(mount.Destination,@"^/[a-zA-Z0-9/_.-]+$") || mount.Destination.Split('/').Any(p=>p=="..") || mount.Destination.StartsWith("/proc") || mount.Destination.StartsWith("/sys") || mount.Destination.StartsWith("/dev"))throw new InvalidOperationException("Invalid persistent volume mount.");
            if(r.Container.Mounts.Select(m=>m.Destination).Distinct().Count()!=r.Container.Mounts.Length)throw new InvalidOperationException("Volume destinations must be unique.");
            foreach(var pair in r.Container.Environment)if(!Regex.IsMatch(pair.Key,@"^[A-Za-z_][A-Za-z0-9_]*$") || pair.Value.Length>8192 || pair.Value.Contains('\0'))throw new InvalidOperationException("Invalid environment variable.");
            return;
        }
        if(r.Kind!="Model" || r.Container!=null)throw new InvalidOperationException("Unknown workload kind.");
        if(!Identifier(r.Id) || string.IsNullOrWhiteSpace(r.Name) ||
            !Regex.IsMatch(r.Image??"",@"^[a-zA-Z0-9][a-zA-Z0-9./:_-]+@sha256:[0-9a-f]{64}$") ||
            r.Command==null || r.Command.Length>128 || r.Command.Any(a=>a==null || a.Length>4096 || a.Contains('\0')) ||
            r.Port is <1 or >65535 || !Regex.IsMatch(r.HealthPath??"",@"^/[a-zA-Z0-9/_.-]*$") ||
            r.Vendor is not ("CPU" or "NVIDIA" or "AMD" or "Intel") || r.GpuCount is <0 or >16 || r.MemoryMiB<0 ||
            (r.Vendor=="CPU")!=(r.GpuCount==0))throw new InvalidOperationException("The recipe must use an immutable image digest, valid health endpoint and explicit GPU requirements.");
        if(r.Model is { } m && (!Uri.TryCreate(m.Url,UriKind.Absolute,out var u) || u.Scheme!="https" || !Regex.IsMatch(m.Sha256,@"^[0-9a-f]{64}$") || m.Bytes<=0))
            throw new InvalidOperationException("The model requires an HTTPS URL, SHA-256 and exact size.");
        if(r.Files!=null && (r.Files.Length is <1 or >256 || r.Files.Select(f=>f.Name).Distinct().Count()!=r.Files.Length || r.Files.Any(f=>!Regex.IsMatch(f.Name,@"^[a-zA-Z0-9_.-]+$") || f.Name.Contains("..") || !Uri.TryCreate(f.Asset.Url,UriKind.Absolute,out var url) || url.Scheme!="https" || !Regex.IsMatch(f.Asset.Sha256,@"^[0-9a-f]{64}$") || f.Asset.Bytes<=0)))
            throw new InvalidOperationException("Invalid model files.");
        if(r.Hub is {} hub && (!Regex.IsMatch(hub.Repository,@"^[a-zA-Z0-9_.-]+/[a-zA-Z0-9_.-]+$") || !Regex.IsMatch(hub.Revision,@"^[0-9a-f]{40}$")))throw new InvalidOperationException("Pin a model repository revision.");
    }
    public static TransitionStep[] Steps(Profile target,RuntimeObservation observed,bool restartStations=false)
    {
        var steps=new List<TransitionStep>();
        foreach(var old in observed.Instances.OrderBy(i=>i.Id,StringComparer.Ordinal))
        {
            var desired=target.Workloads.SingleOrDefault(w=>w.Id==old.Id);
            if(desired!=null && desired.Fingerprint==old.Fingerprint && old.State=="running" && !(restartStations&&desired.Recipe.Kind=="Workstation"))
                steps.Add(new("Keep",old.Id,"Keep the running instance and active requests"));
            else
            {
                steps.Add(new("Drain",old.Id,desired==null ? "Drain removed workload" : "Drain changed workload"));
                steps.Add(new("Stop",old.Id,"Stop and verify resource release"));
            }
        }
        foreach(var w in target.Workloads.OrderBy(w=>w.Id,StringComparer.Ordinal))
            if(restartStations&&w.Recipe.Kind=="Workstation" || !observed.Instances.Any(i=>i.Id==w.Id && i.Fingerprint==w.Fingerprint && i.State=="running"))
                steps.Add(new("Start",w.Id,"Start and pass the health check"));
        steps.Add(new("Publish","","Publish routes atomically"));
        return steps.ToArray();
    }
}

public sealed class RecipeCatalog
{
    readonly string directory;
    readonly string? selected;
    public Recipe[] Recipes => Read().Select(ModelLaunchSettings.RepairSaved).ToArray();
    public RecipeCatalog(string directory,string? selected=null)
    {
        this.directory=directory;this.selected=selected;Read();
    }
    Recipe[] Read()
    {
        var recipes=new[]{directory,selected}.Where(d=>d!=null && Directory.Exists(d)).SelectMany(d=>Directory.GetFiles(d!,"*.json",SearchOption.AllDirectories)).Select(p=>System.Text.Json.JsonSerializer.Deserialize<Recipe>(File.ReadAllText(p),new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!).ToArray();
        foreach(var r in recipes)ProfilePolicy.ValidateRecipe(r);
        if(recipes.Select(r=>r.Id).Distinct().Count()!=recipes.Length)throw new InvalidOperationException("Duplicate recipe ID");
        return recipes;
    }
    public void Verify(Recipe recipe)
    {if(!Read().Any(r=>r.Id==recipe.Id && (Canonical.Hash(r)==Canonical.Hash(recipe) || Canonical.Hash(ModelLaunchSettings.RepairSaved(r))==Canonical.Hash(recipe))))throw new InvalidOperationException("Select a recipe from the installed catalog.");}
}
