using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xur.Control;
using Xur.Domain;

static class ConsoleSetupTests
{
    public static async Task Run(Action<bool,string> check,string? captureDirectory=null)
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../.build/evidence/setup-"+Guid.NewGuid().ToString("N")[..8]));Directory.CreateDirectory(root);
        var oldRun=Environment.GetEnvironmentVariable("XUR_RUN");var oldMode=Environment.GetEnvironmentVariable("XUR_MODE");
        Environment.SetEnvironmentVariable("XUR_RUN",root);Environment.SetEnvironmentVariable("XUR_MODE","Installer");
        var agentBuilder=WebApplication.CreateBuilder();agentBuilder.Logging.ClearProviders();agentBuilder.WebHost.ConfigureKestrel(k=>k.ListenUnixSocket(root+"/agent.sock"));
        await using var agent=agentBuilder.Build();
        var disk=new Disk("/dev/test","/dev/disk/by-id/test","TEST-001","wwn-test","Test SSD",64L<<30,"old layout",[],[]);
        var blocked=disk with{Path="/dev/usb",Serial="USB",Blocked=["Boot media"]};
        Operation? operation=null;int approvals=0;bool changed=false,expired=false;var savedName=new ComputerNameStatus("xur",false);NetworkChange? pending=null;
        int exports=0;agent.MapGet("/installation-logs/usb",()=>new[]{new UsbLogVolume("usb-id","/dev/usb1","Logs","Test USB",false)});
        agent.MapPost("/installation-logs/usb",(UsbLogRequest request)=>{if(request.Id!="usb-id")return Results.BadRequest();exports++;return Results.Json(new UsbLogReceipt("Saved report on USB."));});
        agent.MapGet("/installation-logs",()=>Results.Text("anaconda.log\nError: installation fixture\npassword=private-fixture\n"+string.Join('\n',Enumerable.Range(0,30).Select(n=>"Log line "+n))));
        agent.MapGet("/status",()=>new{scan=new{state="NoAnswer"},operation});
        agent.MapGet("/network/settings",()=>new NetworkSettingsStatus([],pending));
        agent.MapGet("/disks",()=>new Inventory("generation",[disk,blocked]));
        agent.MapGet("/computer-name",()=>savedName);
        agent.MapPost("/computer-name",(ComputerNameRequest request)=>string.IsNullOrWhiteSpace(request.Name)?Results.BadRequest(new{error="Enter a server name."}):Results.Json(savedName=new(request.Name,true)));
        agent.MapPost("/plan",(ConsolePlanRequest request)=>new InstallPlan("plan","digest","generation",changed?disk with{Serial="REPLACEMENT"}:disk,[blocked],["Erase selected disk","Install OS"],DateTimeOffset.UtcNow.AddMinutes(expired?-1:5)));
        agent.MapPost("/approve",(Approval request)=>{if(request!=new Approval("plan","digest"))return Results.Conflict();approvals++;operation=new("plan","Installing","Installing approved disk",DateTimeOffset.UtcNow);return Results.Json(operation);});
        var controlBuilder=WebApplication.CreateBuilder();controlBuilder.Logging.ClearProviders();controlBuilder.WebHost.ConfigureKestrel(k=>k.ListenUnixSocket(root+"/control.sock"));
        await using var control=controlBuilder.Build();var device=new Appliance();using var agentClient=device.Agent;var auth=new Bootstrap(directory:root);
        control.MapConsoleSetup(device);control.MapNetworkSettings(device);
        try
        {
            await agent.StartAsync();await control.StartAsync();using var client=LocalClient.Create(root+"/control.sock");
            LocalConsole.Status(device,auth);
            File.WriteAllText(Path.Combine(root,"diagnostics-status.json"),JsonSerializer.Serialize(new InstallerDiagnosticsStatus(ApiEnabled:true)));
            LocalConsole.OpenMaintenance(new("setup-confirm","Confirm disk erasure","Disk serial TEST-001",[new('0',"No"),new('y',"Yes")]));
            check(LocalConsole.ExportFrame(80,25).Contains(InstallerDiagnosticWarning.Banner)&&LocalConsole.DiagnosticSnapshot().Body.Contains(InstallerDiagnosticWarning.Banner),"Physical disk confirmation and diagnostic snapshots both warn when diagnostics are active");
            File.Delete(Path.Combine(root,"diagnostics-status.json"));
            check(!LocalConsole.ExportFrame(80,25).Contains(InstallerDiagnosticWarning.Banner),"Removing the active status refreshes a cached console frame immediately");
            using(var denied=await client.PostAsJsonAsync("/local/setup/plan",new ConsolePlanRequest(disk.Path)))check(denied.StatusCode==HttpStatusCode.Conflict,"Console installation requires a saved server name before planning");
            check(!await device.StartWebLogin()&&!device.QrRunning&&device.EnrollmentError.Contains("after installation"),"Tailscale enrollment is blocked throughout live installation");
            var menu=new ConsoleMaintenance(client,true,true);await menu.Open("setup");check(menu.Screen.Title.Contains("Step 1")&&menu.Screen.InputAction=="Continue"&&menu.Screen.InputLabel=="Server name","Initial setup starts at the server-name step with a labeled field and Continue action");await menu.Submit("");check(menu.Screen.Id=="computer-name"&&menu.Screen.Body.Contains("Enter a server name"),"A rejected server name keeps the flow on the naming step");await menu.Submit("living-room");
            check(menu.Screen.Id=="network-list"&&menu.Screen.Title.Contains("Step 2"),"Saving the name advances directly to networking");
            await menu.Select('0');check(menu.Screen.Id=="computer-name","Back from networking edits the name without leaving setup");await menu.Submit("living-room");
            check(savedName is {Configured:true,Name:"living-room"},"Console setup saves the server name without a web browser");
            check(!menu.Screen.Options.Any(o=>o.Key=='a')&&!auth.AccountConfigured,"Device setup leaves required account creation to the installed web manager");
            using(var removed=await client.PostAsJsonAsync("/local/setup/account",new {username="other",password="password"}))check(removed.StatusCode==HttpStatusCode.NotFound,"Console cannot create the browser administrator account");
            check(device.Urls().Length==0,"Live installer never advertises web management URLs");
            pending=new("pending","eno1","uuid",null,"checkpoint",DateTimeOffset.UtcNow.AddMinutes(2),"Confirm","Keep this change",[]);
            await menu.Select('c');check(menu.Screen.Id=="network-list","A newly pending network change cannot be skipped by Continue");
            pending=null;await menu.Refresh();
            await menu.Select('c');check(menu.Screen.Title.Contains("Step 3"),"Continue from networking opens disk selection");check(menu.Screen.Options.Single(o=>o.Key==(char)257).Enabled==false&&menu.Screen.Body.Contains("Boot media"),"Console disk selection identifies and disables blocked boot media");
            await menu.Select('0');check(menu.Screen.Id=="network-list","Back from disk selection returns to networking within setup");await menu.Select('c');
            await menu.Select((char)257);check(approvals==0&&menu.Screen.Id=="setup-disks","Blocked disks cannot be selected from the console");
            changed=true;await menu.Select((char)256);check(menu.Screen.Id=="setup-disks"&&menu.Screen.Body.Contains("identity changed"),"A replaced disk invalidates the console selection before review");changed=false;
            await menu.Select((char)256);check(menu.Screen.Body.Contains("TEST-001")&&menu.Screen.Body.Contains("Erase selected disk")&&menu.Screen.Options[0].Key=='0',"Disk review shows identity, destructive actions and cancellation as the default");
            await menu.Select('y');check(menu.Screen.InputValue==null&&menu.Screen.Options.Select(o=>o.Label).SequenceEqual(["No","Yes"])&&menu.Screen.Options[0].Key=='0',"Erase confirmation offers No and Yes with No first and no text input");
            await menu.Submit("ERASE /dev/test");check(approvals==0&&menu.Screen.Id=="setup-confirm","Text input cannot approve disk erasure");
            await menu.Select('0');check(approvals==0&&menu.Screen.Id=="setup-disks","Cancelling erase confirmation leaves disks unchanged");
            expired=true;await menu.Select((char)256);check(!menu.Screen.Options.Single(o=>o.Key=='y').Enabled,"Expired disk plans cannot advance to erase confirmation");
            await menu.Select('0');expired=false;await menu.Select((char)256);await menu.Select('y');await menu.Select('y');await menu.Select('y');
            check(approvals==1&&menu.Screen.Id=="setup-progress"&&menu.Screen.Body.Contains("Installing approved disk"),"Yes confirmation sends exactly one approval and opens live progress");
            operation=operation! with{Progress=new(2,"Download OS image","Downloading 128 image layers (5.6 GB).")};await menu.Refresh();
            check(menu.Screen.Body.Contains("[########------------] 2/5 stages complete")&&menu.Screen.Body.Contains("128 image layers (5.6 GB)")&&!menu.Screen.Body.Contains("percentage unavailable"),"Console shows useful download detail alongside completed installation stages");
            var native="Fetching layers █░ 10/128\n└ Fetching                  █░ 50.26\u00a0MiB/115.13 MiB (32.94 MiB/s) ostree chunk "+new string('a',64);
            operation=operation with{Message="Anaconda is installing the approved disk\n\nAnaconda:\nDeploying image: # Initializing ostree layout\nDeploying image: # Waiting for sysroot lock...\nDeploying image: # layers already present: 0; layers needed: 128 (5.6 GB)",Progress=operation.Progress! with{Detail=native}};await menu.Refresh();
            var progressBody=menu.Screen.Body;
            check(progressBody.StartsWith("Installing · Download OS image\n[########------------] 2/5 stages complete\nTime check")&&progressBody.IndexOf("Layers:")<progressBody.IndexOf("Anaconda output:"),"Current stage and overall progress precede download counters and recent activity");
            check(progressBody.Contains("Layers: 10 / 128\nLayer: 50.26 MiB / 115.13 MiB\nRate: 32.94 MiB/s")&&!progressBody.Contains("ostree chunk")&&!progressBody.Contains("Deploying image:")&&progressBody.Contains("────────────────────────\nAnaconda output:\nInitializing ostree layout\nWaiting for sysroot lock..."),"Console aligns real transfer counters and separates external output without native padding, duplicate prefixes or chunk identifiers");
            foreach(var (width,height) in new[]{(40,20),(80,25),(100,40),(140,50)})
            {
                var rendered=LocalConsole.Frame(menu.Screen.Title,progressBody,width,height,optionList:menu.Screen.Options.Select(o=>o.Display).ToArray(),diagnosticsActive:true);
                var rows=Regex.Split(rendered,@"\x1b\[\d+;1H").Skip(1).Select(LocalConsole.Clean).ToArray();
                check(rows.Length==height&&rows.All(row=>row.Length==width)&&rows.Any(row=>row.Contains(InstallerDiagnosticWarning.Banner)),"Installer progress stays within terminal bounds and retains its diagnostic warning at "+width+"x"+height);
                if(width>=80)check(LocalConsole.Clean(rendered).Contains("Layers: 10 / 128")&&LocalConsole.Clean(rendered).Contains("Rate: 32.94 MiB/s")&&LocalConsole.Clean(rendered).Contains("Anaconda output:")&&LocalConsole.Clean(rendered).Contains("Initializing ostree layout"),"Transfer counters and labelled external activity remain visible on the first progress page at "+width+"x"+height);
            }
            Capture(menu.Screen,"download",captureDirectory);
            operation=operation with{Progress=operation.Progress! with{Detail="Fetching layers █ 128/128"}};await menu.Refresh();
            check(menu.Screen.Body.Contains("Layers: 128 / 128")&&!menu.Screen.Body.Contains("Layer:")&&!menu.Screen.Body.Contains("Rate:"),"A completed native layer counter does not invent a current transfer");
            operation=operation with{Progress=new(3,"Deploy OS and bootloader"),Message="Anaconda is installing the approved disk\n\nAnaconda:\nDeploying image: # Deploying container image\nDeploying image: # Deploying container image\nDeploying image: # Deploying container image"};await menu.Refresh();
            check(menu.Screen.Body.Contains("3/5 stages complete")&&!menu.Screen.Body.Contains("Layers:")&&menu.Screen.Body.EndsWith("Anaconda output:\nDeploying container image"),"Deployment clears transfer details and collapses consecutive native activity repeats");
            Capture(menu.Screen,"deploy",captureDirectory);
            await menu.Select('0');await menu.Open("setup");check(menu.Screen.Id=="setup-progress","Reopening setup resumes an existing installation");
            operation=operation! with{Stage="Complete",Message="Installation completed"};await menu.Refresh();check(menu.Screen.Options.Any(o=>o.Key=='r'),"Console installation completion offers an explicit reboot action");
            check(menu.Screen.Body.Contains("[####################] 5/5 stages complete"),"Only confirmed installation success fills the progress bar");
            Capture(menu.Screen,"complete",captureDirectory);
            operation=operation with{Stage="Failed",Message="Download failed",Progress=new(2,"Download OS image",native)};await menu.Refresh();check(!menu.Screen.Options.Any(o=>o.Key=='r')&&menu.Screen.Body.Contains("No automatic retry")&&!menu.Screen.Body.Contains("Rate:"),"Console installation failure remains visible without stale transfer counters, retrying erasure or rebooting");
            Capture(menu.Screen,"failed",captureDirectory);
            await menu.Select('s');check(menu.Screen.Id=="setup-usb"&&menu.Screen.Options.Any(o=>o.Label.Contains("/dev/usb1")),"Failed installation offers eligible USB log destinations");
            await menu.Select((char)256);check(exports==1&&menu.Screen.Body.Contains("Saved report"),"Selecting a USB destination exports logs and shows the receipt");await menu.Select('0');
            await menu.Select('l');check(menu.Screen.Id=="setup-logs"&&menu.Screen.Body.Contains("Error: installation fixture")&&!menu.Screen.Body.Contains("private-fixture"),"Failure details open installation logs directly and redact credentials");
            var logPage=menu.Screen.Body;await menu.Refresh();check(menu.Screen.Body==logPage&&logPage.Contains("Log line 29"),"Installation logs retain the complete report for terminal-sized pagination");
            var wide=LocalConsole.Clean(LocalConsole.Frame(menu.Screen.Title,logPage,140,70,optionList:menu.Screen.Options.Select(o=>o.Display).ToArray()));
            var shortFrame=LocalConsole.Clean(LocalConsole.Frame(menu.Screen.Title,logPage,140,24,optionList:menu.Screen.Options.Select(o=>o.Display).ToArray()));
            check(wide.Contains("Log line 29")&&wide.Contains("Error: installation fixture")&&!shortFrame.Contains("Log line 29")&&shortFrame.Contains("PgUp/PgDn"),"Log pagination fills a tall display and adapts to shorter terminals");
            await menu.Select('0');check(menu.Screen.Id=="setup-progress"&&approvals==1,"Back from installation logs preserves failure without repeating approval");
        }
        finally{await control.StopAsync();await agent.StopAsync();Environment.SetEnvironmentVariable("XUR_RUN",oldRun);Environment.SetEnvironmentVariable("XUR_MODE",oldMode);Directory.Delete(root,true);}
    }
    static void Capture(ConsoleScreen screen,string phase,string? directory)
    {
        if(directory==null)return;
        Directory.CreateDirectory(directory);
        foreach(var (width,height) in new[]{(40,20),(80,25),(100,40),(140,50)})
            File.WriteAllText(Path.Combine(directory,$"{phase}-{width}x{height}.ansi"),LocalConsole.Frame(screen.Title,screen.Body,width,height,optionList:screen.Options.Select(o=>o.Display).ToArray(),diagnosticsActive:true));
    }
}
