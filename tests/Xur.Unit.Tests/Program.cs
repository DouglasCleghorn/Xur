using Xur.Control;
using Xur.Domain;
if(args is ["--terminal-probe",var terminal])
{
    string Tty() => File.ReadAllText("/proc/self/stat").Split(") ",2)[1].Split(' ')[4];
    if(Tty()!="0")throw new Exception("Probe must start without a controlling terminal");
    using(var output=LocalConsole.OpenDevice(terminal,FileAccess.Write))
    using(var input=LocalConsole.OpenDevice(terminal,FileAccess.Read))
    {
        LocalConsole.ConfigureInput(output);
        if(Tty()!="0")throw new Exception("Opening console acquired a controlling terminal");
    }
    Console.WriteLine("Console daemon remains detached from the terminal");return;
}
if(args is ["--updates-render",var renderOutput]) { await UpdatesRender.Run(renderOutput);return; }
if(args is ["--workload-settings-smoke"]) { await WorkloadSettingsSmoke.Run();return; }
if(args is ["--station-units-smoke"]) { await StationUnitsTests.Smoke();return; }
if(args is ["--cancellation-render",var cancelOutput]) { await CancellationRender.Run(cancelOutput);return; }
if(args is ["--control-panel-render",var homeOutput]) { await ControlPanelRender.Run(homeOutput);return; }
var results = new List<string>();
void Check(bool value,string name) { if(!value) throw new Exception(name); results.Add(name); }
await StationIdentityTests.Run(Check);
HuggingFaceTests.Run(Check);
AccountTests.Run(Check);
WorkstationUpdateTests.Run(Check);
await NvidiaDeviceTests.Run(Check);
await NvLinkTopologyTests.Run(Check);
StationStreamingTests.Run(Check);
Check(Xur.Agent.ServerPower.Configuration.Contains("--what=sleep:idle")&&!Xur.Agent.ServerPower.Configuration.Contains("--what=shutdown"),"Server sleep protection leaves reboot and shutdown available");
Check(LocalConsole.WebAddresses([]).Contains("Waiting for network"),"Console explains network address acquisition instead of showing an empty list");
Check(LocalConsole.WebAddresses(["https://192.0.2.1:8443/"]).Contains("192.0.2.1"),"Console shows acquired addresses immediately");
await StationGraphicsTests.Run(Check);
await StationUnitsTests.Run(Check);
await HeadlessStationTests.Run(Check);
await ModelLabTests.Run(Check);
await ModelLabApiTests.Run(Check);
ApiKeyTests.Run(Check);
await StationDeviceAccessTests.Run(Check);
await EngineStartupTests.Run(Check);
await ParallelStopGateTests.Run(Check);
await StationNetworkPolicyTests.Run(Check);
await TimezoneTests.Run(Check);
await NtpTests.Run(Check);
await PrepSettingsTests.Run(Check);
StationDeviceTests.Run(Check);
ContainerTests.Run(Check);
BootMediaTests.Run(Check);
var clock = new Clock(); var auth = new Bootstrap(clock); var code=auth.DisplayCode;
Check(code.Length == 7 && code[3] == '-' && code.Replace("-", "").All(c => "0123456789ABCDEFGHJKMNPQRSTVWXYZ".Contains(c)),"Bootstrap code uses six Crockford Base32 characters in 3-3 format (30 bits)");
Check(auth.Login("xur","wrong").Status == 401,"Wrong bootstrap code rejected");
var valid=auth.Login("xur",code.ToLowerInvariant());
Check(valid.Status==200 && auth.CanSetup(valid.Session),"Valid code creates authenticated session");
Check(auth.Login("xur",code).Status==200 && auth.DisplayCode==code,"Setup code can be reused and remains displayed after login");
var key=System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
var firstBoot=new Bootstrap(clock,signingKey:key);var session=firstBoot.Login("xur",firstBoot.DisplayCode).Session!;
Check(session.Split('.').Length==3 && new Bootstrap(clock,signingKey:key).CanSetup(session),"Signed JWT session survives a new host instance with the persisted key");
Check(!new Bootstrap(clock).CanSetup(session),"Another machine's signing key cannot authorize the JWT");
clock.Now=clock.Now.AddHours(-6);
Check(firstBoot.CanSetup(session),"Anaconda resetting the clock backward does not invalidate an active session");
Check(new Bootstrap(clock,signingKey:key).CanSetup(session),"Session remains valid after a clock correction and installed-host restart");
clock.Now=clock.Now.AddHours(6);
clock.Now=clock.Now.AddHours(9); Check(!auth.CanSetup(valid.Session),"Session expiration enforced");
auth=new Bootstrap(clock); code=auth.DisplayCode; clock.Now=clock.Now.AddMinutes(31);
Check(auth.Login("xur",code).Status==401,"Expired code rejected");
auth=new Bootstrap(clock); for(var i=0;i<5;i++) auth.Login("xur","wrong");
Check(auth.Login("xur",auth.DisplayCode).Status==429,"Global login rate limit enforced even with correct code");
clock.Now=clock.Now.AddSeconds(31); Check(auth.Login("xur",auth.DisplayCode).Status==200,"Rate-limit window resets");
var pending=new Bootstrap(clock);
var initialSession=pending.Login("xur",pending.DisplayCode).Session;
Check(initialSession!=null && !pending.Configured,"Local access code works immediately before storage discovery completes");
clock.Now=clock.Now.AddMinutes(31);
pending.Initialize("A7K-2M9");
Check(pending.DisplayCode=="A7K-2M9" && pending.Login("xur","a7k2m9").Status==200,"Answer token accepts 3-3 or compact input and expires from activation");
Check(pending.CanSetup(initialSession),"Answer token handoff preserves existing browser sessions");
pending.Initialize("B7K2M9");
Check(pending.DisplayCode=="A7K-2M9","Answer token is applied only once per boot");
Check(!new Xur.Agent.Storage().CanPlan,"Immediate console login cannot bypass pending storage discovery");
Check(Xur.Agent.Storage.ReadBootstrapToken("schemaVersion: 1\nbootstrapToken: A7K2M9\n")=="A7K2M9","Typed bootstrap answer is parsed by YamlDotNet");
bool invalid=false;try { Xur.Agent.Storage.ReadBootstrapToken("bootstrapToken: A7K2M9\ndisk: /dev/vda\n"); } catch { invalid=true; }
Check(invalid,"Answer token cannot silently authorize additional provisioning fields");
var redacted=Redaction.Logs("One-time code: fixture-code\nhttps://login.tailscale.com/a/fixture\ntoken=fixture-token\n▄▀▄▀\nInstallation stage completed");
Check(!redacted.Contains("fixture-code") && !redacted.Contains("login.tailscale.com") && !redacted.Contains("fixture-token") && !redacted.Contains('▄'),"Support log redacts code, claim, token and QR payload");
Check(redacted.Contains("Installation stage completed"),"Redacted log retains operational evidence");
Check(LocalConsole.Command("\x1b[?6c")==null && LocalConsole.Command("\x1b[7;1R")==null && LocalConsole.Command("67")==null,"Terminal replies and multi-character input cannot trigger reboot or shutdown");
Check(LocalConsole.Command(" 6 ")=='6' && LocalConsole.Command("P")=='p',"Only complete menu option lines are accepted");
var keys=new ConsoleKeyReader();
Check("\x1b[?6c\x1b[7;1R".Select(keys.Read).All(k=>k==ConsoleKeyAction.None),"Raw terminal replies cannot become power actions");
Check("\x1b[B".Select(keys.Read).ToArray().Last()==ConsoleKeyAction.Down && "\x1b[A".Select(keys.Read).ToArray().Last()==ConsoleKeyAction.Up && keys.Read('\r')==ConsoleKeyAction.Enter,"Arrow keys and Enter navigate the console");
keys.Read('\x1b');
Check(keys.AwaitingEscape && keys.FlushEscape()==ConsoleKeyAction.Back && !keys.AwaitingEscape,"Standalone Escape returns to the menu without interpreting arrow sequences");
LocalConsole.OpenQr(false);
Check(LocalConsole.Navigate(ConsoleKeyAction.Enter)=='0' && LocalConsole.Navigate(ConsoleKeyAction.Back)=='0',"Enter and Escape leave the QR instead of reopening enrollment");
Check(Appliance.LoginUrl("  https://login.tailscale.com/a/test-only-fixture  ")!=null && Appliance.LoginUrl("https://login.tailscale.com.evil.invalid/a/test")==null && Appliance.LoginUrl("http://login.tailscale.com/a/test")==null,"Browser authorization links require the real Tailscale HTTPS host");
Check(Xur.Agent.SystemMonitor.Managed("xur-workload-example.service") && !Xur.Agent.SystemMonitor.Managed("xur-control.service") && !Xur.Agent.SystemMonitor.Managed("sshd.service"),"Workload actions cannot stop the control service or arbitrary system services");
var longOutput=string.Join('\n',Enumerable.Range(0,1000).Select(n=>$"Log line {n}"));
var frame=LocalConsole.Frame("Status",longOutput,80,25);
Check(frame.Contains('╭') && frame.Contains('╰'),"Spectre.Console renders the bounded menu panels");
Check(frame.Contains("6 Reboot") && frame.Contains("7 Shut down") && !frame.Contains('\n'),"Long output cannot scroll the fixed menu off screen");
Check(LocalConsole.Frame("Status",longOutput,80,25,1)!=frame,"Console content supports bounded pagination");
var highlighted=LocalConsole.Frame("Status","Access code: ABC-DEF",80,25,selectedOption:2);
Check(highlighted.Contains("\x1b[7m") && System.Text.RegularExpressions.Regex.IsMatch(highlighted,@"\x1b\[7m[^\x1b]*> 3 IP addresses[^\x1b]*\x1b\[0m"),"Entire selected row is highlighted");
var terminalRows=System.Text.RegularExpressions.Regex.Split(highlighted,@"\x1b\[\d+;1H").Skip(1).Select(LocalConsole.Clean).ToArray();
Check(terminalRows.Length==25 && terminalRows.All(row=>row.Length==80 && row.StartsWith("    ") && row.EndsWith("    ")) && terminalRows.Take(2).Concat(terminalRows.TakeLast(2)).All(row=>string.IsNullOrWhiteSpace(row)),"Console content leaves five-percent margins on all four edges");
Check(LocalConsole.Clean("safe\x1b[2J\x1b[Htext")=="safetext","Log control sequences cannot erase the console window");
Check(LocalConsole.Frame("QR",longOutput,80,25,qrView:true).Contains("Enlarge this terminal"),"Small terminals never silently crop a QR");
Check(System.Text.RegularExpressions.Regex.IsMatch(LocalConsole.Frame("QR","Scan this QR",80,25,qrView:true),@"\x1b\[7m[^\x1b]*> Back to menu[^\x1b]*\x1b\[0m"),"QR screen has a highlighted Back to menu option");
Check(Xur.Agent.OsUpdates.Allowed("stage") && !Xur.Agent.OsUpdates.Allowed("switch") && !Xur.Agent.OsUpdates.Allowed("stage;reboot"),"OS actions accept a fixed command set only");
var updateCalls=new List<string>();
var updateState=new OsUpdateStatus(new("1","sha256:old","upstream",false),null,new("0","sha256:previous","upstream",false),null,false,true,false,null,"");
var updater=new Xur.Agent.OsUpdates((exe,arguments,timeout)=> {
    updateCalls.Add(exe+" "+string.Join(' ',arguments));
    return Task.FromResult(new ProcessResult(0,exe.EndsWith("os-update") ? System.Text.Json.JsonSerializer.Serialize(updateState) : "inactive"));
});
await updater.Start("stage");
Check(updateCalls.Last().Contains("systemd-run --unit=xur-os-manual --collect --no-block") && updateCalls.Last().EndsWith("os-update stage"),"OS staging runs independently under systemd without blocking the manager");
updateState=updateState with {Pending=new("2","sha256:new","upstream",false)};
bool updateBlocked=false;try{await updater.Start("stage");}catch(InvalidOperationException){updateBlocked=true;}
Check(updateBlocked,"A queued OS deployment cannot be silently replaced");
updateState=updateState with {Pending=null,Previous=null};updateBlocked=false;
try{await updater.Start("rollback");}catch(InvalidOperationException){updateBlocked=true;}
Check(updateBlocked,"Rollback requires an observed previous deployment");
LocalConsole.OpenUpdates(updateState);
Check(LocalConsole.Navigate(ConsoleKeyAction.Enter)=='c',"Terminal updates page selects Check for updates initially");
LocalConsole.Navigate(ConsoleKeyAction.Down);
Check(LocalConsole.Navigate(ConsoleKeyAction.Enter)=='d',"Terminal updates page navigates to Update OS");
Check(LocalConsole.Navigate(ConsoleKeyAction.Back)=='u',"Escape from OS updates returns to Updates");
LocalConsole.OpenUpdateMenu();
Check(LocalConsole.Navigate(ConsoleKeyAction.Enter)=='h',"Updates submenu offers the Xur application first");
LocalConsole.Navigate(ConsoleKeyAction.Down);
Check(LocalConsole.Navigate(ConsoleKeyAction.Enter)=='o',"Updates submenu offers the operating system separately");
LocalConsole.Navigate(ConsoleKeyAction.Down);
Check(LocalConsole.Navigate(ConsoleKeyAction.Enter)=='0',"Updates submenu has a selectable Back to menu action");
LocalConsole.OpenApplicationUpdates(null);
Check(LocalConsole.Navigate(ConsoleKeyAction.Back)=='u',"Escape from application updates returns to Updates");
LocalConsole.OpenUpdateMenu();LocalConsole.OpenApplicationUpdates(null,refreshOnly:true);
Check(LocalConsole.Navigate(ConsoleKeyAction.Enter)=='h',"Background refresh cannot reopen a closed application update page");
var desktopRecipe=new Recipe("gaming-workstation","Gaming workstation","host:plasma",[],0,"","Display",1,0,"Desktop",Kind:"Workstation",Engine:"Plasma");
var displayGpu=new GpuDevice("0000:01:00.0","NVIDIA","Display GPU","nvidia","GPU-test",24576,["/dev/dri/renderD128"],["Compute runtime unavailable"],["/dev/dri/card0"],["card0-HDMI-A-1"]);
var desktopWorkload=new Workload("41","Desktop",desktopRecipe,[displayGpu.Pci],"workload-41");
ProfilePolicy.Validate(new("42","Profile 42",1,[desktopWorkload]),new("display",[displayGpu],[]));
Check(true,"A connected graphics GPU can run a workstation without requiring a compute backend");
bool stationConflict=false;try{ProfilePolicy.Validate(new("42","Profile 42",1,[desktopWorkload,desktopWorkload with{Id="43",Route="workload-43"}]),new("display",[displayGpu],[]));}catch(InvalidOperationException){stationConflict=true;}
Check(stationConflict,"Two stations cannot silently take the single local seat");
Check(desktopWorkload.Fingerprint==(desktopWorkload with{Name="Renamed",Recipe=desktopRecipe with{Name="New label",Description="Updated catalog text"}}).Fingerprint,"Workstation catalog labels do not restart its session");
var namedDesktop=desktopWorkload with {User=new("xuruserone",1001)};
Check(desktopWorkload.Fingerprint==Canonical.Hash(new{desktopRecipe.Kind,desktopRecipe.Image,Gpus=desktopWorkload.Gpus}),"Legacy workstation fingerprint is unchanged by user-picker upgrade");
Check(namedDesktop.Fingerprint!=(namedDesktop with{User=new("xurusertwo",1002)}).Fingerprint,"Changing workstation account changes the runtime fingerprint");
Check(namedDesktop.Fingerprint!=(namedDesktop with{User=new("xuruserone",1002)}).Fingerprint,"Reused account name with a different UID is a different runtime");
Check(Xur.Agent.StationAccounts.Username(desktopWorkload).StartsWith("xurws") && Xur.Agent.StationAccounts.Username(desktopWorkload with{User=new("temporary",0,true)}).StartsWith("xurtmp"),"Temporary homes never reuse or erase a legacy user's home");
bool privilegedUser=false;try{ProfilePolicy.Validate(new("42","Profile 42",1,[namedDesktop with{User=new("root",0)}]),new("display",[displayGpu],[]));}catch(InvalidOperationException){privilegedUser=true;}
Check(privilegedUser,"Root is not a workstation account selection");
var passwd=Path.GetTempFileName();
try{
 File.WriteAllText(passwd,"root:x:0:0:Root:/root:/bin/bash\nxuruserone:x:1001:1001:Alex:/var/home/xuruserone:/bin/bash\nxurtmpabc:x:1002:1002:Temporary:/var/home/xurtmpabc:/bin/bash\nservice:x:1003:1003:Service:/var/home/service:/usr/sbin/nologin\n");
 Check(Xur.Agent.StationAccounts.Read(passwd) is [{Username:"xuruserone",Uid:1001,Name:"Alex"}],"User inventory excludes root, services and disposable temporary users");
}finally{File.Delete(passwd);}
var filesystemJson="""
{"filesystems":[{"source":"/dev/vda3[/ostree/var]","target":"/var","fstype":"ext4","size":1000,"used":400,"avail":500},{"source":"/dev/vda3","target":"/sysroot","fstype":"ext4","size":1000,"used":400,"avail":500},{"source":"tmpfs","target":"/run","fstype":"tmpfs","size":800,"used":200,"avail":600},{"source":"/dev/vdb1","target":"/mnt/data","fstype":"xfs","size":2000,"used":800,"avail":1200}]}
""";
var storageFilesystems=Xur.Agent.StorageUsage.Filesystems(filesystemJson);
Check(storageFilesystems.Length==2 && storageFilesystems[0].Source=="/dev/vda3" && storageFilesystems[0].Mounts.Length==2 && storageFilesystems.Sum(f=>f.Bytes)==3000,"Storage deduplicates bind-mounted system filesystems and excludes RAM disks");
Check(storageFilesystems[0].Used==400 && storageFilesystems[0].Available==500,"Available space remains observed rather than pretending reserved blocks are free");
var modelFile=new ModelFile("model.gguf",new("https://huggingface.co/a/b/resolve/"+new string('a',40)+"/model.gguf",new string('a',64),100,"apache-2.0","a/b",new string('a',40)));
var importedRecipe=new Recipe("model-test","Model","registry.example/model@sha256:"+new string('a',64),[],8080,"/health","CPU",0,0,"",Files:[modelFile],SettingsSource:"revision-a");
var importedWorkload=new Workload("44","Model",importedRecipe,[],"workload-44");
Check(importedWorkload.Fingerprint==(importedWorkload with{Recipe=importedRecipe with{Name="Renamed",SettingsSource="revision-b"}}).Fingerprint,"Refreshing unchanged upstream model settings preserves the runtime fingerprint");
Check(importedWorkload.Fingerprint!=(importedWorkload with{Recipe=importedRecipe with{Files=[modelFile with{Asset=modelFile.Asset with{Sha256=new string('b',64)}}]}}).Fingerprint,"Changing actual model weights changes the runtime fingerprint");
await GpuPowerTests.Run(Check);
GpuTelemetryTests.Run(Check);
await GpuInventoryTests.Run(Check);
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { suite="Unit", passed=results }));
sealed class Clock : TimeProvider { public DateTimeOffset Now = new(2026,9,12,0,0,0,TimeSpan.Zero); public override DateTimeOffset GetUtcNow()=>Now; }
