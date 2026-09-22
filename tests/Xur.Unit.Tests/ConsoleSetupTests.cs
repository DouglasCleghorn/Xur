using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xur.Control;
using Xur.Domain;

static class ConsoleSetupTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../.build/evidence/setup-"+Guid.NewGuid().ToString("N")[..8]));Directory.CreateDirectory(root);
        var oldRun=Environment.GetEnvironmentVariable("XUR_RUN");var oldMode=Environment.GetEnvironmentVariable("XUR_MODE");
        Environment.SetEnvironmentVariable("XUR_RUN",root);Environment.SetEnvironmentVariable("XUR_MODE","Installer");
        var agentBuilder=WebApplication.CreateBuilder();agentBuilder.Logging.ClearProviders();agentBuilder.WebHost.ConfigureKestrel(k=>k.ListenUnixSocket(root+"/agent.sock"));
        await using var agent=agentBuilder.Build();
        var disk=new Disk("/dev/test","/dev/disk/by-id/test","TEST-001","wwn-test","Test SSD",64L<<30,"old layout",[],[]);
        var blocked=disk with{Path="/dev/usb",Serial="USB",Blocked=["Boot media"]};
        Operation? operation=null;int approvals=0;bool changed=false,expired=false;var savedName=new ComputerNameStatus("xur",false);
        agent.MapGet("/status",()=>new{scan=new{state="NoAnswer"},operation});
        agent.MapGet("/disks",()=>new Inventory("generation",[disk,blocked]));
        agent.MapGet("/computer-name",()=>savedName);
        agent.MapPost("/computer-name",(ComputerNameRequest request)=>savedName=new(request.Name,true));
        agent.MapPost("/plan",(ConsolePlanRequest request)=>new InstallPlan("plan","digest","generation",changed?disk with{Serial="REPLACEMENT"}:disk,[blocked],["Erase selected disk","Install OS"],DateTimeOffset.UtcNow.AddMinutes(expired?-1:5)));
        agent.MapPost("/approve",(Approval request)=>{if(request!=new Approval("plan","digest"))return Results.Conflict();approvals++;operation=new("plan","Installing","Installing approved disk",DateTimeOffset.UtcNow);return Results.Json(operation);});
        var controlBuilder=WebApplication.CreateBuilder();controlBuilder.Logging.ClearProviders();controlBuilder.WebHost.ConfigureKestrel(k=>k.ListenUnixSocket(root+"/control.sock"));
        await using var control=controlBuilder.Build();var device=new Appliance();using var agentClient=device.Agent;var auth=new Bootstrap(directory:root);
        control.MapConsoleSetup(device);control.MapNetworkSettings(device);
        try
        {
            await agent.StartAsync();await control.StartAsync();using var client=LocalClient.Create(root+"/control.sock");
            using(var denied=await client.PostAsJsonAsync("/local/setup/plan",new ConsolePlanRequest(disk.Path)))check(denied.StatusCode==HttpStatusCode.Conflict,"Console installation requires a saved server name before planning");
            check(!await device.StartWebLogin()&&!device.QrRunning&&device.EnrollmentError.Contains("after installation"),"Tailscale enrollment is blocked throughout live installation");
            var menu=new ConsoleMaintenance(client,true,true);await menu.Open("setup");await menu.Select('n');await menu.Submit("living-room");await menu.Select('0');
            check(savedName is {Configured:true,Name:"living-room"},"Console setup saves the server name without a web browser");
            check(!menu.Screen.Options.Any(o=>o.Key=='a')&&!auth.AccountConfigured,"Device setup leaves required account creation to the installed web manager");
            using(var removed=await client.PostAsJsonAsync("/local/setup/account",new {username="other",password="password"}))check(removed.StatusCode==HttpStatusCode.NotFound,"Console cannot create the browser administrator account");
            check(device.Urls().Length==0,"Live installer never advertises web management URLs");
            await menu.Select('d');check(menu.Screen.Options.Single(o=>o.Key==(char)257).Enabled==false&&menu.Screen.Body.Contains("Boot media"),"Console disk selection identifies and disables blocked boot media");
            await menu.Select((char)257);check(approvals==0&&menu.Screen.Id=="setup-disks","Blocked disks cannot be selected from the console");
            changed=true;await menu.Select((char)256);check(menu.Screen.Id=="setup-disks"&&menu.Screen.Body.Contains("identity changed"),"A replaced disk invalidates the console selection before review");changed=false;
            await menu.Select((char)256);check(menu.Screen.Body.Contains("TEST-001")&&menu.Screen.Body.Contains("Erase selected disk")&&menu.Screen.Options[0].Key=='0',"Disk review shows identity, destructive actions and cancellation as the default");
            await menu.Select('y');check(menu.Screen.InputValue==null&&menu.Screen.Options.Select(o=>o.Label).SequenceEqual(["No","Yes"])&&menu.Screen.Options[0].Key=='0',"Erase confirmation offers No and Yes with No first and no text input");
            await menu.Submit("ERASE /dev/test");check(approvals==0&&menu.Screen.Id=="setup-confirm","Text input cannot approve disk erasure");
            await menu.Select('0');check(approvals==0&&menu.Screen.Id=="setup-home","Cancelling erase confirmation leaves disks unchanged");
            expired=true;await menu.Select('d');await menu.Select((char)256);check(!menu.Screen.Options.Single(o=>o.Key=='y').Enabled,"Expired disk plans cannot advance to erase confirmation");
            await menu.Select('0');expired=false;await menu.Select('d');await menu.Select((char)256);await menu.Select('y');await menu.Select('y');await menu.Select('y');
            check(approvals==1&&menu.Screen.Id=="setup-progress"&&menu.Screen.Body.Contains("Installing approved disk"),"Yes confirmation sends exactly one approval and opens live progress");
            operation=operation! with{Stage="Complete",Message="Installation completed"};await menu.Refresh();check(menu.Screen.Options.Any(o=>o.Key=='r'),"Console installation completion offers an explicit reboot action");
            operation=operation with{Stage="Failed",Message="Download failed"};await menu.Refresh();check(!menu.Screen.Options.Any(o=>o.Key=='r')&&menu.Screen.Body.Contains("No automatic retry"),"Console installation failure remains visible without retrying erasure or rebooting");
        }
        finally{await control.StopAsync();await agent.StopAsync();Environment.SetEnvironmentVariable("XUR_RUN",oldRun);Environment.SetEnvironmentVariable("XUR_MODE",oldMode);Directory.Delete(root,true);}
    }
}
