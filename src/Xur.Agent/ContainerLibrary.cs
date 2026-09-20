using System.Text.Json;
using System.Text.RegularExpressions;
using Xur.Domain;
namespace Xur.Agent;

public sealed class ContainerLibrary
{
    readonly string directory,selected;
    readonly object gate=new();
    Task? worker;
    static readonly JsonSerializerOptions json=new(JsonSerializerDefaults.Web);
    public bool Busy { get { lock(gate)return worker is {IsCompleted:false}; } }
    public ContainerLibrary(string state)
    {
        directory=Path.Combine(state,"container-jobs");selected=Path.Combine(state,"catalog-selected");Directory.CreateDirectory(directory);
        foreach(var job in Jobs().Where(j=>j.Stage is "Preparing" or "Building" or "Pulling"))Save(job with {Stage="Interrupted",Log="Preparation was interrupted. Prepare the container again.",Updated=DateTimeOffset.UtcNow});
    }
    public ContainerJob[] Jobs()=>Directory.GetFiles(directory,"*.json").Select(p=>JsonSerializer.Deserialize<ContainerJob>(File.ReadAllText(p),json)!).OrderByDescending(j=>j.Updated).Take(30).ToArray();
    void Save(ContainerJob job)
    {
        var path=Path.Combine(directory,job.Id+".json");var tmp=path+".tmp";
        using(var f=new FileStream(tmp,new FileStreamOptions{Mode=FileMode.Create,Access=FileAccess.Write,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite})){JsonSerializer.Serialize(f,job,json);f.Flush(true);}File.Move(tmp,path,true);
    }
    public ContainerJob Prepare(ContainerPrepareRequest request)
    {
        lock(gate)
        {
            if(Busy)throw new InvalidOperationException("A container is already being prepared.");
            bool build=!string.IsNullOrWhiteSpace(request.Dockerfile);
            if(build==!string.IsNullOrWhiteSpace(request.Image))throw new InvalidOperationException("Choose a container image or a Dockerfile.");
            if(!build && !Regex.IsMatch(request.Image!,@"^[A-Za-z0-9][A-Za-z0-9._:/@-]{1,500}$"))throw new InvalidOperationException("Enter a valid registry image reference.");
            if(request.Dockerfile?.Length>1024*1024 || request.Files?.Length>64 || (request.Files?.Sum(f=>f.Content.Length)??0)>8*1024*1024)throw new InvalidOperationException("Build context exceeds 8 MiB or 64 files.");
            foreach(var file in request.Files??[])
                if(!Regex.IsMatch(file.Name,@"^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*$") || file.Name.Split('/').Any(p=>p is "." or "..") || file.Name.Equals("Dockerfile",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Build files need safe relative names.");
            var mounts=(request.Volumes??[]).Select(v=>new ContainerMount(v.Volume??"xur-volume-"+new string('0',32),v.Destination,v.ReadOnly)).ToArray();
            ProfilePolicy.ValidateRecipe(Recipe(request,"container-validation","sha256:"+new string('0',64),mounts));
            if((request.Volumes??[]).Any(v=>string.IsNullOrWhiteSpace(v.Name)||v.Name.Length>80||v.Name.Any(char.IsControl)))throw new InvalidOperationException("Give each volume a short name.");
            var job=new ContainerJob(Guid.NewGuid().ToString("N"),"Preparing","",null,DateTimeOffset.UtcNow);Save(job);worker=Task.Run(()=>Run(job,request));return job;
        }
    }
    static Recipe Recipe(ContainerPrepareRequest r,string id,string image,ContainerMount[] mounts)=>new(id,string.IsNullOrWhiteSpace(r.Name)?"Container "+id[^6..]:r.Name.Trim(),image,r.Command??[],r.Port,r.HealthPath,r.Vendor,r.GpuCount,0,"Custom container",Kind:"Container",Engine:"Podman",Container:new(mounts,r.Environment??[]));
    async Task Run(ContainerJob job,ContainerPrepareRequest request)
    {
        var context=Path.Combine(directory,job.Id);Directory.CreateDirectory(context);
        void Stage(string stage,string log=""){job=job with {Stage=stage,Log=log,Updated=DateTimeOffset.UtcNow};Save(job);}
        try
        {
            string reference;
            ProcessResult result;
            if(!string.IsNullOrWhiteSpace(request.Dockerfile))
            {
                Stage("Building","Building the Dockerfile…");
                await File.WriteAllTextAsync(Path.Combine(context,"Dockerfile"),request.Dockerfile);
                foreach(var file in request.Files??[]){var buildPath=Path.Combine(context,file.Name);Directory.CreateDirectory(Path.GetDirectoryName(buildPath)!);await File.WriteAllTextAsync(buildPath,file.Content);}
                reference="localhost/xur-custom:"+job.Id;
                result=await Processes.Run("podman",["build","--pull=missing","--tag",reference,"--file",Path.Combine(context,"Dockerfile"),context],3600);
            }
            else {reference=request.Image!;Stage("Pulling","Pulling container image…");result=await Processes.Run("podman",["pull",reference],1800);}
            var log=Redaction.Logs(result.Output);if(log.Length>32768)log=log[^32768..];
            if(result.ExitCode!=0){Stage("Failed",log);return;}
            var inspect=await Processes.Run("podman",["image","inspect",reference],15);if(inspect.ExitCode!=0)throw new IOException("Image inspection failed.");
            using var image=JsonDocument.Parse(inspect.Output);var identity=image.RootElement[0].GetProperty("Id").GetString()!;
            if(Regex.IsMatch(identity,@"^[a-f0-9]{64}$"))identity="sha256:"+identity;
            var mounts=new List<ContainerMount>();
            foreach(var volume in request.Volumes??[])
            {
                var id=volume.Volume;
                if(id==null)
                {
                    id="xur-volume-"+Guid.NewGuid().ToString("N");
                    var created=await Processes.Run("podman",["volume","create","--label","io.xur.volume="+id,"--label","io.xur.name="+volume.Name,id],20);
                    if(created.ExitCode!=0)throw new IOException("Could not create volume.");
                }
                await VerifyVolume(id);mounts.Add(new(id,volume.Destination,volume.ReadOnly));
            }
            var recipe=Recipe(request,"container-"+job.Id,identity,mounts.ToArray());ProfilePolicy.ValidateRecipe(recipe);
            Directory.CreateDirectory(selected);var path=Path.Combine(selected,recipe.Id+".json");
            using(var f=new FileStream(path+".tmp",new FileStreamOptions{Mode=FileMode.Create,Access=FileAccess.Write,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite})){JsonSerializer.Serialize(f,recipe,json);f.Flush(true);}File.Move(path+".tmp",path);
            job=job with {RecipeId=recipe.Id};Stage("Ready",log);
        }
        catch(Exception e){Stage("Failed",e is InvalidOperationException or IOException?Redaction.Logs(e.Message):"Container preparation failed.");}
        finally{Directory.Delete(context,true);}
    }
    public static async Task VerifyVolume(string id)
    {
        if(!Regex.IsMatch(id,@"^xur-volume-[a-f0-9]{32}$"))throw new InvalidOperationException("Invalid volume identity.");
        var result=await Processes.Run("podman",["volume","inspect",id],10);if(result.ExitCode!=0)throw new InvalidOperationException("A persistent volume is missing.");
        using var doc=JsonDocument.Parse(result.Output);if(doc.RootElement[0].GetProperty("Labels").GetProperty("io.xur.volume").GetString()!=id)throw new InvalidOperationException("Volume ownership changed.");
    }
    public static async Task<ContainerVolume[]> Volumes()
    {
        var result=await Processes.Run("podman",["volume","ls","--filter","label=io.xur.volume","--format","json"],15);if(result.ExitCode!=0)throw new IOException("Volume inventory unavailable.");
        using var doc=JsonDocument.Parse(result.Output);var volumes=new List<ContainerVolume>();
        foreach(var v in doc.RootElement.EnumerateArray())
        {
            var id=v.GetProperty("Name").GetString()!;var path=v.GetProperty("Mountpoint").GetString()!;
            if(!Regex.IsMatch(id,@"^xur-volume-[a-f0-9]{32}$") || !path.StartsWith("/var/lib/containers/storage/volumes/",StringComparison.Ordinal))continue;
            var size=await Processes.Run("du",["--summarize","--one-file-system","--block-size=1","--",path],20);long? bytes=size.ExitCode==0&&long.TryParse(size.Output.Split('\t')[0],out var n)?n:null;
            volumes.Add(new(id,v.GetProperty("Labels").GetProperty("io.xur.name").GetString()??id,path,bytes,[]));
        }
        return volumes.ToArray();
    }
}
