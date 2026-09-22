namespace Xur.Domain;
public record WifiAdapter(string Interface,string MacAddress,string State,string? Connection,string DevicePath,string Model="",string Driver="",string Firmware="",bool FirmwareMissing=false,string Reason="")
{
    public string UnavailableMessage=>FirmwareMissing?"Firmware is missing for this Wi-Fi adapter.":
        State.StartsWith("10 ")||State=="unmanaged"?"This Wi-Fi adapter is unmanaged by NetworkManager.":
        State.StartsWith("20 ")||State=="unavailable"?"This Wi-Fi adapter is unavailable. It cannot scan yet.":"";
}
public record WifiStatus(bool Enabled,bool HardwareEnabled,WifiAdapter[] Adapters);
public record WifiNetwork(string Ssid,string Bssid,string Security,int Signal,string KeyManagement)
{
    public bool Supported=>KeyManagement is "open" or "wpa-psk" or "sae" or "owe";
    public bool NeedsPassword=>KeyManagement is "wpa-psk" or "sae";
}
public record WifiScanRequest(string Interface,string MacAddress);
public record WifiConnectRequest(string Interface,string MacAddress,string Ssid,string Bssid,string KeyManagement,string Password);
