using System.Net.Http.Json;
using System.Text.Json;
using Xur.Domain;
namespace Xur.Control;

public sealed class ConsoleNetwork(HttpClient client,bool local=false)
{
    readonly ConsoleWifi wifi=new(client,local);
    bool wifiOpen;
    NetworkSettingsStatus? status;
    NetworkConfiguration? draft;
    string view="list",editField="",notice="";
    bool ipv6;
    string Prefix=>local?"/local":"";
    public bool Closed {get;private set;}
    IpConfiguration Family=> (ipv6?draft!.Ipv6:draft!.Ipv4)??new();
    void FamilySet(IpConfiguration value){draft=ipv6?draft! with{Ipv6=value}:draft! with{Ipv4=value};}
    public ConsoleScreen Screen
    {
        get
        {
            if(wifiOpen)return wifi.Screen;
            var options=new List<ConsoleOption>();string title,body;string? input=null;
            if(view=="list")
            {
                title="Network settings";body=status==null?"Could not load wired adapters. Refresh to retry.":"Select a wired adapter. Settings persist after reboot and installation.\n"+string.Join('\n',status.Devices.Select(d=>d.Interface+": "+string.Join(", ",d.Addresses)));
                var busy=status?.Pending?.Stage is "Applying" or "Confirm";
                if(status?.Pending is {} pending)
                {
                    body=$"{pending.Interface}: {pending.Stage}\n{pending.Message}\n"+string.Join('\n',pending.Addresses)+"\n\n"+body;
                    if(busy){options.Add(new('k',"Keep settings",pending.Stage=="Confirm"));options.Add(new('r',"Revert",pending.Stage=="Confirm"));}
                }
                if(status!=null)for(var i=0;i<status.Devices.Length;i++)options.Add(new((char)(256+i),status.Devices[i].Interface+" · "+status.Devices[i].MacAddress,status.Devices[i].Editable&&!busy));
                options.AddRange([new('w',"Wi-Fi setup",!busy),new('v',"Refresh status"),new('0',"Back to menu")]);
            }
            else if(view=="adapter")
            {
                title=draft!.Interface+" · IP settings";
                string Describe(IpConfiguration? ip)=>ip==null?"Automatic":ip.Method+"\n  "+string.Join(", ",ip.Addresses??[])+"\n  Gateway: "+(string.IsNullOrEmpty(ip.Gateway)?"None":ip.Gateway)+"\n  DNS: "+string.Join(", ",ip.Dns??[]);
                body=$"MAC: {draft.MacAddress}\nIPv4: {Describe(draft.Ipv4)}\nIPv6: {Describe(draft.Ipv6)}\n\nApply, then keep within two minutes or the previous connection returns.";
                options.AddRange([new('4',"IPv4 settings"),new('6',"IPv6 settings"),new('a',"Apply network settings"),new('0',"Back to adapters")]);
            }
            else
            {
                title=draft!.Interface+" · "+(ipv6?"IPv6":"IPv4");
                body="Mode: "+Family.Method+"\nAddresses: "+string.Join(", ",Family.Addresses??[])+"\nGateway: "+Family.Gateway+"\nDNS: "+string.Join(", ",Family.Dns??[]);
                if(view=="editField")
                {
                    input=editField switch{"addresses"=>string.Join(",",Family.Addresses??[]),"gateway"=>Family.Gateway??"",_=>string.Join(",",Family.Dns??[])};
                    body=editField switch{"addresses"=>"Enter addresses with prefix length, separated by commas.\nExample: "+(ipv6?"2001:db8:1::10/64":"192.0.2.10/24"),"gateway"=>"Enter the gateway address, or leave blank for none.",_=>"Enter DNS server IPs, separated by commas, or leave blank."};
                    body+="\nEnter: Save field | Esc: Cancel | Backspace: Delete";
                    options.Add(new('0',"Cancel field edit"));
                }
                else options.AddRange([new('a',"Automatic"),new('s',"Static"),new('d',"Disabled"),new('i',"Edit addresses",Family.Method=="manual"),new('g',"Edit gateway",Family.Method=="manual"),new('z',"Edit DNS",Family.Method!="disabled"),new('0',"Back to adapter")]);
            }
            if(notice.Length>0)body=notice+"\n\n"+body;
            return new("network-"+view+(view=="editField"?"-"+editField:""),title,body,options.ToArray(),input);
        }
    }
    public async Task Open(){wifiOpen=false;Closed=false;view="list";notice="";await Refresh();}
    public async Task Refresh()
    {
        if(wifiOpen){await wifi.Refresh();return;} // Complete a pending scan without reordering existing choices.
        try{status=await client.GetFromJsonAsync<NetworkSettingsStatus>(Prefix+"/network/settings");}
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException or JsonException){status=null;}
    }
    public async Task Select(char key)
    {
        if(wifiOpen){await wifi.Select(key);if(wifi.Closed){wifiOpen=false;await Refresh();}return;}
        var option=Screen.Options.FirstOrDefault(o=>o.Key==key);if(option?.Enabled!=true)return;notice="";
        if(key=='0')
        {
            if(view=="list")Closed=true;
            else if(view=="adapter")view="list";
            else if(view=="editField")view="family";
            else view="adapter";
            return;
        }
        if(view=="list")
        {
            if(key=='w'){wifiOpen=true;await wifi.Open();return;}
            if(key=='v'){await Refresh();return;}
            if(key is 'k' or 'r'){await Send("/network/"+(key=='k'?"keep":"revert"),new NetworkChangeRequest(status!.Pending!.Id));return;}
            var device=status!.Devices[key-256];draft=new(device.Interface,device.MacAddress,device.Ipv4,device.Ipv6);view="adapter";return;
        }
        if(view=="adapter")
        {
            if(key is '4' or '6'){ipv6=key=='6';view="family";return;}
            try{var config=NetworkValidation.Validate(draft!);if(await Send("/network/settings",config))view="list";}
            catch(InvalidOperationException e){notice=e.Message;}
            return;
        }
        if(key=='a'){FamilySet(Family with {Method="auto",Addresses=[],Gateway=""});return;}
        if(key=='d'){FamilySet(new("disabled"));return;}
        if(key=='s'){FamilySet(Family with {Method="manual"});editField="addresses";view="editField";return;}
        editField=key switch{'i'=>"addresses",'g'=>"gateway",_=>"dns"};view="editField";
    }
    public async Task Submit(string text)
    {
        if(wifiOpen){await wifi.Submit(text);return;}
        if(view!="editField")return;
        var values=text.Split([',',' ','\r','\n'],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        FamilySet(editField switch{"addresses"=>Family with{Addresses=values},"gateway"=>Family with{Gateway=text.Trim()},_=>Family with{Dns=values}});view="family";
    }
    async Task<bool> Send(string path,object value)
    {
        try
        {
            using var response=await client.PostAsJsonAsync(Prefix+path,value);
            if(!response.IsSuccessStatusCode)
            {
                var json=await response.Content.ReadFromJsonAsync<JsonElement>();notice=json.TryGetProperty("error",out var error)?error.GetString()??"Network change failed.":"Network change failed.";return false;
            }
            await Refresh();return true;
        }
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException or JsonException){notice="Connection interrupted. Refresh status before trying again.";return false;}
    }
}
