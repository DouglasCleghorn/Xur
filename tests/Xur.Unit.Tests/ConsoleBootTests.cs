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
            var calls=new List<string>();
            Task<ProcessResult> Run(string exe,string[] args,int timeout){calls.Add(exe+" "+string.Join(' ',args));return Task.FromResult(new ProcessResult(0,exe=="tailscale"&&args[0]=="status"?"{\"BackendState\":\"Running\"}":""));}
            var name=new ComputerNameSettings(Path.Combine(root,"computer-name"),Run);
            check(!name.Read().Configured,"Computer name is prompted until explicitly saved");
            var result=await name.Set("Living-Room");
            check(result.Name=="living-room"&&name.Read().Configured&&new ComputerNameSettings(Path.Combine(root,"computer-name"),Run).Read().Name=="living-room","Computer name persists across agent restarts");
            check(calls.Contains("hostnamectl set-hostname living-room")&&calls.Contains("tailscale set --hostname=living-room"),"Computer name updates the host and an enrolled Tailscale node");
            foreach(var bad in new[]{"", "-bad", "bad-", "two words", "bad;reboot", "localhost",new string('x',64),"1234"})
            {var rejected=false;try{ComputerNameSettings.Validate(bad);}catch(InvalidOperationException){rejected=true;}check(rejected,"Invalid computer name is rejected before a host mutation");}
        }
        finally{Directory.Delete(root,true);}
        var connected=false;var transient=false;
        var hotplug=new Appliance(networkObserver:()=>transient?throw new System.Net.NetworkInformation.NetworkInformationException():[new("eno1",connected?"Up":"Down","Ethernet",connected?["192.0.2.25"]:[])]);
        using var hotplugAgent=hotplug.Agent;var hotplugAuth=new Bootstrap();LocalConsole.Status(hotplug,hotplugAuth);
        check(LocalConsole.ExportFrame(160,45).Contains("Waiting for network"),"Boot without an Ethernet cable shows that it is waiting for an address");
        connected=true;LocalConsole.Refresh();check(LocalConsole.ExportFrame(160,45).Contains("192.0.2.25:8443"),"Late Ethernet addresses appear on the open status screen without navigation");
        transient=true;LocalConsole.Refresh();transient=false;LocalConsole.Refresh();
        check(LocalConsole.ExportFrame(160,45).Contains("192.0.2.25:8443"),"A transient adapter observation failure does not stop subsequent address refreshes");
        connected=false;LocalConsole.Refresh();check(!LocalConsole.ExportFrame(160,45).Contains("192.0.2.25:8443"),"Unplugged interfaces stop advertising stale management addresses");
        var running=false;var configured=false;var serveStarts=0;
        Task<ProcessResult> Tailscale(string exe,string[] args,int timeout)
        {
            if(args[0]=="serve")
            {
                if(args[1]!="status"){configured=true;serveStarts++;return Task.FromResult(new ProcessResult(0,""));}
                var configuration=new{TCP=new Dictionary<string,object>{{"443",new{HTTPS=true}}},Web=new Dictionary<string,object>{{"console.example.ts.net:443",new{Handlers=new Dictionary<string,object>{{"/",new{Proxy="unix:"+Path.Combine(Environment.GetEnvironmentVariable("XUR_RUN")??"/run/xur","serve.sock")}}}}}}};
                return Task.FromResult(new ProcessResult(0,configured?System.Text.Json.JsonSerializer.Serialize(configuration):"{}"));
            }
            return Task.FromResult(new ProcessResult(0,running?"{\"BackendState\":\"Running\",\"Self\":{\"DNSName\":\"console.example.ts.net.\"}}":"{\"BackendState\":\"NeedsLogin\"}"));
        }
        var appliance=new Appliance(Tailscale);
        using var agent=appliance.Agent;var auth=new Bootstrap();
        LocalConsole.Status(appliance,auth);LocalConsole.OpenQr(true);LocalConsole.AppendQr("Enrolling…");
        running=true;await appliance.RefreshTailscale();LocalConsole.EndQr("Serve ready");LocalConsole.EndQr("Enrollment finished");
        check(appliance.TailServeReady&&serveStarts==1,"An enrolled node repairs missing HTTPS Serve configuration without enrolling again");
        var frame=LocalConsole.ExportFrame(160,45);
        check(frame.Contains("Scan to open Xur")&&frame.Contains('█')&&!frame.Contains("Enrollment finished"),"Enrollment completion immediately replaces the enrollment QR with the Serve login QR");
        running=false;await appliance.RefreshTailscale();LocalConsole.Status(appliance,auth);var before=LocalConsole.ExportFrame(160,45);
        running=true;await appliance.RefreshTailscale();LocalConsole.Refresh();var after=LocalConsole.ExportFrame(160,45);
        check(after!=before&&after.Contains('█'),"Serve QR appears on an already-open status screen when the address arrives");
        LocalConsole.OpenLogs();var logs=LocalConsole.ExportFrame(160,45);
        check(logs.Contains("> Back to menu")&&LocalConsole.Navigate(ConsoleKeyAction.Enter)=='0'&&LocalConsole.Navigate(ConsoleKeyAction.Back)=='0',"Logs retain a visible Back action and accept Enter or Escape without switching VTs");
        LocalConsole.Status(appliance,auth);check(LocalConsole.ExportFrame(160,45).Contains("Status and login"),"Leaving logs restores the menu and management addresses");
        var unavailable=new Appliance((exe,args,timeout)=>Task.FromResult(args[0]=="serve"?new ProcessResult(1,"disabled"):new ProcessResult(0,"{\"BackendState\":\"Running\",\"Self\":{\"DNSName\":\"console.example.ts.net.\"}}")));
        using var unavailableAgent=unavailable.Agent;await unavailable.RefreshTailscale();
        LocalConsole.Status(unavailable,auth);LocalConsole.OpenQr(true);
        check(!unavailable.TailServeReady&&LocalConsole.ExportFrame(160,45).Contains("HTTPS proxy unavailable"),"Failed HTTPS setup is visible instead of advertising a ready Serve QR");

    }
}
