using System.Net.Http.Json;
using System.Text.Json;
using Xur.Domain;
namespace Xur.Control;

// The local setup path uses the agent's expiring plans and exact disk identity approval.
public sealed class ConsoleSetup(HttpClient client,bool installer=true)
{
    readonly ConsoleNetwork network=new(client,true);
    readonly ConsoleComputerName name=new(client,true);
    string view="home",notice="";
    ComputerNameStatus? serverName;
    Inventory? inventory;
    InstallPlan? plan;
    Operation? operation;
    bool disksReady;
    string discovery="Storage discovery is not ready. Refresh shortly.";
    public bool Closed {get;private set;}
    string Disk(Disk d)=>$"{d.Path} · {d.Model} · {d.Bytes/1073741824d:N1} GiB\n  Serial: {d.Serial} · WWN: {d.Wwn}\n  Identity: {d.StablePath}";
    public ConsoleScreen Screen
    {
        get
        {
            if(view=="name")return name.Screen;
            if(view=="network")return network.Screen;
            string title=installer?"Setup and installation":"Local setup",body;string? input=null;bool secret=false;
            var options=new List<ConsoleOption>();
            switch(view)
            {
                case "disks":
                    title="Choose installation disk";body="The selected disk will be erased only after you review and confirm.\nInternet is required to download Bazzite.\n\n";
                    if(!disksReady)body+=discovery;
                    else if(inventory==null)body+="Could not read disks. Refresh to retry.";
                    else
                    {
                        for(var i=0;i<inventory.Disks.Length;i++)
                        {
                            var d=inventory.Disks[i];body+=Disk(d)+"\n"+(d.Blocked.Length>0?"  Unavailable: "+string.Join("; ",d.Blocked)+"\n":"")+"\n";
                            options.Add(new((char)(256+i),d.Path+" · "+d.Model+" · "+(d.Bytes/1073741824d).ToString("N1")+" GiB",d.Blocked.Length==0));
                        }
                        if(inventory.Disks.Length==0)body+="No disks found.";
                    }
                    options.Add(new('v',"Refresh disks"));break;
                case "review":
                    title="Review disk installation";
                    body="WILL ERASE ALL CONTENTS:\n"+Disk(plan!.Target)+"\n\nPlanned changes:\n"+string.Join('\n',plan.Actions)+
                        "\n\nOther disks (unchanged):\n"+string.Join('\n',plan.Unaffected.Select(Disk))+"\n\nInternet is required. Review expires in five minutes.";
                    options.Add(new('0',"Cancel"));options.Add(new('y',"Continue to erase confirmation",plan.Expires>DateTimeOffset.UtcNow));break;
                case "confirm":
                    title="Confirm disk erasure";body="ALL CONTENTS WILL BE ERASED:\n"+Disk(plan!.Target)+"\n\nType ERASE "+plan.Target.Path+" and press Enter to install.\nEscape cancels.";input="";break;
                case "progress":
                    title="Installation progress";body=operation==null?"No installation has started.":operation.Stage+"\n"+operation.Message;
                    if(operation?.Stage=="Complete"){body+="\nRemove the installer USB when restarting.";options.Add(new('r',"Reboot into installed system"));}
                    if(operation?.Stage=="Failed")body+="\nNo automatic retry. Review Logs and network settings before restarting setup.";
                    options.Add(new('v',"Refresh progress"));break;
                case "reboot":title="Confirm reboot";body="Reboot into the installed system. Remove the installer USB when restarting.";options.AddRange([new('0',"Cancel"),new('y',"Reboot now",operation?.Stage=="Complete")]);break;
                default:
                    body=(installer?"Complete device setup here. Web management and Tailscale are available after installation and reboot.\n":"Manage local server settings.\n")+
                        (serverName?.Configured==true?"Server: "+serverName.Name:"Save the server name before installation.")+"\n"+
                        "Create the required administrator account in the browser after installation, using the console access code."+
                        (installer?"\nInternet is required for the OS download.":"");
                    options.AddRange([new('n',"Server name"),new('w',"Network and Wi-Fi"),
                        new('v',"Refresh setup status")]);
                    if(installer)options.InsertRange(2,[new('d',"Choose installation disk",serverName?.Configured==true&&operation==null),new('p',"Installation progress")]);break;
            }
            if(!options.Any(o=>o.Key=='0'))options.Add(new('0',view=="home"?"Back to menu":"Back to setup"));
            return new("setup-"+view,title,LocalConsole.Clean((notice.Length>0?notice+"\n\n":"")+body),options.ToArray(),input,secret);
        }
    }
    public async Task Open(){Closed=false;view="home";plan=null;notice="";await Refresh();if(serverName is {Configured:false}){view="name";await name.Open();}}
    public async Task Refresh()
    {
        try
        {
            if(view=="network"){await network.Refresh();return;}
            if(view is "name" or "confirm" or "review")return;
            serverName=await client.GetFromJsonAsync<ComputerNameStatus>("/local/computer-name");
            if(!installer)return;
            var status=await client.GetFromJsonAsync<JsonElement>("/local/setup/status");
            operation=status.TryGetProperty("operation",out var op)&&op.ValueKind==JsonValueKind.Object?op.Deserialize<Operation>(new JsonSerializerOptions(JsonSerializerDefaults.Web)):null;
            disksReady=status.TryGetProperty("scan",out var scan)&&scan.TryGetProperty("state",out var state)&&state.GetString() is "NoAnswer" or "AnswerFound";
            if(!disksReady)discovery=scan.ValueKind==JsonValueKind.Object&&scan.TryGetProperty("state",out var scanState)&&scanState.GetString()!="Starting"
                ?"Storage needs attention: "+scanState.GetString()+"\n"+(scan.TryGetProperty("errors",out var errors)&&errors.ValueKind==JsonValueKind.Array?string.Join('\n',errors.EnumerateArray().Select(e=>e.GetString())):"")
                :"Storage discovery is not ready. Refresh shortly.";
            if(view=="disks")
            {
                if(operation!=null){view="progress";return;}
                if(!disksReady)inventory=null;
                else if(inventory==null)inventory=await client.GetFromJsonAsync<Inventory>("/local/setup/disks");
            }
        }
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException or JsonException){notice="Could not read setup status. Refresh to retry.";disksReady=false;inventory=null;}
    }
    async Task<T?> Post<T>(string path,object value)
    {
        using var response=await client.PostAsJsonAsync(path,value);
        if(response.IsSuccessStatusCode)return await response.Content.ReadFromJsonAsync<T>();
        var message="Request failed. Refresh status before retrying.";
        try{using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());if(json.RootElement.TryGetProperty("error",out var error))message=error.GetString()??message;}catch(JsonException){}
        throw new InvalidOperationException(message);
    }
    public async Task Select(char key)
    {
        try
        {
            if(view=="name"){name.Select(key);if(name.Closed){view="home";await Refresh();}return;}
            if(view=="network"){await network.Select(key);if(network.Closed){view="home";await Refresh();}return;}
            if(Screen.Options.FirstOrDefault(o=>o.Key==key)?.Enabled!=true)return;
            notice="";
            if(key=='0'){plan=null;if(view=="home")Closed=true;else{view="home";await Refresh();}return;}
            if(view=="review"&&key=='y'){view="confirm";return;}
            if(view=="progress"&&key=='r'){view="reboot";return;}
            if(view=="reboot"&&key=='y')
            {
                await Refresh();if(operation?.Stage!="Complete"){view="progress";return;}
                view="progress";using var response=await client.PostAsync("/local/reboot",null);notice=response.IsSuccessStatusCode?"Reboot requested.":"Reboot unavailable. Refresh progress.";return;
            }
            if(view=="disks"&&key>=256)
            {
                // Preserve the chosen identity, even if a later inventory uses a new order.
                var target=inventory!.Disks[key-256];
                plan=await Post<InstallPlan>("/local/setup/plan",new ConsolePlanRequest(target.Path));
                if(plan==null||plan.Target.Path!=target.Path||plan.Target.Serial!=target.Serial||plan.Target.Wwn!=target.Wwn||plan.Target.Bytes!=target.Bytes||plan.Target.StablePath!=target.StablePath)
                {plan=null;inventory=null;await Refresh();notice="Disk identity changed. Review the refreshed disk list.";return;}
                view="review";return;
            }
            if(view=="home")switch(key)
            {
                case 'n':view="name";await name.Open();return;
                case 'w':view="network";await network.Open();return;
                case 'd':view="disks";inventory=null;break;
                case 'p':view="progress";break;
            }
            if(view=="disks"&&key=='v')inventory=null;
            await Refresh();
        }
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException){notice=e is InvalidOperationException?e.Message:"Connection interrupted. Refresh before retrying.";}
    }
    public async Task Submit(string text)
    {
        try
        {
            notice="";
            if(view=="name"){await name.Submit(text);return;}
            if(view=="network"){await network.Submit(text);return;}
            if(view=="confirm")
            {
                if(plan==null||plan.Expires<=DateTimeOffset.UtcNow){plan=null;view="disks";await Refresh();notice="The plan expired. Select the disk and review again.";return;}
                if(text!="ERASE "+plan.Target.Path){notice="Confirmation did not match. Nothing was erased.";return;}
                var approval=new Approval(plan.Id,plan.Digest);plan=null;view="progress";
                // Consume the plan before sending; a timeout must never repeat approval.
                operation=await Post<Operation>("/local/setup/approve",approval);await Refresh();
            }
        }
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException){notice=e is InvalidOperationException?e.Message:"Connection interrupted. Refresh progress before retrying; installation may already have started.";}
    }
}
