namespace Xur.Domain;
public record GpuOwner(int Pid,string Name,string? Service,string[] Devices,bool Blocking,string Role);
public record GpuLink(string From,string To,string State,int? LinkCount,double? SpeedGBps,string Connection);
public record GpuTopologyProbe(string[] Arguments,int ExitCode,string Output);
public record GpuTopology(DateTimeOffset CapturedAt,GpuLink[] Links,string? Error=null,string State="Unknown",GpuTopologyProbe[]? Probes=null);
