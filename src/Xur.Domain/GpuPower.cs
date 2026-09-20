namespace Xur.Domain;
public record GpuPowerCard(string Pci,string Identity,string Name,string Vendor,double? CurrentWatts,double? MinimumWatts,double? MaximumWatts,double? DefaultWatts,double? SavedWatts,bool CanChange,string? Message,bool HasSavedSetting=false);
public record GpuPowerRequest(string Pci,string Identity,double? Watts);
public record GpuPowerSnapshot(GpuPowerCard[] Cards);
