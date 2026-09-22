using System.Text;
using System.Text.RegularExpressions;
using Xur.Domain;
namespace Xur.Agent;

public sealed partial class NetworkSettings
{
    // nmcli terse fields escape both ':' and '\\'. Never split an SSID on a raw colon.
    public static string[] WifiFields(string line)
    {
        var fields=new List<string>();var value=new StringBuilder();var escaped=false;
        foreach(var c in line)
        {
            if(escaped){value.Append(c);escaped=false;}
            else if(c=='\\')escaped=true;
            else if(c==':'){fields.Add(value.ToString());value.Clear();}
            else value.Append(c);
        }
        if(escaped)value.Append('\\');fields.Add(value.ToString());return fields.ToArray();
    }
    public async Task<WifiStatus> ReadWifi()
    {
        var radio=WifiFields(await Nm("-g","WIFI,WIFI-HW","general"));
        var adapters=new List<WifiAdapter>();
        foreach(var line in Lines(await Nm("-t","-f","DEVICE,TYPE","device","status")))
        {
            var fields=WifiFields(line);
            if(fields.Length!=2 || fields[1]!="wifi" || !Regex.IsMatch(fields[0],@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,14}$"))continue;
            var name=fields[0];var values=(await Nm("--escape","no","-g","GENERAL.STATE,GENERAL.CON-UUID,GENERAL.DBUS-PATH","device","show",name)).Split('\n');
            if(values.Length<3 || !Regex.IsMatch(values[2],@"^/org/freedesktop/NetworkManager/Devices/\d+$"))continue;
            // Scanning can randomize the current MAC; bind saved profiles to the permanent one.
            var hardware=await Check("busctl",["get-property",Bus,values[2],Bus+".Device.Wireless","PermHwAddress"]);
            var mac=Regex.Match(hardware,"^s \"([0-9A-Fa-f:]{17})\"$").Groups[1].Value.ToUpperInvariant();
            if(mac.Length==0)continue;
            string[] detail=[];
            try{detail=(await Nm("--escape","no","-g","GENERAL.PRODUCT,GENERAL.DRIVER,GENERAL.FIRMWARE-VERSION,GENERAL.FIRMWARE-MISSING,GENERAL.REASON","device","show",name)).Split('\n');}catch{}
            adapters.Add(new(name,mac,values[0],Guid.TryParse(values[1],out _)?values[1]:null,values[2],detail.ElementAtOrDefault(0)??"",detail.ElementAtOrDefault(1)??"",detail.ElementAtOrDefault(2)??"",detail.ElementAtOrDefault(3)=="yes",detail.ElementAtOrDefault(4)??""));
        }
        return new(radio.ElementAtOrDefault(0)=="enabled",radio.ElementAtOrDefault(1)=="enabled",adapters.ToArray());
    }
    public async Task EnableWifi(){await Nm("radio","wifi","on");}
    async Task<WifiAdapter> WifiAdapterFor(string name,string mac)
    {
        var status=await ReadWifi();
        if(!status.HardwareEnabled)throw new InvalidOperationException("Wi-Fi is blocked by a hardware switch or airplane mode. Unblock it and refresh.");
        if(!status.Enabled)throw new InvalidOperationException("Enable Wi-Fi before scanning.");
        var matches=status.Adapters.Where(a=>a.Interface==name && a.MacAddress==mac).ToArray();
        if(matches.Length!=1)throw new InvalidOperationException("The Wi-Fi adapter changed or was removed. Refresh the adapter list.");
        if(matches[0].FirmwareMissing)throw new InvalidOperationException("Firmware is missing for "+matches[0].Interface+" ("+matches[0].Model+", driver "+matches[0].Driver+"). Use a wired connection or an installer with firmware for this adapter.");
        if(matches[0].State.StartsWith("10 "))throw new InvalidOperationException("This Wi-Fi adapter is unmanaged by NetworkManager. Check its network configuration.");
        if(matches[0].UnavailableMessage.Length>0)throw new InvalidOperationException(matches[0].UnavailableMessage+" NetworkManager reason: "+matches[0].Reason+" Check the radio switch and NetworkManager/wpa_supplicant messages in Logs, then refresh the adapter.");
        return matches[0];
    }
    public static WifiNetwork[] ParseWifiNetworks(string output)
    {
        var networks=new List<WifiNetwork>();
        foreach(var line in output.Split('\n',StringSplitOptions.RemoveEmptyEntries))
        {
            var f=WifiFields(line);if(f.Length!=4 || !Regex.IsMatch(f[0],@"^(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$") || f[1].Length==0 || f[1].Any(char.IsControl) || Encoding.UTF8.GetByteCount(f[1])>32)continue;
            var security=f[2];
            var kind=security.Contains("802.1X",StringComparison.OrdinalIgnoreCase)||security.Contains("EAP",StringComparison.OrdinalIgnoreCase)?"unsupported":
                security.Contains("WPA2")?"wpa-psk":security.Contains("WPA3")?"sae":security.Contains("OWE")?"owe":security is "" or "--"?"open":"unsupported";
            networks.Add(new(f[1],f[0].ToUpperInvariant(),security,int.TryParse(f[3],out var signal)?Math.Clamp(signal,0,100):0,kind));
        }
        return networks.OrderByDescending(n=>n.Signal).ToArray();
    }
    public async Task<WifiNetwork[]> ScanWifi(WifiScanRequest request,bool rescan=true)
    {
        var adapter=await WifiAdapterFor(request.Interface,request.MacAddress);
        async Task<long?> LastScan()
        {
            try{var result=await Check("busctl",["get-property",Bus,adapter.DevicePath,Bus+".Device.Wireless","LastScan"],5);return long.TryParse(result.Split(' ').Last(),out var value)?value:null;}
            catch{return null;}
        }
        var before=rescan?await LastScan():null;
        for(var attempt=0;attempt<(rescan?3:1);attempt++)
        {
            var output=await Check("nmcli",["--wait","20","--colors","no","-t","--escape","yes","-f","BSSID,SSID,SECURITY,SIGNAL","device","wifi","list","ifname",request.Interface,"--rescan",rescan&&attempt==0?"yes":"no"],30);
            var networks=ParseWifiNetworks(output);
            if(networks.Length>0)return networks;
            if(output.Length>0&&!output.Split('\n').Any(line=>WifiFields(line) is {Length:4} fields&&Regex.IsMatch(fields[0],@"^(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$")))
                throw new InvalidOperationException("NetworkManager returned an unreadable Wi-Fi list. Check network diagnostics; this is not an empty scan.");
            if(rescan&&attempt<2)await Task.Delay(1000);
        }
        await WifiAdapterFor(request.Interface,request.MacAddress);
        var after=rescan?await LastScan():null;
        if(rescan&&after.HasValue&&(after.Value<0||before.HasValue&&after<=before))
            throw new InvalidOperationException("The Wi-Fi scan has not completed. Wait a moment and scan again. If this continues, check the adapter driver and firmware in Logs.");
        return [];
    }
    public async Task<NetworkChange> ConnectWifi(WifiConnectRequest request)
    {
        await gate.WaitAsync();try
        {
            await Expire();if(Pending() is {Stage:"Applying" or "Confirm"})throw new InvalidOperationException("Keep or revert the pending network change first.");
            var adapter=await WifiAdapterFor(request.Interface,request.MacAddress);
            var network=(await ScanWifi(new(adapter.Interface,adapter.MacAddress),false)).FirstOrDefault(n=>n.Ssid==request.Ssid && n.Bssid==request.Bssid && n.KeyManagement==request.KeyManagement);
            if(network==null)throw new InvalidOperationException("The selected network changed or is no longer visible. Scan again.");
            if(!network.Supported)throw new InvalidOperationException("This menu supports open, WPA2-Personal and WPA3-Personal networks. Enterprise and WEP need separate configuration.");
            var password=request.Password??"";
            if(network.NeedsPassword && (password.Any(c=>c<' ' || c>'~') || (network.KeyManagement=="sae"?password.Length is <1 or >63:!(password.Length is >=8 and <=63 || password.Length==64 && password.All(Uri.IsHexDigit)))))
                throw new InvalidOperationException(network.KeyManagement=="sae"?"Enter a Wi-Fi password of 1–63 printable characters.":"Enter 8–63 printable characters, or a 64-digit hexadecimal WPA key.");
            if(!network.NeedsPassword && password.Length>0)throw new InvalidOperationException("This network does not require a password.");
            var id=Guid.NewGuid().ToString("N");var uuid=Guid.NewGuid().ToString();
            var args=new List<string>{"--offline","connection","add","type","wifi","con-name","xur-network-"+id,"connection.uuid",uuid,"ifname","*","wifi.ssid",network.Ssid,"wifi.mac-address",adapter.MacAddress,"connection.autoconnect","no","connection.autoconnect-priority","999","ipv4.method","auto","ipv6.method","auto"};
            if(network.KeyManagement!="open")args.AddRange(["wifi-sec.key-mgmt",network.KeyManagement]);
            var keyfile=await Nm(args.ToArray());
            // Generate the public profile with nmcli, then write its secret directly to a
            // root-only keyfile. Passwords never enter process arguments or error messages.
            if(network.NeedsPassword)keyfile=keyfile.Replace("[wifi-security]\n","[wifi-security]\npsk="+password.Replace("\\","\\\\").Replace(" ","\\s")+"\npsk-flags=0\n");
            var checkpoint=await BusCall("CheckpointCreate","aouu","1",adapter.DevicePath,"120","0");
            var path=Regex.Match(checkpoint,"^o \"(/org/freedesktop/NetworkManager/Checkpoint/[0-9]+)\"$").Groups[1].Value;
            if(path.Length==0)throw new InvalidOperationException("Could not create a Wi-Fi recovery checkpoint.");
            var change=new NetworkChange(id,adapter.Interface,uuid,adapter.Connection,path,DateTimeOffset.UtcNow.AddSeconds(120),"Applying","Connecting to Wi-Fi…",[]);
            var filename=Path.Combine(profilesDirectory,"xur-network-"+id+".nmconnection");
            try
            {
                Save(change);Directory.CreateDirectory(profilesDirectory);
                using(var file=new FileStream(filename,new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite}))
                using(var writer=new StreamWriter(file))await writer.WriteAsync(keyfile+"\n");
                await Check("restorecon",[filename]);
                await Nm("connection","load",filename);
                await Activate(change);await KeepUnlocked(id);
                return Pending()!;
            }
            catch
            {
                try{if(Pending()?.Stage is "Applying" or "Confirm")await Rollback(change,"Wi-Fi connection failed. The previous connection was restored.");}
                catch{ /* The NetworkManager checkpoint still owns its timeout rollback. */ }
                finally{File.Delete(filename);}
                throw new InvalidOperationException("Could not connect to Wi-Fi. Check the password and signal, then retry. Review network status if the previous connection did not return.");
            }
        }finally{gate.Release();}
    }
}
