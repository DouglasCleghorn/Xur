namespace Xur.Domain;
public record UsbLogVolume(string Id,string Path,string Label,string Model,bool InstallerMedia);
public record UsbLogRequest(string Id);
public record UsbLogReceipt(string Message);
