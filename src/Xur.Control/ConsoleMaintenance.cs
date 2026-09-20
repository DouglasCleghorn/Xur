using System.Net.Http.Json;
using System.Text.Json;
using Xur.Domain;

namespace Xur.Control;

public record ConsoleOption(char Key,string Label,bool Enabled=true)
{
    public string Display => Label+(Enabled ? "" : " (unavailable)");
}
public record ConsoleScreen(string Id,string Title,string Body,ConsoleOption[] Options,string? InputValue=null);

// Shared by the physical/serial console and the interactive `xur` command.
public sealed class ConsoleMaintenance(HttpClient client,bool installer=false,bool local=false)
{
    readonly SemaphoreSlim gate=new(1,1);
    readonly ConsoleNetwork network=new(client,local);
    string view="updates",notice="",power="",returnView="power";
    ApplicationUpdateStatus? application;
    OsUpdateStatus? os;
    UpdateAllStatus? all;
    bool Busy => all?.Busy==true || application?.Busy==true || os?.Busy==true;
    bool Ready => !installer && all!=null && application!=null && os!=null && !Busy;
    bool RebootRequired => os?.Pending!=null || os?.RollbackQueued==true;
    public bool Closed {get;private set;}

    public ConsoleScreen Screen
    {
        get
        {
            if(view=="network")return network.Screen;
            var options=new List<ConsoleOption>();string title,body;
            switch(view)
            {
                case "power":
                    title="Power";body="Rebooting or shutting down stops all running workstations and AI services.";
                    if(Busy)body+="\nWait for the current update to finish.";
                    options.AddRange([new('r',"Reboot",!Busy),new('s',"Shut down",!Busy),new('0',"Back to menu")]);
                    break;
                case "confirm":
                    title=power=="reboot" ? "Confirm reboot" : "Confirm shut down";
                    body="All running workstations and AI services will stop. Unsaved work may be lost.";
                    if(Busy)body+="\nWait for the current update to finish.";
                    options.AddRange([new('0',"Cancel"),new('y',power=="reboot"?"Reboot now":"Shut down now",!Busy)]);
                    break;
                case "application":
                    title="Xur updates";
                    body=application==null ? "Could not load Xur update status. Refresh to retry." :
                        $"Installed: {application.Current.Version}\nAvailable: {application.Available?.Version ?? "Not checked / unavailable"}\nChannel: {Channel}\nServer: {application.Server}\n"+
                        $"{application.Operation?.Stage ?? "Idle"}: {application.Operation?.Message}\nThe manager briefly reconnects during activation.";
                    options.AddRange([new('e',"Check for updates",Ready && application?.Server.Length>0),
                        new('f',"Update Xur",Ready && application?.Available!=null && application.Available.Id!=application.Current.Id)]);
                    if(application?.Previous!=null)options.Add(new('g',"Roll back Xur",Ready));
                    options.AddRange([new('v',"Refresh status"),new('0',"Back to updates")]);
                    break;
                case "os":
                    title="OS updates";
                    body=os==null ? "Could not load OS update status. Refresh to retry." :
                        $"Installed: {os.Current?.Version ?? "Unknown"}\nAvailable: {os.Available?.Version ?? "Not checked / unavailable"}\n"+
                        $"Pending: {(os.RollbackQueued ? os.Previous?.Version : os.Pending?.Version) ?? "None"}\n"+
                        $"Automatic updates: {(os.Automatic?"On":"Paused")}\n{os.Operation?.Stage ?? "Idle"}: {os.Operation?.Message}";
                    options.AddRange([new('c',"Check for updates",Ready),
                        new('d',"Update OS",Ready && !RebootRequired && os?.Available!=null && os.Available.Digest!=os.Current?.Digest),
                        new('a',os?.Automatic==true?"Pause automatic updates":"Enable automatic updates",Ready)]);
                    if(RebootRequired)options.Add(new('r',"Reboot to finish",Ready));
                    if(os?.Previous!=null && !RebootRequired)options.Add(new('b',"Roll back OS",Ready));
                    options.AddRange([new('v',"Refresh status"),new('0',"Back to updates")]);
                    break;
                default:
                    title="Updates";
                    body=installer ? "Updates are available after installation." :
                        $"Xur: {application?.Current.Version ?? "Status unavailable"} · {Channel}\nOS: {os?.Current?.Version ?? "Status unavailable"}\n"+
                        (RebootRequired?"Reboot required to finish the queued OS change.\n":"")+
                        (all==null?"Update All status unavailable. Refresh to retry.\n":
                            $"Update All: {(all.Busy?"Running":all.Operation?.Stage ?? "Idle")}\n{all.Operation?.Message}\n"+
                            string.Join('\n',all.Operation?.Results.Select(r=>$"{r.Name}: {r.Stage} · {r.Message}") ?? []))+
                        "\nUpdates Xur and the OS; never reboots automatically.\nSaved workloads keep their pinned engine versions.";
                    if(!installer)
                    {
                        options.AddRange([new('t',"Update All",Ready),new('h',"Xur application"),new('o',"Operating system")]);
                        if(RebootRequired)options.Add(new('r',"Reboot to finish",Ready));
                        options.Add(new('v',"Refresh status"));
                    }
                    options.Add(new('0',"Back to menu"));
                    break;
            }
            if(view is "application" or "os" && Busy)body+="\nAn update is running. Actions will be available when it finishes.";
            if(notice.Length>0)body=notice+"\n\n"+body;
            return new(view,title,LocalConsole.Clean(body),options.ToArray());
        }
    }
    string Channel => application==null?"Channel unavailable":application.Development?"Local build testing":application.Channel;

