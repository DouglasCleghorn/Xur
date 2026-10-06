using System.Net.Http.Json;
using System.Text.Json;
using Xur.Domain;
namespace Xur.Control;

public sealed class ConsoleDisplays(HttpClient client,bool local=false)
{
    DisplayPowerStatus? status;ConnectedDisplay? selected;string notice="";int page;
    public bool Closed {get;private set;}
    string Prefix=>local?"/local":"";
    ConnectedDisplay[] Choices=>status?.Displays.Skip(page*6).Take(6).ToArray()??[];
    public ConsoleScreen Screen
    {
        get
        {
            var options=new List<ConsoleOption>();string body;
            if(selected!=null)
            {
                body=selected.Name+" · "+selected.Connector+"\n"+selected.Power+"\n\nCEC screen off puts the TV in standby. Workloads keep running.\nPress a controller button or key to wake a screen turned off through Xur.";
                if(!selected.CanControl)body+="\n\n"+selected.UnavailableReason;
                options.AddRange([new('s',"CEC screen off",selected.CanControl),new('w',"CEC screen on",selected.CanControl),new('0',"Back to displays")]);
            }
            else
            {
                body=status==null?"Display controls unavailable. Refresh to retry.":status.Displays.Length==0?"No connected console displays found.":"Choose a console display. Workstation displays are controlled in their profile picker or the web manager.";
                for(var i=0;i<Choices.Length;i++)options.Add(new((char)('1'+i),Choices[i].Name+" · "+Choices[i].Connector));
                if((status?.Displays.Length??0)>(page+1)*6)options.Add(new('d',"Next displays"));
                if(page>0)options.Add(new('b',"Previous displays"));
                options.AddRange([new('v',"Refresh displays"),new('0',"Back to power")]);
            }
            return new("displays","Display power",notice.Length>0?notice+"\n\n"+body:body,options.ToArray());
        }
    }
    public async Task Open(){Closed=false;selected=null;page=0;notice="";await Refresh();}
    public async Task Refresh()
    {
        try{status=await client.GetFromJsonAsync<DisplayPowerStatus>(Prefix+"/displays"+(local?"":"?consoleOnly=true"));if(selected!=null)selected=status?.Displays.FirstOrDefault(d=>d.Id==selected.Id);}
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException or JsonException){status=null;selected=null;notice="Display controls unavailable. Refresh to retry.";}
    }
    public async Task Select(char key)
    {
        if(Screen.Options.FirstOrDefault(o=>o.Key==key)?.Enabled!=true)return;
        if(key=='0'){if(selected!=null)selected=null;else Closed=true;return;}
        if(key=='v'){notice="";await Refresh();return;}
        if(key is 'd' or 'b'){page+=key=='d'?1:-1;return;}
        if(selected==null){selected=Choices[key-'1'];notice="";return;}
        try
        {
            using var response=await client.PostAsJsonAsync(Prefix+"/displays/power",new DisplayPowerRequest(selected.Id,key=='s'?"off":"on",ConsoleOnly:true));
            var data=await response.Content.ReadFromJsonAsync<JsonElement>();
            notice=data.TryGetProperty(response.IsSuccessStatusCode?"message":"error",out var message)?message.GetString()??"CEC request finished.":"CEC request failed.";
            await Refresh();
        }
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException or JsonException){notice="CEC request interrupted. Refresh before retrying.";}
    }
}
