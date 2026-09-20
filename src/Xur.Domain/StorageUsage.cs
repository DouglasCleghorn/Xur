namespace Xur.Domain;
public record StorageFilesystem(string Source,string Type,string[] Mounts,long Bytes,long Used,long Available);
public record StorageDiskUsage(string Path,string Model,long Bytes,string[] Filesystems,string[] Mounts);
public record StorageCategory(string Name,string[] Paths,long? Bytes,string? Error=null);
public record StorageUsageSnapshot(DateTimeOffset? CapturedAt,bool Scanning,StorageFilesystem[] Filesystems,StorageDiskUsage[] Disks,StorageCategory[] Categories,string? Error=null,ContainerVolume[]? Volumes=null);
