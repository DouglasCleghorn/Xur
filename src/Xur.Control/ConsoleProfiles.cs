using System.Net.Http.Json;
using System.Text.Json;
using Xur.Domain;
namespace Xur.Control;

public sealed class ConsoleProfiles(HttpClient client)
{
    ProfileState? state;ProfilePlan? plan;string error="";int page;
    public bool Closed {get;private set;}
    public string Trigger {get;set;}="terminal";
    bool Busy=>state?.Operation?.Stage is "Applying" or "Cancelling" or "Failed";
    Profile[] Choices=>state?.Profiles.OrderBy(p=>p.Name,StringComparer.OrdinalIgnoreCase).Skip(page*6).Take(6).ToArray()??[];
    public ConsoleScreen Screen
    {
        get
        {
            var options=new List<ConsoleOption>();string body;
            if(plan!=null)
            {
                var definitions=(state?.Profiles.SelectMany(p=>p.Workloads)??[]).Concat(state?.Active?.Workloads??[]).Concat(plan.Target.Workloads).GroupBy(w=>w.Id).ToDictionary(g=>g.Key,g=>g.Last());
                body=string.Join('\n',plan.Steps.Where(s=>s.Kind is "Keep" or "Stop" or "Start").Select(s=>(s.Kind=="Keep"?"Keep running":s.Kind)+": "+definitions.GetValueOrDefault(s.WorkloadId)?.Name));
                if(plan.Steps.Any(s=>s.Kind=="Stop"&&definitions.GetValueOrDefault(s.WorkloadId)?.Recipe.Kind=="Workstation"))body+="\n\nYour current desktop may stop. Save your work first.";
                options.AddRange([new('0',"Back"),new('y',plan.Unload?"Unload all":"Load profile",plan.Expires>DateTimeOffset.UtcNow)]);
            }
            else
            {
                body=state==null?"Profiles unavailable.":"Loaded: "+(state.Active?.Name??"No complete profile loaded");
                if(Busy)body+="\nA profile change needs attention or is in progress. Open the web manager.";
                for(var i=0;i<Choices.Length;i++)options.Add(new((char)('1'+i),Choices[i].Name,!Busy));
                if((state?.Profiles.Length??0)>(page+1)*6)options.Add(new('d',"Next profiles"));
                if(page>0)options.Add(new('b',"Previous profiles"));
                options.AddRange([new('u',"Unload all",!Busy&&state?.Runtime.Instances.Length>0),new('v',"Refresh"),new('0',"Back to menu")]);
            }
            if(error.Length>0)body=error+"\n\n"+body;
            return new("profiles",plan==null?"Switch profile":plan.Unload?"Review unload all":"Review "+plan.Target.Name,LocalConsole.Clean(body),options.ToArray());
        }
    }
    public async Task Open(){Closed=false;page=0;plan=null;await Refresh();}
    public async Task Refresh()
    {
        if(plan!=null)return;
        state=null;error="";
        try {using var response=await client.GetAsync("/local/profiles");if(response.IsSuccessStatusCode)state=await response.Content.ReadFromJsonAsync<ProfileState>();else error=await Error(response);}
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException or JsonException){error="Could not contact the profile manager. Refresh to retry.";}
    }
    public async Task Select(char key)
    {
        if(Screen.Options.SingleOrDefault(o=>o.Key==key)?.Enabled!=true)return;
        if(key=='0'){if(plan!=null){plan=null;await Refresh();}else Closed=true;return;}
        if(key=='v'){await Refresh();return;}
        if(key is 'd' or 'b'){page+=key=='d'?1:-1;return;}
        error="";
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,plan!=null?"/local/profiles/apply":key=='u'?"/local/profiles/unload/preview":"/local/profiles/preview");
            request.Headers.Add("X-Xur-Switch-Trigger",Trigger);
            request.Content=plan!=null?JsonContent.Create(new Approval(plan.Id,plan.Digest)):key=='u'?JsonContent.Create(new{}):JsonContent.Create(new ConsoleProfileSelection(Choices[key-'1'].Id));
            using var response=await client.SendAsync(request);
            if(!response.IsSuccessStatusCode){error=await Error(response);plan=null;return;}
            if(plan==null)plan=await response.Content.ReadFromJsonAsync<ProfilePlan>();
            else{plan=null;await Refresh();}
        }
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException or JsonException){plan=null;error="Connection interrupted. Refresh before reviewing again.";}
    }
    static async Task<string> Error(HttpResponseMessage response)
    {try{var data=await response.Content.ReadFromJsonAsync<JsonElement>();return data.GetProperty("error").GetString()??"Profile action failed.";}catch{return "Profile action failed. Refresh to retry.";}}
}
