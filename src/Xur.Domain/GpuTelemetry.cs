namespace Xur.Domain;
public record GpuReading(DateTimeOffset At,double? Utilization=null,double? MemoryUsedMiB=null,double? MemoryTotalMiB=null,double? MemoryFreeMiB=null,double? PowerWatts=null,double? PowerLimitWatts=null,double? TemperatureC=null,double? FanPercent=null,double? FanRpm=null,double? GraphicsClockMHz=null,double? MemoryClockMHz=null,string? PerformanceState=null);
public record GpuProcess(int Pid,string Name,double? MemoryMiB);
public record GpuTelemetry(GpuDevice Device,string? DriverVersion,GpuReading Reading,GpuReading[] History,GpuProcess[] Processes);
public record GpuTelemetrySnapshot(DateTimeOffset? CapturedAt,GpuTelemetry[] Gpus,string? Error=null,GpuTopology? Topology=null);
