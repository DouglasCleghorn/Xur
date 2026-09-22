using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xur.Agent;
using Xur.Control;
using Xur.Domain;

static class WifiTests
{
    const string Mac="02:00:00:00:00:10",Bssid="02:00:00:00:00:20",Secret="  correct horse ";
    public static async Task Run(Action<bool,string> check)
    {
        var networks=NetworkSettings.ParseWifiNetworks("02\\:00\\:00\\:00\\:00\\:20:Cafe\\: A\\\\B:WPA2:75\n02\\:00\\:00\\:00\\:00\\:21:Guest:--:35\n02\\:00\\:00\\:00\\:00\\:22:Office:WPA2 802.1X:60\n02\\:00\\:00\\:00\\:00\\:23:Private:WPA3:50\n");
        check(networks[0].Ssid=="Cafe: A\\B"&&networks[0].Bssid==Bssid,"Wi-Fi scan preserves colons and backslashes in SSIDs");
        check(networks.Single(n=>n.Ssid=="Office").Supported==false&&networks.Single(n=>n.Ssid=="Guest").NeedsPassword==false&&networks.Single(n=>n.Ssid=="Private").KeyManagement=="sae","Wi-Fi security distinguishes open, WPA2, WPA3 and unsupported enterprise networks");
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../.build/evidence/wifi-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        try
        {
            var fake=new Nm();var settings=new NetworkSettings(root,fake.Run,Path.Combine(root,"profiles"));
            var status=await settings.ReadWifi();check(status.Adapters.Single().MacAddress==Mac,"Wi-Fi inventory uses the permanent adapter MAC instead of a randomized scan MAC");
            check(status.Adapters.Single().Driver=="test_wifi"&&status.Adapters.Single().Model=="Test adapter","Wi-Fi inventory exposes adapter model, driver and firmware details");
            fake.EmptyReads=2;var late=await settings.ScanWifi(new("wlan0",Mac));check(late.Length==1&&fake.ScanReads==3,"An initially empty Wi-Fi scan waits for late access point results before reporting no networks");
            fake.Malformed=true;var malformed=false;try{await settings.ScanWifi(new("wlan0",Mac));}catch(InvalidOperationException e){malformed=e.Message.Contains("unreadable");}fake.Malformed=false;
            check(malformed,"Unparseable Wi-Fi results report a scan error instead of no visible networks");
            fake.FirmwareMissing=true;var missing=false;try{await settings.ScanWifi(new("wlan0",Mac));}catch(InvalidOperationException e){missing=e.Message.Contains("Firmware is missing");}fake.FirmwareMissing=false;
            check(missing,"Missing adapter firmware is reported before attempting a scan");
            fake.State="20 (unavailable)";var reads=fake.ScanReads;var unavailable=false;
            try{await settings.ScanWifi(new("wlan0",Mac));}catch(InvalidOperationException e){unavailable=e.Message.Contains("supplicant");}
            check(unavailable&&fake.ScanReads==reads,"Unavailable adapters report the NetworkManager reason without requesting a scan");
            fake.State="100 (connected)";
            var request=new WifiConnectRequest("wlan0",Mac,"Home",Bssid,"wpa-psk",Secret);
            var result=await settings.ConnectWifi(request);
            check(result.Stage=="Kept"&&fake.AutoConnect,"Successful Wi-Fi connection is saved for automatic reconnection");
            check(fake.Profile.Contains("psk=\\s\\scorrect\\shorse\\s")&&fake.Permissions==(UnixFileMode.UserRead|UnixFileMode.UserWrite),"Wi-Fi password preserves spaces and is written with owner-only permissions");
            if(File.Exists("/usr/bin/nmcli"))
            {
                var info=new System.Diagnostics.ProcessStartInfo("nmcli"){RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
                foreach(var argument in new[]{"--offline","connection","modify","connection.autoconnect","yes"})info.ArgumentList.Add(argument);
                using var process=System.Diagnostics.Process.Start(info)!;
                await process.StandardInput.WriteAsync(fake.Profile);process.StandardInput.Close();
                var output=await process.StandardOutput.ReadToEndAsync();await process.WaitForExitAsync();
                var stored=output.Split('\n').SingleOrDefault(l=>l.StartsWith("psk="))?[4..].Replace("\\s"," ");
                check(process.ExitCode==0&&stored==Secret,"Real NetworkManager keyfile parsing preserves the entered Wi-Fi password");
            }
            check(fake.Calls.All(c=>!c.Contains(Secret))&&!File.ReadAllText(Path.Combine(root,"network-change.json")).Contains(Secret),"Wi-Fi password never enters process arguments or pending state");
            var rejected=false;try{await settings.ConnectWifi(request with{MacAddress="02:00:00:00:00:99"});}catch(InvalidOperationException){rejected=true;}
            check(rejected,"Wi-Fi rejects a replaced adapter before applying credentials");
            rejected=false;try{await settings.ConnectWifi(request with{Password="short"});}catch(InvalidOperationException){rejected=true;}
            check(rejected,"WPA2 validates the password before changing a connection");
            fake.Fail=true;var before=Directory.GetFiles(Path.Combine(root,"profiles")).Length;string error="";
            try{await settings.ConnectWifi(request);}catch(InvalidOperationException e){error=e.Message;}
            check(error.Length>0&&!error.Contains(Secret)&&Directory.GetFiles(Path.Combine(root,"profiles")).Length==before&&fake.RolledBack,"Failed Wi-Fi activation removes the rejected password profile, restores the checkpoint and hides command output");
        }
        finally{Directory.Delete(root,true);}
        var handler=new Menu();using var client=new HttpClient(handler){BaseAddress=new Uri("http://local")};var menu=new ConsoleMaintenance(client);
        await menu.Open("network");await menu.Select('w');
        check(menu.Screen.Id=="wifi-networks","A single Wi-Fi adapter skips the adapter chooser");
        await menu.Select((char)256);check(menu.Screen.Secret&&menu.Screen.InputValue=="","Secured SSIDs open a masked password field");
        LocalConsole.OpenMaintenance(menu.Screen);foreach(var c in Secret)LocalConsole.EditText(c);
        var frame=LocalConsole.ExportFrame(120,33);check(!frame.Contains(Secret)&&frame.Contains(new string('*',Secret.Length)),"Physical and mirrored console frames contain only password masks");
        await menu.Submit(Secret);check(handler.Request?.Password==Secret&&!menu.Screen.Body.Contains(Secret)&&menu.Screen.Body.Contains("Connected"),"Wi-Fi menu preserves password spaces and reports connection success without echoing credentials");
        var flow=new ConsoleNetwork(client,setup:true);await flow.Open();await flow.Select('w');await flow.Select((char)256);await flow.Submit(Secret);
        check(flow.Screen.Options[0].Key=='c',"Connected Wi-Fi offers Continue directly within the setup flow");
        await flow.Select((char)256);check(flow.Screen.Secret&&!flow.Screen.Options.Any(o=>o.Key=='c'),"Continue is hidden while editing another Wi-Fi password");await flow.Select('0');
        await flow.Select('c');check(flow.Closed&&flow.Completed,"Wi-Fi Continue completes the networking step without returning through menus");
        handler.Adapters=2;await menu.Open("network");await menu.Select('w');check(menu.Screen.Id=="wifi-adapters","Multiple Wi-Fi adapters require explicit selection");
        await menu.Select((char)257);check(handler.Scanned=="wlan1","Wi-Fi scan follows the chosen adapter");
        await menu.Select((char)257);check(handler.Request?.Password==""&&menu.Screen.Id=="wifi-networks","Open SSIDs connect without a password prompt");
        handler.Adapters=1;handler.PendingScan=new(TaskCreationOptions.RunContinuationsAsynchronously);await menu.Open("network");await menu.Select('w');
        check(menu.Screen.Id=="wifi-scanning"&&menu.Screen.Body.Contains("wlan0"),"Slow Wi-Fi scans show progress and adapter identity without blocking menu input");
        await menu.Select('0');check(menu.Screen.Id=="wifi-adapters","A pending Wi-Fi scan can be left without waiting for its timeout");
        handler.PendingScan.SetResult();handler.PendingScan=null;
        handler.Unavailable=true;handler.Scanned=null;await menu.Open("network");await menu.Select('w');
        check(menu.Screen.Id=="wifi-unavailable"&&handler.Scanned==null&&menu.Screen.Body.Contains("supplicant"),"Unavailable adapter shows its cause without entering a misleading scanning screen");
        handler.Unavailable=false;await menu.Select('v');check(menu.Screen.Id=="wifi-networks"&&handler.Scanned=="wlan0","Refreshing a recovered adapter scans normally");
        handler.Adapters=0;await menu.Open("network");await menu.Select('w');check(menu.Screen.Body.Contains("No Wi-Fi adapters"),"Missing Wi-Fi hardware has a useful menu state");
        check(DisplayConsoles.FontSize(["1280x720"])==16&&DisplayConsoles.FontSize(["1920x1080"])==16&&DisplayConsoles.FontSize(["2560x1440"])==32&&DisplayConsoles.FontSize(["3840x2160"])==48,"Console fonts scale to readable pixel sizes on HD, QHD and 4K displays");
        check(DisplayConsoles.FontSize(["3840x2160","1280x720"])==16&&DisplayConsoles.FontSize(["unknown"])==16,"Cloned or unknown displays retain a font that fits the smallest screen");
        LocalConsole.Show("Cache test","Current");var first=LocalConsole.ExportFrame(120,33);
        check(ReferenceEquals(first,LocalConsole.ExportFrame(120,33)),"Unchanged console frames reuse the rendered output");
        LocalConsole.Show("Cache test","Changed");check(LocalConsole.ExportFrame(120,33)!=first,"Menu changes invalidate the console frame cache");
        var longMenu=Enumerable.Range(1,80).Select(i=>"SSID "+i).ToArray();
        check(LocalConsole.Frame("Wi-Fi","Select a network",120,33,selectedOption:79,optionList:longMenu).Contains("80 SSID 80"),"Long SSID lists keep the selected option visible on a scaled console");
    }
    sealed class Nm
    {
        public List<string> Calls=[];public bool Fail,AutoConnect,RolledBack,Malformed,FirmwareMissing;public int EmptyReads,ScanReads;public string Profile="",State="100 (connected)";public UnixFileMode Permissions;
        string active="00000000-0000-4000-8000-000000000001",candidate="";
        public async Task<ProcessResult> Run(string exe,string[] args,int timeout)
        {
            var text=string.Join(' ',args);Calls.Add(text);
            if(args.Contains("--offline"))
            {
                candidate=args[Array.IndexOf(args,"connection.uuid")+1];
                return File.Exists("/usr/bin/nmcli")?await Processes.Run(exe,args,timeout):new(0,"[connection]\nuuid="+candidate+"\n[wifi]\nssid=Home\n[wifi-security]\nkey-mgmt=wpa-psk\n");
            }
            if(text.Contains("WIFI,WIFI-HW"))return new(0,"enabled:enabled");
            if(text.Contains("DEVICE,TYPE"))return new(0,"wlan0:wifi\n");
            if(text.Contains("GENERAL.STATE"))return new(0,State+"\n"+active+"\n/org/freedesktop/NetworkManager/Devices/1");
            if(text.Contains("GENERAL.PRODUCT"))return new(0,"Test adapter\ntest_wifi\n1.0\n"+(FirmwareMissing?"yes":"no")+"\n42 (The supplicant is not available)");
            if(text.Contains("PermHwAddress"))return new(0,"s \""+Mac+"\"");
            if(text.Contains("BSSID,SSID")){ScanReads++;return new(0,Malformed?"unreadable":EmptyReads-->0?"":"02\\:00\\:00\\:00\\:00\\:20:Home:WPA2:80");}
            if(text.Contains("CheckpointCreate"))return new(0,"o \"/org/freedesktop/NetworkManager/Checkpoint/1\"");
            if(text.StartsWith("connection load")){Profile=File.ReadAllText(args[^1]);Permissions=File.GetUnixFileMode(args[^1]);}
            if(text.Contains("connection up")){if(Fail)return new(1,Secret);active=candidate;}
            if(text.Contains("CheckpointRollback")){RolledBack=true;return new(0,"a{su} 1 \"/org/freedesktop/NetworkManager/Devices/1\" 0");}
            if(text.Contains("GENERAL.CON-UUID"))return new(0,active);
            if(text.Contains("connection modify"))AutoConnect=true;
            if(text.Contains("connection.id"))return new(0,"external-profile");
            return new(0,"");
        }
    }
    sealed class Menu:HttpMessageHandler
    {
        public bool Unavailable;public int Adapters=1;public WifiConnectRequest? Request;public string? Scanned;
        public TaskCompletionSource? PendingScan;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        {
            object result=new{};var path=request.RequestUri!.AbsolutePath;
            if(path=="/network/settings")result=new NetworkSettingsStatus([],null);
            if(path=="/network/wifi")result=new WifiStatus(true,true,Enumerable.Range(0,Adapters).Select(i=>new WifiAdapter("wlan"+i,Mac,Unavailable?"20 (unavailable)":"disconnected",null,"/device"+i,Reason:Unavailable?"42 (The supplicant is not available)":"")).ToArray());
            if(path.EndsWith("/scan")){Scanned=(await request.Content!.ReadFromJsonAsync<WifiScanRequest>())!.Interface;if(PendingScan!=null)await PendingScan.Task;result=new[]{new WifiNetwork("Home",Bssid,"WPA2",80,"wpa-psk"),new WifiNetwork("Guest",Bssid,"--",50,"open")};}
            if(path.EndsWith("/connect"))Request=await request.Content!.ReadFromJsonAsync<WifiConnectRequest>();
            return new(HttpStatusCode.OK){Content=JsonContent.Create(result)};
        }
    }
}
