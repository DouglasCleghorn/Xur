using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xur.Control;
using Xur.Domain;
static class ControlPanelRender
{
    public static async Task Run(string output, bool documentation = false)
    {
        var root=Path.GetFullPath(Path.Combine(".build/evidence","h-"+Guid.NewGuid().ToString("N")[..12]));Directory.CreateDirectory(root);Directory.CreateDirectory(output);
        var previous=Environment.GetEnvironmentVariable("XUR_RUN");var mode=Environment.GetEnvironmentVariable("XUR_MODE");
        Environment.SetEnvironmentVariable("XUR_RUN",root);Environment.SetEnvironmentVariable("XUR_MODE","Installed");
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.ListenUnixSocket(root+"/agent.sock"));
        await using var agent=builder.Build();
        agent.MapGet("/system",()=>new SystemSnapshot(12,32L<<30,128L<<30,400L<<30,1000L<<30,1234,[]));
        var gpu=new GpuDevice("0000:01:00.0","NVIDIA","NVIDIA GeForce RTX 3090","nvidia","",24576,[],[]);
        agent.MapGet("/gpu-telemetry",()=>new GpuTelemetrySnapshot(DateTimeOffset.UtcNow,Enumerable.Range(1,4).Select(n=>new GpuTelemetry(gpu with{Pci=$"0000:0{n}:00.0"},"test",new(DateTimeOffset.UtcNow,MemoryUsedMiB:n==4?null:n*4096,MemoryTotalMiB:24576),[],[])).ToArray()));
        agent.MapGet("/storage-usage",()=>new StorageUsageSnapshot(DateTimeOffset.UtcNow,false,[new("/dev/nvme0n1p2","btrfs",["/"],500L<<30,120L<<30,380L<<30)],[new("/dev/nvme0n1","System SSD",500L<<30,["/dev/nvme0n1p2"],["/"]),new("/dev/sda","USB archive",2L<<40,[],[])],[]));
        agent.MapGet("/timezone",()=>new TimezoneStatus("America/Denver",["UTC","America/Denver"],true));
        agent.MapGet("/network/settings",()=>new NetworkSettingsStatus([new("eno1","02:00:00:00:00:10","Connected",["192.0.2.10/24"],"uuid",new("manual",["192.0.2.10/24"],"192.0.2.1",["192.0.2.53"]),new("auto"),true)],null));
        WifiStatus? wifi=new(true,true,[new("wlan0","02:00:00:00:00:30","disconnected",null,"/device/1","Example Wi-Fi adapter","example_wifi","1.0"),new("wlan1","02:00:00:00:00:31","disconnected",null,"/device/2","USB Wi-Fi adapter","example_usb","1.0")]);
        agent.MapGet("/network/wifi",IResult()=>wifi==null?Results.Conflict(new{error="Unavailable"}):Results.Json(wifi));
        agent.MapGet("/ntp",()=>new NtpStatus(true,true,true,["time.cloudflare.com"],""));
        DiagnosticSshStatus diagnosticSsh=new();
        agent.MapGet("/diagnostics/ssh",()=>diagnosticSsh);
        agent.MapGet("/storage/trim",()=>new TrimStatus(false,"ActiveState=active","Result=success",[new("ssd","/etc","/dev/nvme0n1p2[/ostree/deploy/default/deploy/"+new string('a',64)+".0/etc]","btrfs",1000,400,500,true,true,false),new("readonly","/boot","/dev/nvme1n1p1","ext4",1000,400,500,true,false,true)],[]));
        agent.MapGet("/updates",()=>new OsUpdateStatus(null,new("new","digest","image",false),null,null,false,true,false,null,""));
        agent.MapGet("/application-updates",()=>new ApplicationUpdateStatus("http://192.0.2.10:8088",new("current","1"),null,null,null,false,true,"local","-----BEGIN PUBLIC KEY-----\nfixture\n-----END PUBLIC KEY-----","http://192.0.2.10:8088"));
        agent.MapGet("/station-devices",()=>new StationDeviceInventory([new("usb:"+new string('a',64),"Desk hub","Serial","/usb/hub",true,false,[],[],Serial:"hub-serial"),new("usb:"+new string('b',64),"Keyboard","Port","/usb/keyboard",false,false,[],[]),new("usb:"+new string('c',64),"Second hub","Serial","/usb/hub2",true,false,[],[],Serial:"hub-serial")],[],[],[
            new("controller:"+new string('a',64),"Xbox controller","Serial","/usb/receiver/gip0",[],Serial:"fixture-pad-a"),
            new("controller:"+new string('b',64),"Xbox controller","Serial","/usb/receiver/gip1",[],Serial:"fixture-pad-b"),
            new(null,"Xbox controller without serial","Unavailable","/usb/receiver/gip2",[],Problem:"The driver does not report a stable controller identity. Assign the whole receiver or hub instead.")
        ]));
        StationStreamStatus[] streams=[];
        agent.MapGet("/workstations",()=>streams);
        agent.MapGet("/station-allocations",()=>Array.Empty<StationDeviceAllocation>());
        agent.MapGet("/station-users",()=>new[]{new StationAccount("doug",1000,"Doug","/var/home/doug")});
        agent.MapGet("/update-all",()=>new UpdateAllStatus(false,null));
        agent.MapGet("/displays",()=>new DisplayPowerStatus([
            new("display-console","card0-HDMI-A-1","0000:01:00.0","Example TV",null,"native",true,null),
            new("display-station","card1-HDMI-A-1","0000:02:00.0","Desk TV","w1","external",true,null),
            new("display-monitor","card2-DP-1","0000:03:00.0","Office monitor",null,null,false,"No CEC adapter assigned. HDMI alone does not guarantee CEC support.")
        ],[new("native","Native CEC","/dev/cec0",true,0,41),new("external","USB CEC","/dev/cec1",true,null,null)]));
        var stationRecipe=new Recipe("gaming-workstation","Desktop","host:plasma",[],0,"","Display",1,0,"",Kind:"Workstation",Engine:"Plasma");
        Directory.CreateDirectory(root+"/catalog");File.WriteAllText(root+"/catalog/desktop.json",System.Text.Json.JsonSerializer.Serialize(stationRecipe));
        using var store=new ProfileStore(root+"/state");var observer=new Observer();var manager=new ProfileManager(store,observer,new Gateway());
        store.Save(new Profile("1","AI and gaming",1,[new("w1","Gaming",stationRecipe,[gpu.Pci],"desktop")]));store.Save(new Profile("2","Speech services",1,[]));
        if(documentation)
        {
            observer.Snapshot=new RuntimeObservation("example",[gpu with {ShortId="GPU 1",Cards=["/dev/dri/card0"]}],[]);
            store.Save(new Profile("1","Gaming desk",2,[new("w1","Gaming",stationRecipe,[gpu.Pci],"desktop")]));
            store.Save(new Profile("2","Studio desk",2,[new("doc-studio","Studio",stationRecipe,[gpu.Pci],"desktop")]));
        }
        try
        {
            await agent.StartAsync();var services=new ServiceCollection();services.AddLogging();var context=new DefaultHttpContext();context.Request.Scheme="https";context.Request.Host=new HostString("stations.test");services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor{HttpContext=context});services.AddSingleton(new Appliance());services.AddSingleton(manager);services.AddSingleton(new ProfileAccessSettings(root+"/profile-access.json"));services.AddSingleton(new RecipeCatalog(root+"/catalog"));services.AddSingleton(new Bootstrap(directory:root));services.AddSingleton<NavigationManager>(new Navigation());
            await using var provider=services.BuildServiceProvider();await using var renderer=new HtmlRenderer(provider,provider.GetRequiredService<ILoggerFactory>());
            foreach(var page in new[]{"home","displays","workstations","endpoints","monitoring","files","model-lab","api-keys","settings","settings-ssh-enabled","diagnostics","network-settings","storage","profile-edit","profile-switch","robotics"})
            {
                diagnosticSsh=page=="settings-ssh-enabled"?new(true,2):new();
                var priorSnapshot=observer.Snapshot;
                if(page=="profile-edit")observer.Snapshot=observer.Snapshot with{Gpus=[gpu with{ShortId="GPU 1",Cards=["/dev/dri/card0"]}]};
                RenderFragment body=b=>{b.OpenComponent(0,page=="robotics"?typeof(Xur.Control.Components.Pages.RoboticsPage):page=="displays"?typeof(Xur.Control.Components.Pages.Displays):page=="profile-switch"?typeof(Xur.Control.Components.Pages.ProfileSwitch):page=="workstations"?typeof(Xur.Control.Components.Pages.Workstations):page=="endpoints"?typeof(Xur.Control.Components.Pages.Endpoints):page=="profile-edit"?typeof(ProfileEditor):page=="network-settings"?typeof(Xur.Control.Components.Pages.NetworkSettingsPage):page is "settings" or "settings-ssh-enabled"?typeof(Xur.Control.Components.Pages.Network):page=="diagnostics"?typeof(Xur.Control.Components.Pages.Diagnostics):page=="storage"?typeof(Xur.Control.Components.Pages.StorageUsagePage):page=="api-keys"?typeof(Xur.Control.Components.Pages.ApiKeysPage):page=="model-lab"?typeof(Xur.Control.Components.Pages.ModelLab):page=="home"?typeof(Xur.Control.Components.Pages.Home):page=="files"?typeof(Xur.Control.Components.Pages.Files):typeof(Xur.Control.Components.Pages.Monitoring));b.CloseComponent();};
                var html=await renderer.Dispatcher.InvokeAsync(async()=> (await renderer.RenderComponentAsync<Xur.Control.Components.Layout.MainLayout>(ParameterView.FromDictionary(new Dictionary<string,object?>{{"Body",body}}))).ToHtmlString());
                // Static rendering has no HTTP request from which to generate antiforgery tokens.
                // Supply a fixture token for browser tests; production renders AntiforgeryToken normally.
                if(page=="profile-edit") html=html.Replace("<div id=\"workload-rows\"", "<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"fixture-only\"><div id=\"workload-rows\"");
                if(page=="network-settings")html=System.Text.RegularExpressions.Regex.Replace(html,"(<form\\b[^>]*>)","$1<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"fixture-only\">");
                await File.WriteAllTextAsync(Path.Combine(output,page+".html"),"<!doctype html><html><head><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><meta name=xur-csrf content=fixture-only><script src='/app-fetch.js'></script><link rel=stylesheet href='/setup.css'><link rel=stylesheet href='/workstations.css'><link rel=stylesheet href='/files.css'><link rel=stylesheet href='/profile-editor.css'><link rel=stylesheet href='/profile-switcher.css'></head><body>"+html+"</body></html>");
                observer.Snapshot=priorSnapshot;
            }
            observer.Snapshot=observer.Snapshot with{Gpus=[gpu with{ShortId="GPU 1",Cards=["/dev/dri/card0"]}]};
            foreach(var (fixture,type) in new[]{("empty",typeof(EmptyProfileEditor)),("primary",typeof(ProfileEditor))})
            {
                if(fixture=="primary")store.Save(store.Get<Profile>("profile","1")! with{Workloads=[new("w1","Gaming",stationRecipe,[gpu.Pci],"desktop",Devices:new(true,["usb:"+new string('b',64),"usb:"+new string('d',64)],["controller:"+new string('a',64),"controller:"+new string('d',64)]))]});
                var html=await renderer.Dispatcher.InvokeAsync(async()=> (await renderer.RenderComponentAsync(type,ParameterView.Empty)).ToHtmlString());
                html=html.Replace("<div id=\"workload-rows\"", "<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"fixture-only\"><div id=\"workload-rows\"");
                await File.WriteAllTextAsync(Path.Combine(output,"profile-edit-"+fixture+".html"),"<!doctype html><html><head><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><script src='/app-fetch.js'></script><link rel=stylesheet href='/setup.css'><link rel=stylesheet href='/profile-editor.css'></head><body><main>"+html+"</main></body></html>");
            }
            var availableWifi=wifi!;
            foreach(var (name,status,pending) in new (string,WifiStatus?,bool)[]{("off",availableWifi with{Enabled=false},false),("blocked",availableWifi with{HardwareEnabled=false},false),("missing",availableWifi with{Adapters=[]},false),("unavailable",availableWifi with{Adapters=[availableWifi.Adapters[0] with{State="20 (unavailable)",Reason="42 (The supplicant is not available)"}]},false),("error",null,false),("pending",availableWifi,true)})
            {
                wifi=status;
                var html=await renderer.Dispatcher.InvokeAsync(async()=> (await renderer.RenderComponentAsync<Xur.Control.Components.WifiSettings>(ParameterView.FromDictionary(new Dictionary<string,object?>{{"ChangePending",pending}}))).ToHtmlString());
                html=System.Text.RegularExpressions.Regex.Replace(html,"(<form\\b[^>]*>)","$1<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"fixture-only\">");
                await File.WriteAllTextAsync(Path.Combine(output,"wifi-"+name+".html"),"<!doctype html><html><head><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><link rel=stylesheet href='/setup.css'></head><body><main>"+html+"</main></body></html>");
            }
            wifi=availableWifi;
            // Exercise the actual workstation renderer with running, shared, stopped and unassigned desktops.
            var gaming=new Workload("w1","Gaming workstation",stationRecipe,[gpu.Pci],"desktop",new("doug",1000,false));
            var studio=new Workload("w2","Studio desktop",stationRecipe,[gpu.Pci],"desktop",new("doug",1000,false));
            var guest=new Workload("w3","Guest desktop",stationRecipe,[gpu.Pci],"desktop",new("",0,true));
            store.Put("station","w1",new StationDefinition(gaming.Id,gaming.Name,gaming.User));
            store.Save(new Profile("1","AI and gaming",2,[gaming]));
            store.Save(new Profile("3","Gaming only",1,[gaming]));
            store.Save(new Profile("4","Studio",1,[studio]));
            store.Save(new Profile("5","Guest",1,[guest]));
            store.Save(new Profile("6","Studio and models",1,[studio]));
            store.Put("station","w4",new StationDefinition("w4","Spare desktop",new("",0,true)));
            observer.Snapshot=new RuntimeObservation("generation",[gpu with {ShortId="GPU 3"}],[new RuntimeInstance(gaming.Id,gaming.Fingerprint,"instance",1,"boot","", "running",gaming.Gpus)]);
            streams=[new("w1","Ready",true)];
            var workstations=await renderer.Dispatcher.InvokeAsync(async()=> (await renderer.RenderComponentAsync<Xur.Control.Components.Pages.Workstations>()).ToHtmlString());
            await File.WriteAllTextAsync(Path.Combine(output,"workstations-populated.html"),"<!doctype html><html><head><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><link rel=stylesheet href='/setup.css'><link rel=stylesheet href='/workstations.css'></head><body><main>"+workstations+"</main></body></html>");
        }
        finally{await agent.StopAsync();Environment.SetEnvironmentVariable("XUR_RUN",previous);Environment.SetEnvironmentVariable("XUR_MODE",mode);}
        store.Dispose();Directory.Delete(root,true);
    }
    sealed class ProfileEditor:Xur.Control.Components.Pages.ProfileEdit { protected override async Task OnInitializedAsync(){Id="1";await base.OnInitializedAsync();} }
    sealed class EmptyProfileEditor:Xur.Control.Components.Pages.ProfileEdit { protected override async Task OnInitializedAsync(){Id="2";await base.OnInitializedAsync();} }
    sealed class Navigation:NavigationManager {public Navigation(){Initialize("http://home.test/","http://home.test/");} protected override void NavigateToCore(string uri,bool forceLoad){} }
    sealed class Observer:IWorkloadRuntime
    {
        public RuntimeObservation Snapshot=new("generation",[],[]);
        public Task<RuntimeObservation> Observe()=>Task.FromResult(Snapshot);
        public Task<RuntimeInstance> Start(Workload w)=>throw new NotSupportedException();
        public Task Stop(RuntimeStop r)=>throw new NotSupportedException();
    }
    sealed class Gateway:IWorkloadGateway
    {
        public Task Drain(string id)=>throw new NotSupportedException();
        public Task Publish(BackendRoute[] routes)=>throw new NotSupportedException();
    }
}
