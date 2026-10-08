using System.Net;
using System.Net.Http.Json;
using Xur.Control;
using Xur.Domain;

static class ConsoleMaintenanceTests
{
    public static async Task Run(Action<bool,string> check)
    {
        using var handler=new Agent();using var client=new HttpClient(handler){BaseAddress=new Uri("http://console.test")};
        var menu=new ConsoleMaintenance(client);
        await menu.Open("updates");
        check(menu.Screen.Options[0] is {Key:'t',Label:"Update All",Enabled:true} && menu.Screen.Body.Contains("nightly"),"Updates opens with Update All and the selected channel");
        LocalConsole.OpenMaintenance(menu.Screen);
        check(LocalConsole.Navigate(ConsoleKeyAction.Enter)=='t',"Keyboard selects Update All first");
        check(LocalConsole.SelectLine("2")=='h',"Serial numbers select the same submenu as keyboard numbers");
        await menu.Select('t');
        check(handler.Posts.SequenceEqual(["/update-all"]),"Update All reaches the shared job without rebooting");
        check(menu.Screen.Body.Contains("Running") && !menu.Screen.Options[0].Enabled,"Update All progress disables duplicate starts");
        LocalConsole.OpenMaintenance(menu.Screen);LocalConsole.Navigate(ConsoleKeyAction.One);
        check(LocalConsole.Navigate(ConsoleKeyAction.Enter)==null,"Keyboard cannot invoke a disabled update action");
        await menu.Select('t');check(handler.Posts.Count==1,"Disabled Update All cannot send another request");
        await menu.Select('h');
        check(menu.Screen.Options.Where(o=>o.Key is 'e' or 'f' or 'g').All(o=>!o.Enabled),"Individual Xur actions are unavailable while Update All runs");
        check(menu.Screen.Options.All(o=>o.Key!='g'),"Application rollback is absent without a previous version");
        await menu.Select('0');check(menu.Screen.Id=="updates","Back from Xur returns to Updates");
        handler.All=new(false,new("job","Complete","Finished",[new("Operating system","Complete","Reboot to finish."),new("Xur","Failed","Signature rejected")],0));
        handler.Os=handler.Os with {Pending=new("2","new","image",false)};
        await menu.Refresh();
        check(menu.Screen.Body.Contains("Signature rejected") && menu.Screen.Options.Any(o=>o.Key=='r'),"Results retain individual failures and offer reboot for a queued OS deployment");
        await menu.Select('o');
        LocalConsole.OpenMaintenance(menu.Screen);LocalConsole.Navigate(ConsoleKeyAction.Down);LocalConsole.Navigate(ConsoleKeyAction.Down);
        LocalConsole.OpenMaintenance(menu.Screen,refreshOnly:true);
        check(LocalConsole.Navigate(ConsoleKeyAction.Enter)=='a',"Background status refresh preserves the selected action");
        check(!menu.Screen.Options.Single(o=>o.Key=='d').Enabled && menu.Screen.Options.All(o=>o.Key!='b'),"A queued OS deployment prevents another update or rollback");
        check(menu.Screen.Options.Single(o=>o.Key=='a').Label=="Pause automatic updates","Automatic update action names its result");
        await menu.Select('a');
        check(handler.Posts.Last()=="/updates" && menu.Screen.Options.Single(o=>o.Key=='a').Label=="Enable automatic updates","Pause changes to Enable after the observed state changes");
        var toggleCount=handler.Posts.Count;handler.Os=handler.Os with {Automatic=true};await menu.Select('a');
        check(handler.Posts.Count==toggleCount,"A concurrent automatic-update setting change cannot reverse the selected action");
        await menu.Select('r');
        LocalConsole.OpenMaintenance(menu.Screen);
        check(menu.Screen.Id=="confirm" && LocalConsole.Navigate(ConsoleKeyAction.Enter)=='0',"Reboot confirmation defaults to Cancel");
        var count=handler.Posts.Count;await menu.Select('0');
        check(menu.Screen.Id=="os" && handler.Posts.Count==count,"Cancelling reboot returns to the originating screen without a power request");
        await menu.Select('r');await menu.Select('y');
        check(handler.Posts.Last()=="/power/reboot" && menu.Screen.Id=="os","Only explicit confirmation sends reboot and then leaves confirmation");
        count=handler.Posts.Count;await menu.Select('y');check(handler.Posts.Count==count,"Repeated confirmation cannot repeat the power action");
        await menu.Open("power");
        check(menu.Screen.Options.Take(3).Select(o=>o.Key).SequenceEqual(['r','s','d']),"CEC display power preserves the existing reboot and shutdown number shortcuts");
        await menu.Select('s');await menu.Select('y');
        check(handler.Posts.Last()=="/power/poweroff","Shutdown uses the same explicit confirmation flow");
        handler.Reject=true;await menu.Open("updates");await menu.Select('h');await menu.Select('f');
        check(menu.Screen.Body.Contains("Wait for the OS update to finish."),"Application update rejection is visible instead of silently ignored");
        await menu.Refresh();check(menu.Screen.Body.Contains("Wait for the OS update to finish."),"Background refresh retains the action failure");
        handler.Reject=false;handler.Offline=true;await menu.Refresh();
        check(menu.Screen.Body.Contains("Could not load Xur") && menu.Screen.Options.Where(o=>o.Key is 'e' or 'f').All(o=>!o.Enabled),"Unavailable status removes stale update actions and explains recovery");
        handler.Offline=false;await menu.Select('v');
        check(menu.Screen.Options.Single(o=>o.Key=='f').Enabled,"Refresh restores actions when the agent recovers");
        // State can change after drawing but before selecting an enabled row.
        handler.All=handler.All with {Busy=true};count=handler.Posts.Count;await menu.Select('f');
        check(handler.Posts.Count==count,"Mutation rechecks status to reject an update started by another client");
        await menu.Select('0');await menu.Select('0');check(menu.Closed,"Back from Updates closes the submenu");
        LocalConsole.OpenQr(false);LocalConsole.OpenMaintenance(menu.Screen,refreshOnly:true);
        check(!LocalConsole.ViewingMaintenance,"Background refresh cannot reopen a closed console screen");
        handler.All=handler.All with {Busy=false};
        handler.Os=handler.Os with{Pending=null,Window=new("123",0,DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds(),"3","Waiting"),Schedule=new("03:00",[0,1,2,3,4,5,6]),Timezone="UTC"};
        await menu.Open("updates");
        check(menu.Screen.Body.Contains("will be staged") && menu.Screen.Options.Any(o=>o.Key=='k'),"Console announces an available update and offers to skip that window");
        LocalConsole.UpdateWindow(handler.Os.Window);
        check(LocalConsole.Clean(LocalConsole.ExportFrame(100,40)).Contains("Open Updates to skip"),"Physical console shows the upcoming update outside its update submenu");
        LocalConsole.UpdateWindow(null);
        await menu.Select('k');
        check(handler.LastUpdate is {Action:"skip",WindowId:"123"} && menu.Screen.Options.All(o=>o.Key!='k') && handler.Os.Automatic,"Console skip carries the announced window ID and leaves recurring updates enabled");
        handler.Os=handler.Os with{Window=new("456",0,1000,"3","Waiting")};await menu.Refresh();
        handler.Os=handler.Os with{Window=new("789",0,1000,"3","Waiting")};count=handler.Posts.Count;await menu.Select('k');
        check(handler.Posts.Count==count && menu.Screen.Body.Contains("window changed"),"A stale console cannot skip a newly announced window");
        await menu.Select('o');await menu.Select('l');await menu.Select('c');await menu.Submit("22:30");
        await menu.Select('d');await menu.Submit("1,5");await menu.Select('n');await menu.Submit("30");
        await menu.Select('y');
        check(menu.Screen.Id=="os" && handler.LastUpdate is {Action:"schedule",Schedule.Time:"22:30",Schedule.WarningMinutes:30} && handler.LastUpdate.Schedule.Days.SequenceEqual([0,4]),"Console schedule editor saves a typed weekly schedule with advance notice");
        var local=new ConsoleMaintenance(client,local:true);await local.Open("updates");await local.Select('t');
        check(handler.Posts.Last()=="/local/update-all/start","Text menu invokes the root-private Update All route");
        handler.All=handler.All with{Busy=false};await local.Open("schedule");await local.Select('y');
        check(handler.Posts.Last()=="/local/updates/schedule" && handler.LastUpdate?.Schedule?.Time=="22:30","Text schedule editor forwards JSON through the root-private route");
        handler.Os=handler.Os with{Schedule=null};await local.Open("schedule");await local.Select('y');
        check(handler.LastUpdate is {Action:"schedule",Schedule.Time:"03:00",Schedule.WarningMinutes:15} && handler.LastUpdate.Schedule.Days.SequenceEqual([6]),"An unconfigured console schedule saves Sunday at 03:00 with fifteen minutes of notice");
        count=handler.Reads;var installer=new ConsoleMaintenance(client,installer:true);await installer.Open("updates");
        check(handler.Reads==count && installer.Screen.Options.Length==1 && !LocalConsole.RootOptions(true).Contains("Updates"),"Installer hides updates and never reads installed update state");
        await installer.Open("power");await installer.Select('s');await installer.Select('0');
        check(installer.Screen.Id=="power","Installer retains cancellable power controls");
        string? command=null;
        await Xur.Cli.Commands.Invoke(["update-all","start"],()=>Task.CompletedTask,(action,_)=>{command=action;return Task.CompletedTask;});
        check(command=="update-all/start","Noninteractive CLI exposes Update All start");
        await Xur.Cli.Commands.Invoke(["update-all","status","--json"],()=>Task.CompletedTask,(action,_)=>{command=action;return Task.CompletedTask;});
        check(command=="update-all","Noninteractive CLI exposes Update All status");
        await Xur.Cli.Commands.Invoke(["updates","skip"],()=>Task.CompletedTask,(action,_)=>{command=action;return Task.CompletedTask;});
        check(command=="updates/skip","Noninteractive CLI exposes skipping the currently announced window");
        await Xur.Cli.Commands.Invoke(["updates","schedule"],()=>Task.CompletedTask,(action,_)=>{command=action;return Task.CompletedTask;});
        check(command=="update-schedule","CLI exposes the shared schedule editor");
    }
    sealed class Agent:HttpMessageHandler
    {
        public UpdateAllStatus All=new(false,null);
        public OsUpdateStatus Os=new(new("1","old","image",false),null,new("0","previous","image",false),new("2","new","image",false),false,true,false,null,"");
        public ApplicationUpdateStatus App=new("https://updates.test",new("old","1"),null,new("new","2"),null,false,Channel:"nightly");
        public List<string> Posts=[];public bool Reject,Offline;public int Reads;
        public OsUpdateAction? LastUpdate;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var path=request.RequestUri!.AbsolutePath;
            if(request.Method==HttpMethod.Post)
            {
                Posts.Add(path);
                if(Reject)return new(HttpStatusCode.Conflict){Content=JsonContent.Create(new{error="Wait for the OS update to finish."})};
                if(path is "/update-all" or "/local/update-all/start")All=All with {Busy=true};
                if(path=="/updates" || path.StartsWith("/local/updates/"))
                {
                    var json=await request.Content!.ReadFromJsonAsync<OsUpdateAction>(token);
                    LastUpdate=json;
                    if(json?.Action=="disable")Os=Os with {Automatic=false};
                    if(json?.Action=="skip")Os=Os with {Window=null};
                    if(json?.Action=="schedule")Os=Os with {Schedule=json.Schedule,Window=null};
                }
                return new(HttpStatusCode.Accepted);
            }
            Reads++;
            if(Offline)return new(HttpStatusCode.ServiceUnavailable);
            object value=path.Replace("/local/","/") switch {"/update-all"=>All,"/updates"=>Os,"/application-updates"=>App,_=>throw new Exception(path)};
            return new(HttpStatusCode.OK){Content=JsonContent.Create(value)};
        }
    }
}
