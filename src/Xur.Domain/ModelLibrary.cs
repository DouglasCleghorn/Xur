namespace Xur.Domain;
public record ModelFolder(string Name,string Path,string Format,long Bytes,int Files,string Source,string? DeletePath=null,string? DeleteUnavailableReason=null);
public record ModelDeleteRequest(string Path,string DeletePath,bool Confirm=false);
public record ModelScanStatus(DateTimeOffset? CapturedAt,bool Scanning,ModelFolder[] Models,string[] Searched,string[] Warnings);
public record NetworkPoint(DateTimeOffset At,double? ReceiveBytesPerSecond,double? SendBytesPerSecond,long ReceivedBytes,long SentBytes);
public record NetworkAdapterUsage(string Name,string State,NetworkPoint Reading,NetworkPoint[] History);
public record NetworkUsageSnapshot(DateTimeOffset CapturedAt,NetworkAdapterUsage[] Adapters,string? Error=null);
