using System.Net.Http.Json;
using System.Text.Json;
using Xur.Domain;
namespace Xur.Control;

public sealed class ConsoleWifi(HttpClient client,bool local=false)
{
    string Prefix=>local?"/local":"";
    WifiStatus? status;WifiAdapter? adapter;WifiNetwork[] networks=[];WifiNetwork? selected;
    string view="adapters",notice="";
    public bool Closed {get;private set;}
    public ConsoleScreen Screen
    {
        get
        {
            var options=new List<ConsoleOption>();string body;string? input=null;var secret=false;
            if(view=="password")
            {
                body=$"{adapter!.Interface} · {LocalConsole.Clean(selected!.Ssid)}\nEnter the Wi-Fi password. It will be saved for automatic reconnection.\nEnter: Connect | Escape: Cancel";
                input="";secret=true;options.Add(new('0',"Cancel"));
            }
            else if(view=="networks")
            {
                body=$"Adapter: {adapter!.Interface}\nChoose a network. Secured networks prompt for a password.\n"+(networks.Length==0?"No visible networks found. Move closer or scan again. Hidden networks require separate configuration.":"");
                for(var i=0;i<networks.Length;i++)
                {
                    var n=networks[i];options.Add(new((char)(256+i),$"{LocalConsole.Clean(n.Ssid)} · {n.Signal}% · {(n.KeyManagement=="open"?"Open":n.Security)}",n.Supported));
                }
                if(networks.Any(n=>!n.Supported))body+="\nEnterprise and WEP networks require separate configuration.";
                options.AddRange([new('v',"Scan again"),new('0',status?.Adapters.Length>1?"Back to adapters":"Back to network settings")]);
            }
            else
            {
                body=status==null?"Could not read Wi-Fi adapters. Refresh to retry.":status.Adapters.Length==0?"No Wi-Fi adapters found. Check the adapter and driver.":!status.HardwareEnabled?"Wi-Fi is blocked by a hardware switch or airplane mode. Unblock it and refresh.":!status.Enabled?"Wi-Fi is turned off.":"Choose a Wi-Fi adapter.";
                if(status is {HardwareEnabled:true,Enabled:false})options.Add(new('e',"Enable Wi-Fi"));
                if(status!=null)for(var i=0;i<status.Adapters.Length;i++)options.Add(new((char)(256+i),$"{status.Adapters[i].Interface} · {status.Adapters[i].MacAddress} · {status.Adapters[i].State}",status.Enabled&&status.HardwareEnabled));
                options.AddRange([new('v',"Refresh adapters"),new('0',"Back to network settings")]);
            }
            return new("wifi-"+view,"Wi-Fi setup",LocalConsole.Clean((notice.Length>0?notice+"\n\n":"")+body),options.ToArray(),input,secret);
        }
    }
    public async Task Open(){Closed=false;notice="";view="adapters";await LoadAdapters();}
    async Task LoadAdapters()
    {
        try
        {
            status=await client.GetFromJsonAsync<WifiStatus>(Prefix+"/network/wifi");
            if(status is {Enabled:true,HardwareEnabled:true,Adapters.Length:1}){adapter=status.Adapters[0];await Scan();}
        }
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException or JsonException){status=null;notice="Could not read Wi-Fi adapters. Refresh to retry.";}
    }
    async Task Scan()
    {
        var response=await Send("/scan",new WifiScanRequest(adapter!.Interface,adapter.MacAddress));
        if(response==null)return;
        using(response)
        {
            networks=(await response.Content.ReadFromJsonAsync<WifiNetwork[]>()??[]).GroupBy(n=>(n.Ssid,n.KeyManagement)).Select(g=>g.OrderByDescending(n=>n.Signal).First()).Take(200).ToArray();view="networks";
        }
    }
    public async Task Select(char key)
    {
        if(Screen.Options.FirstOrDefault(o=>o.Key==key)?.Enabled!=true)return;
        notice="";
        if(key=='0')
        {
            if(view=="password"){selected=null;view="networks";}
            else if(view=="networks"&&status?.Adapters.Length>1)view="adapters";
            else Closed=true;
            return;
        }
        if(view=="adapters")
        {
            if(key=='e'){using var enabled=await Send("/enable",new{});if(enabled!=null)await LoadAdapters();}
            else if(key=='v')await LoadAdapters();
            else{adapter=status!.Adapters[key-256];await Scan();}
        }
        else if(view=="networks")
        {
            if(key=='v'){await Scan();return;}
            selected=networks[key-256];
            if(selected.NeedsPassword)view="password";
            else await Connect("");
        }
    }
    public async Task Submit(string password){if(view=="password")await Connect(password);}
    async Task Connect(string password)
    {
        using var response=await Send("/connect",new WifiConnectRequest(adapter!.Interface,adapter.MacAddress,selected!.Ssid,selected.Bssid,selected.KeyManagement,password));
        if(response==null)return;
        notice="Connected to "+LocalConsole.Clean(selected.Ssid)+". Saved for reboot and installation.";view="networks";selected=null;
    }
    async Task<HttpResponseMessage?> Send(string path,object request)
    {
        try
        {
            var response=await client.PostAsJsonAsync(Prefix+"/network/wifi"+path,request);
            if(response.IsSuccessStatusCode)return response;
            using(response)
            {
                var error=await response.Content.ReadFromJsonAsync<JsonElement>();
                notice=error.ValueKind==JsonValueKind.Object && error.TryGetProperty("error",out var message)&&message.ValueKind==JsonValueKind.String?message.GetString()!:"Wi-Fi request failed. Refresh and retry.";
            }
        }
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException or JsonException){notice="Wi-Fi request interrupted. Refresh status before trying again.";}
        return null;
    }
}
