using Xur.Agent;
using Xur.Control;
using Xur.Domain;
static class ConsoleBootTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../.build/evidence/name-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        try
        {
            var statusDirectory=Path.Combine(root,"app");Directory.CreateDirectory(statusDirectory);
            File.WriteAllText(Path.Combine(statusDirectory,"check.json"),"{\"state\":\"unavailable\"}");
            check(InstallerAppStatus.Message(root).Contains("Setup remains available"),"Offline update checks clearly retain local setup availability");
            File.WriteAllText(Path.Combine(statusDirectory,"check.json"),"{\"state\":\"available\"}");
            check(InstallerAppStatus.Message(root).Contains("after installation"),"Available installer updates do not imply an automatic setup restart");
            File.WriteAllText(Path.Combine(statusDirectory,"check.json"),"{");
            check(InstallerAppStatus.Message(root).Contains("do not delay"),"An incomplete update status file cannot break the console");
            var calls=new List<string>();
            Task<ProcessResult> Run(string exe,string[] args,int timeout){calls.Add(exe+" "+string.Join(' ',args));return Task.FromResult(new ProcessResult(0,exe=="tailscale"&&args[0]=="status"?"{\"BackendState\":\"Running\"}":""));}
            var name=new ComputerNameSettings(Path.Combine(root,"computer-name"),Run);
            check(!name.Read().Configured,"Computer name is prompted until explicitly saved");
            var result=await name.Set("Living-Room");
            check(result.Name=="living-room"&&name.Read().Configured&&new ComputerNameSettings(Path.Combine(root,"computer-name"),Run).Read().Name=="living-room","Computer name persists across agent restarts");
            check(calls.Contains("hostnamectl set-hostname living-room")&&calls.Contains("tailscale set --hostname=living-room"),"Computer name updates the host and an enrolled Tailscale node");
            calls.Clear();await new ComputerNameSettings(Path.Combine(root,"installer-name"),Run,updateTailscale:false).Set("setup-server");
            check(!calls.Any(c=>c.StartsWith("tailscale")),"Naming the live installer never starts Tailscale");
            foreach(var bad in new[]{"", "-bad", "bad-", "two words", "bad;reboot", "localhost",new string('x',64),"1234"})
            {var rejected=false;try{ComputerNameSettings.Validate(bad);}catch(InvalidOperationException){rejected=true;}check(rejected,"Invalid computer name is rejected before a host mutation");}
        }
        finally{Directory.Delete(root,true);}
        var connected=false;var transient=false;var lanAddress="192.0.2.25";
        var hotplug=new Appliance(networkObserver:()=>transient?throw new System.Net.NetworkInformation.NetworkInformationException():[new("eno1",connected?"Up":"Down","Ethernet",connected?[lanAddress]:[])]);
        using var hotplugAgent=hotplug.Agent;var hotplugAuth=new Bootstrap();LocalConsole.Status(hotplug,hotplugAuth);
        check(LocalConsole.ExportFrame(160,45).Contains("Waiting for network"),"Boot without an Ethernet cable shows that it is waiting for an address");
        connected=true;check(LocalConsole.ExportFrame(160,45).Contains("192.0.2.25:8443"),"Native frame requests show late Ethernet addresses even without a background refresh or navigation");
        transient=true;LocalConsole.Refresh();transient=false;LocalConsole.Refresh();
        check(LocalConsole.ExportFrame(160,45).Contains("192.0.2.25:8443"),"A transient adapter observation failure does not stop subsequent address refreshes");
        connected=false;LocalConsole.Refresh();check(!LocalConsole.ExportFrame(160,45).Contains("192.0.2.25:8443"),"Unplugged interfaces stop advertising stale management addresses");
        connected=true;LocalConsole.OpenNetwork();LocalConsole.Navigate(ConsoleKeyAction.Down);
        var networkSelection=LocalConsole.DiagnosticSnapshot().Selected;
        lanAddress="192.0.2.26";
        var liveNetwork=LocalConsole.ExportFrame(160,45);
        check(liveNetwork.Contains("192.0.2.26")&&!liveNetwork.Contains("192.0.2.25")&&LocalConsole.DiagnosticSnapshot().Screen=="network"&&LocalConsole.DiagnosticSnapshot().Selected==networkSelection,"A pulled network frame replaces the address without navigation or resetting selection");
        LocalConsole.UpdateLogs("Background log while reading network");LocalConsole.Refresh();
        check(LocalConsole.DiagnosticSnapshot().Screen=="network"&&LocalConsole.ExportFrame(160,45).Contains("192.0.2.26"),"Background log and refresh updates keep the current network screen open");
        var running=false;var configured=false;var serveStarts=0;var tailHost="console.example.ts.net";
        Task<ProcessResult> Tailscale(string exe,string[] args,int timeout)
        {
            if(args[0]=="serve")
            {
                if(args[1]!="status"){configured=true;serveStarts++;return Task.FromResult(new ProcessResult(0,""));}
                var configuration=new{TCP=new Dictionary<string,object>{{"443",new{HTTPS=true}}},Web=new Dictionary<string,object>{{tailHost+":443",new{Handlers=new Dictionary<string,object>{{"/",new{Proxy="unix:"+Path.Combine(Environment.GetEnvironmentVariable("XUR_RUN")??"/run/xur","serve.sock")}}}}}}};
                return Task.FromResult(new ProcessResult(0,configured?System.Text.Json.JsonSerializer.Serialize(configuration):"{}"));
            }
            return Task.FromResult(new ProcessResult(0,running?System.Text.Json.JsonSerializer.Serialize(new{BackendState="Running",Self=new{DNSName=tailHost+"."}}):"{\"BackendState\":\"NeedsLogin\"}"));
        }
        var appliance=new Appliance(Tailscale);
        using var agent=appliance.Agent;var auth=new Bootstrap();
        LocalConsole.Status(appliance,auth);LocalConsole.OpenQr(true);LocalConsole.AppendQr("Enrolling…");
        running=true;await appliance.RefreshTailscale();LocalConsole.EndQr("Serve ready");LocalConsole.EndQr("Enrollment finished");
        check(appliance.TailServeReady&&serveStarts==1,"An enrolled node repairs missing HTTPS Serve configuration without enrolling again");
        var frame=LocalConsole.ExportFrame(160,45);
        check(frame.Contains("Status and login")&&frame.Contains('█')&&!frame.Contains("Enrollment finished"),"Successful enrollment returns home with management information instead of remaining on the QR screen");
        LocalConsole.OpenQr(true);LocalConsole.Refresh();check(LocalConsole.ExportFrame(160,45).Contains("Scan to open Xur"),"Opening the login QR after enrollment stays on the explicitly selected QR screen");
        running=false;await appliance.RefreshTailscale();LocalConsole.Status(appliance,auth);var before=LocalConsole.ExportFrame(160,45);
        running=true;await appliance.RefreshTailscale();LocalConsole.Refresh();var after=LocalConsole.ExportFrame(160,45);
        check(after!=before&&after.Contains('█'),"Serve QR appears on an already-open status screen when the address arrives");
        LocalConsole.OpenQr(true);var originalQr=LocalConsole.ExportFrame(160,45);
        tailHost="renamed.example.ts.net";await appliance.RefreshTailscale();
        var replacedQr=LocalConsole.ExportFrame(160,45);
        check(replacedQr!=originalQr&&replacedQr.Contains('█')&&LocalConsole.DiagnosticSnapshot().Screen=="qr","A changed Serve login URL replaces the QR on the already-open QR screen");
        running=false;await appliance.RefreshTailscale();
        check(!LocalConsole.ExportFrame(160,45).Contains('█')&&LocalConsole.DiagnosticSnapshot().Screen=="qr","Losing the Serve address removes the stale QR without navigating away");
        running=true;await appliance.RefreshTailscale();
        check(LocalConsole.ExportFrame(160,45).Contains('█')&&LocalConsole.DiagnosticSnapshot().Screen=="qr","The QR returns on the same screen when Serve reconnects");
        LocalConsole.OpenLogs();var logs=LocalConsole.ExportFrame(160,45);
        check(logs.Contains("> Back to menu")&&LocalConsole.Navigate(ConsoleKeyAction.Enter)=='0'&&LocalConsole.Navigate(ConsoleKeyAction.Back)=='0',"Logs retain a visible Back action and accept Enter or Escape without switching VTs");
        LocalConsole.Status(appliance,auth);check(LocalConsole.ExportFrame(160,45).Contains("Status and login"),"Leaving logs restores the menu and management addresses");
        var unavailable=new Appliance((exe,args,timeout)=>Task.FromResult(args[0]=="serve"?new ProcessResult(1,"disabled"):new ProcessResult(0,"{\"BackendState\":\"Running\",\"Self\":{\"DNSName\":\"console.example.ts.net.\"}}")));
        using var unavailableAgent=unavailable.Agent;await unavailable.RefreshTailscale();
        LocalConsole.Status(unavailable,auth);LocalConsole.OpenQr(true);
        check(!unavailable.TailServeReady&&LocalConsole.ExportFrame(160,45).Contains("HTTPS proxy unavailable"),"Failed HTTPS setup is visible instead of advertising a ready Serve QR");

    }
}
