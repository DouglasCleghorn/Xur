namespace Xur.Domain;
public record WifiAdapter(string Interface,string MacAddress,string State,string? Connection,string DevicePath);
public record WifiStatus(bool Enabled,bool HardwareEnabled,WifiAdapter[] Adapters);
public record WifiNetwork(string Ssid,string Bssid,string Security,int Signal,string KeyManagement)
{
    public bool Supported=>KeyManagement is "open" or "wpa-psk" or "sae" or "owe";
    public bool NeedsPassword=>KeyManagement is "wpa-psk" or "sae";
}
public record WifiScanRequest(string Interface,string MacAddress);
public record WifiConnectRequest(string Interface,string MacAddress,string Ssid,string Bssid,string KeyManagement,string Password);
