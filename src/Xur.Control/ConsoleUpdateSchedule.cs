using System.Net.Http.Json;
using Xur.Domain;
namespace Xur.Control;

public sealed class ConsoleUpdateSchedule(HttpClient client,bool local)
{
    static readonly string[] Days=["Mon","Tue","Wed","Thu","Fri","Sat","Sun"];
    OsUpdateSchedule schedule=new("03:00",[0,1,2,3,4,5,6]);
    string edit="",notice="",zone="",validation="";
    public bool Closed {get;private set;}
    public void Open(OsUpdateStatus? status)
    {schedule=status?.Schedule??new("03:00",[0,1,2,3,4,5,6]);zone=status?.Timezone??"Server local time";edit="";notice="";validation="";Closed=false;}
    public ConsoleScreen Screen=>edit switch {
        "time"=>new("schedule-time","Update time",validation+"Enter HH:mm in the server timezone: "+zone,[new('0',"Cancel")],schedule.Time),
        "days"=>new("schedule-days","Update days",validation+"Enter day numbers separated by commas: 1=Mon, 2=Tue, …, 7=Sun.",[new('0',"Cancel")],string.Join(',',schedule.Days.Select(d=>d+1))),
        "warning"=>new("schedule-warning","Advance notice",validation+"Enter minutes of advance notice, from 5 to 120.",[new('0',"Cancel")],schedule.WarningMinutes.ToString()),
        _=>new("schedule","Automatic update schedule",notice+($"\nTime: {schedule.Time} · {zone}\nDays: {string.Join(", ",schedule.Days.Select(d=>Days[d]))}\nAdvance notice: {schedule.WarningMinutes} minutes\nNotice appears only when an OS update is available. Reboot remains manual."),
            [new('c',"Change time"),new('d',"Change days"),new('n',"Change advance notice"),new('y',"Save schedule"),new('0',"Back to OS updates")])
    };
    public void Submit(string text)
    {
        if(edit=="time")
        {
            if(!System.Text.RegularExpressions.Regex.IsMatch(text,@"\A(?:[01][0-9]|2[0-3]):[0-5][0-9]\z")){validation="Invalid time. ";return;}
            schedule=schedule with{Time=text};
        }
        else if(edit=="days")
        {
            var values=text.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
            if(values.Length==0 || values.Any(v=>!int.TryParse(v,out var day)||day is <1 or >7)){validation="Choose at least one valid day. ";return;}
            schedule=schedule with{Days=values.Select(v=>int.Parse(v)-1).Distinct().Order().ToArray()};
        }
        else if(edit=="warning")
        {
            if(!int.TryParse(text,out var minutes)||minutes is <5 or >120){validation="Invalid notice period. ";return;}
            schedule=schedule with{WarningMinutes=minutes};
        }
        edit="";validation="";notice="Changes are ready to save.";
    }
    public async Task Select(char key)
    {
        if(key=='0'){validation="";if(edit.Length>0)edit="";else Closed=true;return;}
        if(edit.Length>0)return;
        if(key is 'c' or 'd' or 'n'){edit=key=='c'?"time":key=='d'?"days":"warning";return;}
        if(key!='y')return;
        try
        {
            using var response=await client.PostAsJsonAsync(local?"/local/updates/schedule":"/updates",new OsUpdateAction("schedule",schedule));
            if(response.IsSuccessStatusCode){Closed=true;return;}
            notice="Could not save the schedule. Check the values and retry.";
        }
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException){notice="Connection interrupted. Review the saved schedule before retrying.";}
    }
}
