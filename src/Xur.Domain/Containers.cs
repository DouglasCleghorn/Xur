namespace Xur.Domain;
public record ContainerMount(string Volume,string Destination,bool ReadOnly=false);
public record ContainerConfiguration(ContainerMount[] Mounts,Dictionary<string,string> Environment);
public record ContainerVolumeRequest(string Name,string Destination,string? Volume=null,bool ReadOnly=false);
public record ContainerBuildFile(string Name,string Content);
public record ContainerPrepareRequest(string? Image=null,string? Dockerfile=null,ContainerBuildFile[]? Files=null,string? Name=null,int Port=8080,string HealthPath="/",string Vendor="CPU",int GpuCount=0,string[]? Command=null,Dictionary<string,string>? Environment=null,ContainerVolumeRequest[]? Volumes=null);
public record ContainerJob(string Id,string Stage,string Log,string? RecipeId,DateTimeOffset Updated);
public record ContainerVolume(string Id,string Name,string Path,long? Bytes,string[] Workloads);
