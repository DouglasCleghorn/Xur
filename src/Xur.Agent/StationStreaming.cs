using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xur.Domain;
namespace Xur.Agent;
public static class StationStreaming
{
    const string Root="/var/lib/xur-streaming";
    public static string Runtime=>"/var/lib/xur-streaming-runtime/"+ApplicationIdentity.Id;
    static readonly object installation=new();
    static void InstallRuntime()
    {lock(installation)InstallRuntimeLocked();}
    static void InstallRuntimeLocked()
    {
        if(Directory.Exists(Runtime))return;
        var source=Path.Combine(AppContext.BaseDirectory,"streaming");
        if(!File.Exists(source+"/usr/bin/sunshine"))throw new InvalidOperationException("Sunshine is missing from the application bundle.");
        Directory.CreateDirectory(Path.GetDirectoryName(Runtime)!);File.SetUnixFileMode(Path.GetDirectoryName(Runtime)!, (UnixFileMode)493);
        var stage=Runtime+".tmp";if(Directory.Exists(stage))Directory.Delete(stage,true);Directory.CreateDirectory(stage);File.SetUnixFileMode(stage,(UnixFileMode)493);
        foreach(var d in Directory.EnumerateDirectories(source,"*",SearchOption.AllDirectories)){var target=Path.Combine(stage,Path.GetRelativePath(source,d));Directory.CreateDirectory(target);File.SetUnixFileMode(target,(UnixFileMode)493);}
        foreach(var file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories)){var target=Path.Combine(stage,Path.GetRelativePath(source,file));File.Copy(file,target);File.SetUnixFileMode(target,(File.GetUnixFileMode(file)&UnixFileMode.UserExecute)!=0?(UnixFileMode)493:(UnixFileMode)420);}
        Directory.Move(stage,Runtime);
    }
    static readonly StationStreamPorts ports=new();
    static async Task Firewall(string id,bool open)
    {if((await Processes.Run("systemctl",["is-active","firewalld"],5)).ExitCode==0)foreach(var port in StationStreamPorts.FirewallPorts(ports.Get(id,false)))await Run("firewall-cmd",[(open?"--add-port=":"--remove-port=")+port]);}
    static string Unit(string id)=>"xur-stream-"+id+".service";
    static string Folder(string id){if(!ProfilePolicy.EntityIdentifier(id))throw new InvalidOperationException("Invalid workstation");return Root+"/"+id;}
    record Credentials(string Password);
    static async Task Run(string command,string[] args,int seconds=20)
    {var r=await Processes.Run(command,args,seconds);if(r.ExitCode!=0)throw new InvalidOperationException(command+" failed while preparing streaming: "+Redaction.Logs(r.Output)[..Math.Min(2000,Redaction.Logs(r.Output).Length)]);}
    public static string Configuration(Workload w,GpuDevice gpu,string path,int port=47989)=>
        $"sunshine_name = {string.Concat(w.Name.Where(c=>!char.IsControl(c)))}\nport = {port}\naddress_family = both\norigin_web_ui_allowed = pc\nupnp = disabled\nlan_encryption_mode = 2\nwan_encryption_mode = 2\nsystem_tray = disabled\nmin_log_level = info\ncapture = kwin\nencoder = {(gpu.Vendor=="NVIDIA"?"nvenc":gpu.Vendor is "AMD" or "Intel"?"vaapi":"software")}\nadapter_name = {gpu.Nodes.FirstOrDefault()??""}\n"+
        $"file_apps = {path}/apps.json\nfile_state = {path}/state/sunshine_state.json\ncredentials_file = {path}/state/credentials.json\npkey = {path}/key.pem\ncert = {path}/cert.pem\nlog_path = {path}/state/sunshine.log\n";
    public static string Applications(bool headless,string helper)
    {
        var desktop=new Dictionary<string,object>{{"name","Desktop"},{"image-path","desktop.png"}};
        if(headless)desktop["prep-cmd"]=new[]{new Dictionary<string,object>{{"do","/usr/bin/python3 "+helper+" --moonlight"},{"undo",""},{"elevated",false}}};
        return JsonSerializer.Serialize(new {env=new{},apps=new[]{desktop}});
    }
    public static string[] EncoderEnvironment(GpuDevice gpu)
    {
        if(gpu.Vendor!="NVIDIA")return [];
        if(!Regex.IsMatch(gpu.RuntimeId,@"^GPU-[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$"))
            throw new InvalidOperationException("The selected NVIDIA GPU has no valid UUID. Refresh hardware inventory before starting streaming.");
        // Sunshine's Linux CUDA initializer selects CUDA device 0 independently
        // of adapter_name. Remap that ordinal to the assigned physical GPU.
        return ["--setenv=CUDA_VISIBLE_DEVICES="+gpu.RuntimeId,"--setenv=CUDA_DEVICE_ORDER=PCI_BUS_ID"];
    }
    public static string[] RecoveryPolicy()=>["--property=Restart=on-failure","--property=RestartSec=5",
        "--property=RestartPreventExitStatus=SIGSEGV SIGABRT","--property=StartLimitIntervalSec=120","--property=StartLimitBurst=3"];
    public static async Task Start(Workload w,GpuDevice gpu)
    {
        var encoderEnvironment=EncoderEnvironment(gpu);
        var port=ports.Get(w.Id);
        var running=(await Processes.Run("systemctl",["is-active",Unit(w.Id)],5)).ExitCode==0;
        if(!running)await new StationUnits().Retire(Unit(w.Id));
        var user=StationAccounts.Username(w);var uid=(await Processes.Run("id",["-u",user],5)).Output.Trim();var gid=(await Processes.Run("id",["-g",user],5)).Output.Trim();
        if(!int.TryParse(uid,out var number)||number<1000||!int.TryParse(gid,out _))throw new InvalidOperationException("The workstation user is unavailable.");
        await new StationDeviceAccess().GrantAccess(w.Id,number,gpu);
        InstallRuntime();
        var label=await Processes.Run("semanage",["fcontext","-a","-t","bin_t","/var/lib/xur-streaming-runtime(/.*)?"],30);
        if(label.ExitCode!=0)await Run("semanage",["fcontext","-m","-t","bin_t","/var/lib/xur-streaming-runtime(/.*)?"],30);
        await Run("restorecon",["-RF",Runtime],30);
        Directory.CreateDirectory(Root);File.SetUnixFileMode(Root,(UnixFileMode)493);
        var path=Folder(w.Id);var owner=user+":"+uid;
        if(!running&&Directory.Exists(path)&&(w.User?.Temporary==true||File.Exists(path+"/owner")&&File.ReadAllText(path+"/owner")!=owner))Directory.Delete(path,true);
        Directory.CreateDirectory(path);await Run("chown",["root:"+gid,path]);File.SetUnixFileMode(path,(UnixFileMode)488);
        await File.WriteAllTextAsync(path+"/owner",owner);File.SetUnixFileMode(path+"/owner",(UnixFileMode)420);
        var state=path+"/state";if(!Directory.Exists(state)){Directory.CreateDirectory(state);await Run("chown",[user+":"+gid,state]);File.SetUnixFileMode(state,(UnixFileMode)448);}
        if(!File.Exists(path+"/cert.pem"))
        {
            using var rsa=RSA.Create(2048);var request=new CertificateRequest("CN=Xur Sunshine "+w.Id,rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            using var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddYears(10));
            await File.WriteAllTextAsync(path+"/key.pem",rsa.ExportPkcs8PrivateKeyPem());File.SetUnixFileMode(path+"/key.pem",(UnixFileMode)416);await Run("chown",["root:"+gid,path+"/key.pem"]);
            await File.WriteAllTextAsync(path+"/cert.pem",cert.ExportCertificatePem());
        }
        await Run("chown",["root:"+gid,path+"/key.pem"]);
        if(!File.Exists(path+"/manager.json"))
        {await File.WriteAllTextAsync(path+"/manager.json",JsonSerializer.Serialize(new Credentials(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))));File.SetUnixFileMode(path+"/manager.json",(UnixFileMode)384);}
        await File.WriteAllTextAsync(path+"/sunshine.conf",Configuration(w,gpu,path,port));
        if(gpu.Displays is not {Length:>0})await File.WriteAllTextAsync(path+"/headless","");else File.Delete(path+"/headless");
        StationDisplay.Install();
        await File.WriteAllTextAsync(path+"/apps.json",Applications(gpu.Displays is not {Length:>0},StationDisplay.ScriptPath));
        foreach(var file in new[]{"sunshine.conf","apps.json","cert.pem"})File.SetUnixFileMode(path+"/"+file,(UnixFileMode)420);
        await Run("modprobe",["uinput"]);
        await Run("modprobe",["uhid"]);
        await new StationDeviceAccess(root:"/var/lib/xur/stream-input-access").GrantNodes(w.Id,number,["/dev/uinput","/dev/uhid"]);
        await Processes.Run("systemctl",["reset-failed",Unit(w.Id)],5);
        var environment=await Processes.Run("runuser",["-u",user,"--","env","XDG_RUNTIME_DIR=/run/user/"+uid,"DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/"+uid+"/bus","systemctl","--user","show-environment"],10);
        var wayland=environment.Output.Split('\n').FirstOrDefault(l=>l.StartsWith("WAYLAND_DISPLAY="))?[16..]??"wayland-0";
        if(!Regex.IsMatch(wayland,@"^[a-zA-Z0-9_-]+$"))throw new InvalidOperationException("Invalid workstation display socket.");
        File.Delete(path+"/startup-error");
        // Check actual opens inside the same user/device boundary as Sunshine.
        // An ACL alone does not prove that cgroups or SELinux permit the device.
        var probeUnit="xur-stream-access-"+Guid.NewGuid().ToString("N");
        await Run("systemd-run",["--quiet","--wait","--collect","--unit="+probeUnit,"--property=User="+user,"--property=DevicePolicy=closed","--property=NoNewPrivileges=true",..await StreamDeviceArguments(gpu),"--property=RuntimeMaxSec=10","/usr/bin/python3","-c","import os,sys; [os.close(os.open(p, os.O_RDWR | os.O_CLOEXEC)) for p in sys.argv[1:]]",..StationDeviceAccess.Nodes(gpu),"/dev/uinput"]);
        if(!running)await Run("systemd-run",["--unit="+Unit(w.Id),"--collect","--property=Type=exec","--property=User="+user,"--property=DevicePolicy=closed","--property=NoNewPrivileges=true",..await StreamDeviceArguments(gpu),"--property=KillMode=control-group","--property=UMask=0077","--property=WorkingDirectory="+Runtime,..RecoveryPolicy(),"--property=PartOf=xur-station-"+w.Id+".service","--setenv=HOME=/var/home/"+user,"--setenv=XDG_RUNTIME_DIR=/run/user/"+uid,"--setenv=DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/"+uid+"/bus","--setenv=WAYLAND_DISPLAY="+wayland,"--setenv=QT_QPA_PLATFORM=offscreen","--setenv="+TimezoneSettings.StationEnvironment,..encoderEnvironment,"--setenv=LD_PRELOAD="+Runtime+"/usr/lib/libxur-seat-input.so","--setenv=XUR_INPUT_PHYS="+StationSeats.Physical(w.Id),"--setenv=XDG_SEAT="+StationSeats.Seat(w.Id),Runtime+"/usr/bin/sunshine",path+"/sunshine.conf"]);
        async Task FailStart(string message)
        {
            await File.WriteAllTextAsync(path+"/startup-error",message);
            File.SetUnixFileMode(path+"/startup-error",(UnixFileMode)384);
            await Processes.Run("systemctl",["stop",Unit(w.Id)],20);
            await Firewall(w.Id,false);
            throw new InvalidOperationException(message);
        }
        using var http=Client(w.Id,false);
        var credentials=JsonSerializer.Deserialize<Credentials>(File.ReadAllText(path+"/manager.json"))!;
        for(var attempt=0;attempt<30;attempt++)
        {
            var observed=await CaptureHealth(w.Id);
            if(observed.Error!=null)await FailStart(observed.Error);
            try
            {
                using var authenticated=Client(w.Id);var check=await authenticated.GetAsync("api/config");
                if(check.IsSuccessStatusCode){var health=await CaptureHealth(w.Id);if(health.Error!=null)await FailStart(health.Error);if(health.Ready){await Firewall(w.Id,true);return;}}
                // First launch has no Sunshine admin. The administration listener is restricted to localhost.
                var response=await http.PostAsJsonAsync("api/password",new {newUsername="xur",newPassword=credentials.Password,confirmNewPassword=credentials.Password});
                if(response.IsSuccessStatusCode){using var verified=Client(w.Id);if((await verified.GetAsync("api/config")).IsSuccessStatusCode){var health=await CaptureHealth(w.Id);if(health.Error!=null)await FailStart(health.Error);if(health.Ready){await Firewall(w.Id,true);return;}}}
            }catch(HttpRequestException){}catch(TaskCanceledException){}
            await Task.Delay(1000);
        }
        await FailStart("Sunshine did not become ready. Open workstation logs for the capture or encoder error.");
    }
    static async Task<string[]> StreamDeviceArguments(GpuDevice gpu)=>StationDeviceAccess.Nodes(gpu)
        .Concat(gpu.Vendor=="NVIDIA"?await NvidiaDevice.WorkstationNodes(gpu):[]).Concat(new[]{"/dev/uinput","/dev/uhid"}).Distinct()
        .Select(n=>"--property=DeviceAllow="+n+" rw").ToArray();
    static async Task<(bool Ready,string? Error)> CaptureHealth(string id)
    {
        var unit=await Processes.Run("systemctl",["show",Unit(id),"--property=InvocationID,Result"],5);
        var fields=unit.Output.Split('\n').Where(l=>l.Contains('=')).Select(l=>l.Split('=',2)).ToDictionary(p=>p[0],p=>p[1]);
        var invocation=fields.GetValueOrDefault("InvocationID","");
        var expected=File.ReadLines(Folder(id)+"/sunshine.conf").FirstOrDefault(l=>l.StartsWith("encoder = "))?[10..];
        if(!Regex.IsMatch(invocation,@"^[0-9a-f]{32}$"))return(false,"Sunshine is not running. Open workstation logs for details.");
        var log=await Processes.Run("journalctl",["_SYSTEMD_INVOCATION_ID="+invocation,"--no-pager","--output=cat","--grep=Found H.264 encoder:|Unable to initialize capture method|Fatal: Unable to find display or encoder|Couldn't find any working encoder matching|OpenEncodeSessionEx failed|Couldn't open:.*Permission denied|Couldn't open DRM FD for CUDA device","--lines=30"],5);
        return EncodingHealth(log,expected,fields.GetValueOrDefault("Result",""));
    }
    public static (bool Ready,string? Error) EncodingHealth(ProcessResult journal,string? expected,string result="")
    {
        // journalctl --grep exits 1 before the new invocation has matching logs.
        var noMatches=journal.ExitCode==1&&(string.IsNullOrWhiteSpace(journal.Output)||journal.Output.Trim()=="-- No entries --");
        if(journal.ExitCode!=0&&!noMatches)return(false,"Could not read Sunshine encoder status.");
        return EncodingHealth(noMatches?"":journal.Output,expected,result);
    }
    public static (bool Ready,string? Error) EncodingHealth(string log,string? expected,string result="")
    {
        if(result is "core-dump" or "signal")return(false,"Sunshine crashed while capturing or encoding. Automatic crash retries are disabled. Open workstation logs for details.");
        if(result=="start-limit-hit")return(false,"Sunshine failed repeatedly and was stopped. Open workstation logs before retrying.");
        if(log.Contains("Unable to initialize capture method"))
            return(false,"Sunshine could not capture the workstation display. Retry streaming to prepare the virtual monitor, or open workstation logs for capture permission errors.");
        if(log.Contains("Permission denied")&&log.Contains("Couldn't open:"))
            return(false,"Sunshine cannot open a DRM card required by the selected CUDA GPU. Check the workstation device mapping and permissions in Diagnostics.");
        var selectedFailed=log.Contains("Couldn't find any working encoder matching");
        var fatal=log.Contains("Fatal: Unable to find display or encoder");
        if((selectedFailed||fatal)&&log.Contains("OpenEncodeSessionEx failed"))return(false,"NVIDIA could not open an NVENC session on the assigned GPU. Open workstation logs and download Diagnostics for the driver and device mapping.");
        if(selectedFailed)return(false,"Sunshine could not initialize the selected encoder. Streaming has not become ready; open workstation logs for details.");
        if(fatal)return(false,"Sunshine could not capture this desktop or initialize its encoder. Open workstation logs for details.");
        if(result.Length>0&&result!="success")return(false,"Sunshine exited with result "+result+". Open workstation logs for details.");
        var found=Regex.Match(log,@"Found H\.264 encoder:[^\r\n]*\[([a-z0-9_]+)\]");
        if(found.Success&&found.Groups[1].Value!=expected)return(false,"Sunshine selected a different encoder than the workstation requested. Open workstation logs for details.");
        return(found.Success,null);
    }
    static HttpClient Client(string id,bool authenticate=true)
    {
        var path=Folder(id);using var expected=X509CertificateLoader.LoadCertificateFromFile(path+"/cert.pem");var thumbprint=expected.GetCertHashString(HashAlgorithmName.SHA256);
        var handler=new HttpClientHandler{AllowAutoRedirect=false,ServerCertificateCustomValidationCallback=(_,cert,_,_)=>cert?.GetCertHashString(HashAlgorithmName.SHA256)==thumbprint};
        var client=new HttpClient(handler){BaseAddress=new Uri("https://127.0.0.1:"+(ports.Get(id,false)+1)+"/"),Timeout=TimeSpan.FromSeconds(8)};
        if(authenticate){var secret=JsonSerializer.Deserialize<Credentials>(File.ReadAllText(path+"/manager.json"))!;client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("xur:"+secret.Password)));}
        return client;
    }
    public static async Task<JsonElement> Pending(string id)
    {using var client=Client(id);return await client.GetFromJsonAsync<JsonElement>("api/pin");}
    public static async Task Pair(string id,StationPairRequest request)
    {
        if(!Regex.IsMatch(request.Pin??"",@"^[0-9]{4}$")||!Regex.IsMatch(request.PairingId??"",@"^[0-9a-fA-F]{32}$")||string.IsNullOrWhiteSpace(request.Name)||request.Name.Length>80)throw new InvalidOperationException("Select a pending client, enter its four-digit PIN and a name.");
        using var client=Client(id);client.Timeout=TimeSpan.FromSeconds(60);var r=await client.PostAsJsonAsync("api/pin",new {pairing_id=request.PairingId,pin=request.Pin,name=request.Name});
        if(!r.IsSuccessStatusCode||!(await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetBoolean())throw new InvalidOperationException("Pairing did not complete. Start pairing in Moonlight and try its new PIN.");
    }
    public static async Task Stop(string id)
    {
        await new StationUnits().Retire(Unit(id),stopActive:true);
        await Firewall(id,false);
        File.Delete(Folder(id)+"/startup-error");
        await new StationDeviceAccess(root:"/var/lib/xur/stream-input-access").Revoke(id);
    }
    public static async Task<StationStreamStatus> Status(Workload w)
    {
        var active=(await Processes.Run("systemctl",["is-active",Unit(w.Id)],5)).Output.Trim();
        var headless=File.Exists(Folder(w.Id)+"/headless");
        var failure=Folder(w.Id)+"/startup-error";
        string? error=File.Exists(failure)?await File.ReadAllTextAsync(failure):null;var ready=false;
        if(active=="failed")error=(await CaptureHealth(w.Id)).Error??"Sunshine stopped after an error. Open workstation logs for details.";
        if(active=="active")try{using var c=Client(w.Id);ready=(await c.GetAsync("api/config")).IsSuccessStatusCode;var health=await CaptureHealth(w.Id);ready &= health.Ready;error=health.Error;}catch{error="Sunshine is starting or unavailable.";}
        return new(w.Id,ready?"Ready":error!=null?"Failed":active=="active"?"Starting":active=="failed"?"Failed":"Stopped",headless,error,ports.Get(w.Id,false));
    }
}
