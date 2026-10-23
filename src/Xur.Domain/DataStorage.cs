namespace Xur.Domain;

public record StorageLocation(string? Uuid,string Path);
public record DataStorageConfig(string Installation,string? DefaultUuid,Dictionary<string,string> Overrides,Dictionary<string,StorageLocation> Locations);
public record DataStorageRequest(string Group,string Destination);
public record DataStorageJob(string Id,string Stage,string Message,DateTimeOffset Updated,string[] RetainedPaths);
public record DataStorageGroup(string Id,string Name,string Selection,string[] Paths,long? Available,string? Error);
public record DataStorageStatus(string DefaultSelection,StorageMount[] Drives,DataStorageGroup[] Groups,bool Busy,DataStorageJob? Job);