    public async Task Open(string target)
    {
        await gate.WaitAsync();try
        {
            view=target;Closed=false;notice="";
            if(view=="network"){await network.Open();return;}
            await ReadStatus();
        }finally{gate.Release();}
    }
    public async Task Refresh()
    {
        await gate.WaitAsync();try{if(view=="network")await network.Refresh();else await ReadStatus();}finally{gate.Release();}
    }
    public async Task Submit(string text){await gate.WaitAsync();try{if(view=="network")network.Submit(text);}finally{gate.Release();}}
    async Task ReadStatus()
    {
        if(installer)return;
        var appTask=Read<ApplicationUpdateStatus>("application-updates");
        var osTask=Read<OsUpdateStatus>("updates");
        var allTask=Read<UpdateAllStatus>("update-all");
        await Task.WhenAll(appTask,osTask,allTask);
        application=await appTask;os=await osTask;all=await allTask;
    }
    async Task<T?> Read<T>(string path)
    {
        try{return await client.GetFromJsonAsync<T>((local?"/local/":"/")+path);}
        catch(HttpRequestException){return default;}
        catch(TaskCanceledException){return default;}
        catch(JsonException){return default;}
    }
    public async Task Select(char key)
    {
        await gate.WaitAsync();try
        {
            if(Closed)return;
            if(view=="network"){await network.Select(key);Closed=network.Closed;return;}
            var option=Screen.Options.FirstOrDefault(o=>o.Key==key);
            if(option==null)return;
            if(!option.Enabled){notice="Action unavailable. Refresh status or wait for the current update to finish.";return;}
            notice="";
            if(key=='0')
            {
                if(view=="confirm")view=returnView;
                else if(view is "application" or "os")view="updates";
                else Closed=true;
                return;
            }
            if(key is 'h' or 'o'){view=key=='h'?"application":"os";await ReadStatus();return;}
            if(key=='v'){await ReadStatus();return;}
            if(key is 'r' or 's'){power=key=='r'?"reboot":"poweroff";returnView=view;view="confirm";return;}
            // Re-observe before a mutation; another console or the web UI may have started work.
            await ReadStatus();
            var current=Screen.Options.FirstOrDefault(o=>o.Key==key);
            if(current?.Enabled!=true || current.Label!=option.Label)
            {notice="Action unavailable. Status changed; review the current update state.";return;}
            string path,action;
            if(key=='y'){path="power";action=power;}
            else if(key=='t'){path="update-all";action="start";}
            else if(view=="application"){path="application-updates";action=key switch{'e'=>"check",'f'=>"update",_=>"rollback"};}
            else{path="updates";action=key switch{'c'=>"check",'d'=>"stage",'b'=>"rollback",_=>os!.Automatic?"disable":"enable"};}
            using var result=local ? await client.PostAsync("/local/"+(path=="power"?action:path+"/"+action),null) :
                path=="power" ? await client.PostAsync("/power/"+action,null) :
                path=="update-all" ? await client.PostAsync("/update-all",null) :
                await client.PostAsJsonAsync("/"+path,new {action});
            notice=result.IsSuccessStatusCode ? (key=='y'?"Power action requested.":"Update request accepted. Status refreshes while this screen is open.") : await Failure(result);
            if(key=='y')view=returnView; // A second Enter must never repeat a confirmed action.
            await ReadStatus();
        }
        catch(HttpRequestException){notice="Connection interrupted. Refresh status before retrying; the operation may already be running.";}
        catch(TaskCanceledException){notice="Request timed out. Refresh status before retrying; the operation may already be running.";}
        finally{gate.Release();}
    }
    static async Task<string> Failure(HttpResponseMessage response)
    {
        var body=await response.Content.ReadAsStringAsync();
        try
        {
            using var json=JsonDocument.Parse(body);
            if(json.RootElement.TryGetProperty("error",out var error) && error.ValueKind==JsonValueKind.String)
                return "Action failed: "+error.GetString();
        }catch(JsonException){}
        return $"Action failed (HTTP {(int)response.StatusCode}). Refresh status or review the logs.";
    }
}
