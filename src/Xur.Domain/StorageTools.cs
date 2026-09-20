namespace Xur.Domain;
public record StorageMount(string Id,string Path,string Source,string Type,long Bytes,long Used,long Available,bool Ssd,bool TrimSupported,bool ReadOnly,string? Uuid=null);
public record TrimRequest(string Id);
public record TrimResult(string Id,DateTimeOffset StartedAt,DateTimeOffset? FinishedAt,bool? Success,string Message);
public record TrimStatus(bool Busy,string Timer,string LastScheduledRun,StorageMount[] Mounts,TrimResult[] Results);
