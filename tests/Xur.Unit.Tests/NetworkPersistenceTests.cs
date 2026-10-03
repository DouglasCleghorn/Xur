using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xur.Agent;
using Xur.Control;
using Xur.Domain;

// Stateful, fake-only profile store: no invocation of nmcli, busctl or host networking.
public static class NetworkPersistenceTests
{
    const string WifiMac="02:00:00:00:00:10",WiredMac="02:00:00:00:00:11",Bssid="02:00:00:00:00:20";
    const string Password="  fixture\\password ;=only ";
    public static async Task Run(Action<bool,string> check)
    {
        var repo=Directory.GetCurrentDirectory();
        var root=Path.Combine(repo,".build/evidence/network-persistence-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var originalError=Console.Error;using var diagnostics=new StringWriter();
        try
        {
            Console.SetError(diagnostics);
            var fixture=new Profiles(Path.Combine(root,"live"));
            var network=new NetworkSettings(Path.Combine(root,"runtime"),fixture.Run,fixture.Directory);
            // A cleanup timeout happens after activation and durable profile save.
            fixture.RetirementFailure="read";bool failed=false;
            try { await network.ConnectWifi(new("wlan0",WifiMac,"Fixture AP",Bssid,"wpa-psk",Password)); }
            catch(InvalidOperationException){failed=true;}
            var kept=(await network.Read()).Pending!;
            check(!failed && kept.Stage=="Kept" && fixture.Exists(kept.Candidate),
                "Old-profile inspection timeout cannot delete a successfully saved Wi-Fi credential profile");
            fixture.RetirementFailure="delete";
            var another=await network.ConnectWifi(new("wlan0",WifiMac,"Fixture AP",Bssid,"wpa-psk",Password));
            check(another.Stage=="Kept" && fixture.Exists(another.Candidate) && fixture.Exists(kept.Candidate),
                "Old-profile deletion timeout retains both saved Wi-Fi profiles without failing the new connection");
            fixture.RetirementFailure="process-race";
            var processRace=await network.ConnectWifi(new("wlan0",WifiMac,"Fixture AP",Bssid,"wpa-psk",Password));
            check(processRace.Stage=="Kept" && fixture.Exists(processRace.Candidate) && fixture.Exists(another.Candidate),
                "A process kill-race exception during optional retirement cannot invalidate the newly kept Wi-Fi keyfile");
            another=processRace;fixture.RetirementFailure="";
            Console.SetError(originalError);
            check(diagnostics.ToString().Contains("Network settings saved.") && !diagnostics.ToString().Contains(Password),
                "Superseded-profile cleanup diagnostics explain retention without exposing a credential-bearing process error");
            // Exercise actual console -> typed agent -> keyfile, rather than an invented handoff file.
            using var client=new HttpClient(new ConsoleHandler(network)){BaseAddress=new Uri("http://fixture")};
            var console=new ConsoleWifi(client);await console.Open();await console.Select((char)256);await console.Submit(Password);
            var wifi=(await network.Read()).Pending!;var wifiPath=fixture.PathFor(wifi.Candidate);
            var wifiFields=ReadProfile(wifiPath);
            check(console.Connected && wifi.Stage=="Kept" && wifiFields["connection.autoconnect"]=="true"
                && wifiFields["wifi.mac-address"]==WifiMac && !wifiFields.ContainsKey("connection.interface-name"),
                "Console success is backed by an autoconnect keyfile bound to permanent MAC, not installer interface name");
            check(Unescape(wifiFields["wifi-security.psk"])==Password && wifiFields["wifi-security.psk-flags"]=="0",
                "Console-written credential keyfile preserves spaces, backslash and punctuation as a saved system secret");
            check(!fixture.Exists(another.Candidate),"A subsequent successful Wi-Fi change retires only the previous Xur profile");
            // Real YAML parser -> real apply/keep -> persistent fake-NM export.
            var answer=AnswerConfiguration.Parse("""
                schemaVersion: 1
                network:
                  interfaces:
                    - macAddress: '02:00:00:00:00:11'
                      ipv4:
                        method: manual
                        addresses: [192.0.2.10/24, 192.0.2.11/24]
                        gateway: 192.0.2.1
                        dns: [192.0.2.53, 192.0.2.54]
                      ipv6:
                        method: disabled
                """);
            var wired=await network.Apply(answer.Network.Single(),answer:true);
            fixture.RetirementFailure="read";
            var replacement=await network.Apply(answer.Network.Single(),answer:true);
            check(replacement.Stage=="Kept" && fixture.Exists(replacement.Candidate),
                "A previous-profile timeout cannot turn a successfully saved static answer into an installation-locking failure");
            fixture.RetirementFailure="";wired=replacement;
            var savedWifi=wifi;var savedCredential=File.ReadAllBytes(wifiPath);
            fixture.CancelActivation=true;bool cancelled=false;
            try { await network.ConnectWifi(new("wlan0",WifiMac,"Fixture AP",Bssid,"wpa-psk",Password)); }
            catch(InvalidOperationException){cancelled=true;}
            fixture.CancelActivation=false;
            check(cancelled && (await network.Read()).Pending?.Stage=="Reverted" && fixture.Exists(savedWifi.Candidate) && File.ReadAllBytes(wifiPath).SequenceEqual(savedCredential),
                "Cancellation during new Wi-Fi activation still fails and rolls back without changing the saved credential profile");
            fixture.FailSave=true;bool saveFailed=false;
            try { await network.ConnectWifi(new("wlan0",WifiMac,"Fixture AP",Bssid,"wpa-psk",Password)); }
            catch(InvalidOperationException){saveFailed=true;}
            fixture.FailSave=false;
            check(saveFailed && (await network.Read()).Pending?.Stage=="Failed" && fixture.Exists(savedWifi.Candidate) && File.ReadAllBytes(wifiPath).SequenceEqual(savedCredential),
                "Failure to persist the new autoconnect setting remains an error and retains the prior saved Wi-Fi profile");
            // Unconfirmed candidates also travel through the handoff, but cannot win boot selection.
            fixture.AddUnconfirmed();
            var target=Path.Combine(root,"installed");Directory.CreateDirectory(target);
            var installer=File.ReadAllText(Path.Combine(repo,"os/installer/install-manager"));
            var handoff=installer.Split("# Copy saved NetworkManager profiles,",2)[1].Split("# Carry the explicitly chosen computer name",2)[0];
            handoff=handoff[handoff.IndexOf("mkdir -p",StringComparison.Ordinal)..]
                .Replace("chroot \"$target\" restorecon -RF /etc/NetworkManager","true")
                .Replace("/etc/NetworkManager/system-connections/.","\"$source\"/.")
                .Replace("if test -d /etc/NetworkManager/system-connections;","if test -d \"$source\";");
            var copy=await Processes.Run("bash",["-euc","target=\"$1\"\nsource=\"$2\"\n"+handoff,"fixture",target,fixture.Directory],10);
            check(copy.ExitCode==0,"Actual installer profile handoff succeeds entirely on disposable paths");
            var installed=Path.Combine(target,"etc/NetworkManager/system-connections");
            foreach(var original in System.IO.Directory.GetFiles(fixture.Directory))
            {
                var saved=Path.Combine(installed,Path.GetFileName(original));
                check(File.ReadAllBytes(saved).SequenceEqual(File.ReadAllBytes(original))
                    && File.GetUnixFileMode(saved)==(UnixFileMode.UserRead|UnixFileMode.UserWrite),
                    "Installer transfers actual saved profile bytes and owner-only permissions: "+Path.GetFileName(original));
            }
            check(File.GetUnixFileMode(installed)==(UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute),
                "Transferred credential directory is private to root");
            // Cold-load exported data with renamed NICs and no previous in-memory active state.
            var reloaded=System.IO.Directory.GetFiles(installed).Select(ReadProfile).ToArray();
            var bootWifi=reloaded.Where(p=>p.GetValueOrDefault("connection.type")=="wifi"
                && p.GetValueOrDefault("wifi.mac-address")==WifiMac && p.GetValueOrDefault("connection.autoconnect")=="true").ToArray();
            check(bootWifi.Any(p=>p["connection.uuid"]==wifi.Candidate && Unescape(p["wifi-security.psk"])==Password),
                "Cold-load fixture retains saved Wi-Fi credentials and autoconnect eligibility without installer interface names");
            var savedWired=reloaded.Single(p=>p["connection.uuid"]==wired.Candidate);
            check(savedWired["connection.autoconnect"]=="true" && savedWired["ethernet.mac-address"]==WiredMac && !savedWired.ContainsKey("connection.interface-name")
                && savedWired["ipv4.method"]=="manual" && savedWired["ipv4.addresses"]=="192.0.2.10/24,192.0.2.11/24"
                && savedWired["ipv4.gateway"]=="192.0.2.1" && savedWired["ipv4.dns"]=="192.0.2.53,192.0.2.54"
                && savedWired["ipv4.ignore-auto-dns"]=="yes" && savedWired["ipv6.method"]=="disabled",
                "Static YAML handoff preserves complete IP/DNS/family policy and permanent NIC binding");
            check(reloaded.Single(p=>p["connection.id"]=="fixture-unconfirmed")["connection.autoconnect"]=="false",
                "Copied unconfirmed candidate remains ineligible for reboot autoconnection");
        }
        finally{Console.SetError(originalError);System.IO.Directory.Delete(root,true);}
    }
    static Dictionary<string,string> ReadProfile(string path)
    {
        var result=new Dictionary<string,string>();string section="";
        foreach(var line in File.ReadAllLines(path))
        {
            if(line.StartsWith('['))section=line.Trim('[',']');
            else if(line.Contains('=')){var field=line.Split('=',2);result[section+"."+field[0]]=field[1];}
        }
        return result;
    }
    static string Unescape(string value)
    {
        var result=new System.Text.StringBuilder();
        for(var i=0;i<value.Length;i++)
        {
            if(value[i]=='\\' && i+1<value.Length){i++;result.Append(value[i]=='s'?' ':value[i]);}
            else result.Append(value[i]);
        }
        return result.ToString();
    }
    sealed class ConsoleHandler(NetworkSettings network):HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        {
            object response=request.RequestUri!.AbsolutePath switch
            {
                "/network/wifi"=>await network.ReadWifi(),
                "/network/wifi/scan"=>await network.ScanWifi((await request.Content!.ReadFromJsonAsync<WifiScanRequest>(cancellation))!),
                "/network/wifi/connect"=>await network.ConnectWifi((await request.Content!.ReadFromJsonAsync<WifiConnectRequest>(cancellation))!),
                _=>throw new InvalidOperationException("Unexpected console request")
            };
            return new(HttpStatusCode.OK){Content=JsonContent.Create(response)};
        }
    }
    sealed class Profiles
    {
        public string Directory {get;}
        public string RetirementFailure="";public bool CancelActivation,FailSave;
        readonly Dictionary<string,string> paths=[];Dictionary<string,string> checkpoint=[];
        readonly Dictionary<string,string> active=new(){["wlan0"]="00000000-0000-4000-8000-000000000001",["eno1"]="00000000-0000-4000-8000-000000000002"};
        public Profiles(string directory){Directory=directory;System.IO.Directory.CreateDirectory(directory);}
        public bool Exists(string uuid)=>paths.TryGetValue(uuid,out var path)&&File.Exists(path);
        public string PathFor(string uuid)=>paths[uuid];
        public void AddUnconfirmed()
        {
            var path=Path.Combine(Directory,"unconfirmed.nmconnection");
            File.WriteAllText(path,"[connection]\nid=fixture-unconfirmed\nuuid=00000000-0000-4000-8000-000000000099\ntype=ethernet\nautoconnect=false\nautoconnect-priority=999\n[ethernet]\nmac-address="+WiredMac+"\n");
        }
        public Task<ProcessResult> Run(string exe,string[] args,int timeout)
        {
            var text=string.Join(' ',args);string output="";
            string Value(string key)=>args[Array.IndexOf(args,key)+1];
            if(exe=="restorecon")return Task.FromResult(new ProcessResult(0,""));
            if(text.Contains("WIFI,WIFI-HW"))output="enabled:enabled";
            else if(text.Contains("DEVICE,TYPE"))output="wlan0:wifi\neno1:ethernet";
            else if(text.Contains("GENERAL.STATE") && !text.Contains("GENERAL.HWADDR"))output="100 (connected)\n"+active["wlan0"]+"\n/org/freedesktop/NetworkManager/Devices/1";
            else if(text.Contains("GENERAL.HWADDR"))output=WiredMac+"\n100 (connected)\n"+active["eno1"];
            else if(text.Contains("PermHwAddress"))output="s \""+WifiMac+"\"";
            else if(text.Contains("GENERAL.PRODUCT"))output="Fixture\nfake\n1\nno\n0";
            else if(text.Contains("GENERAL.DBUS-PATH"))output="/org/freedesktop/NetworkManager/Devices/2";
            else if(text.Contains("IP4.ADDRESS"))output="192.0.2.10/24";
            else if(text.Contains("BSSID,SSID"))output="02\\:00\\:00\\:00\\:00\\:20:Fixture AP:WPA2:80";
            else if(text.Contains("CheckpointCreate")){checkpoint=active.ToDictionary();output="o \"/org/freedesktop/NetworkManager/Checkpoint/1\"";}
            else if(text.Contains("CheckpointRollback")){foreach(var item in checkpoint)active[item.Key]=item.Value;output="a{su} 1 \"/org/freedesktop/NetworkManager/Devices/1\" 0";}
            else if(text.Contains("GENERAL.CON-UUID"))output=active[args[^1]];
            else if(text.Contains("connection add"))
            {
                var wifi=args.Contains("--offline");var uuid=Value("connection.uuid");
                var profile="[connection]\nid="+Value("con-name")+"\nuuid="+uuid+"\ntype="+(wifi?"wifi":"ethernet")+"\nautoconnect=false\nautoconnect-priority=999\n";
                if(Value("ifname")!="*")profile+="interface-name="+Value("ifname")+"\n";
                if(wifi)output=profile+"[wifi]\nssid="+Value("wifi.ssid")+"\nmac-address="+Value("wifi.mac-address")+"\n[wifi-security]\nkey-mgmt="+Value("wifi-sec.key-mgmt")+"\n";
                else
                {
                    profile+="[ethernet]\nmac-address="+Value("802-3-ethernet.mac-address")+"\n";
                    foreach(var family in new[]{"ipv4","ipv6"})
                    {profile+="["+family+"]\n";foreach(var field in new[]{"method","addresses","gateway","dns","ignore-auto-dns"})profile+=field+"="+Value(family+"."+field)+"\n";}
                    var filename=Path.Combine(Directory,Value("con-name")+".nmconnection");File.WriteAllText(filename,profile);paths[uuid]=filename;
                }
            }
            else if(text.StartsWith("connection load")){var filename=args[^1];paths[ReadProfile(filename)["connection.uuid"]]=filename;}
            else if(text.Contains("connection up"))
            {
                if(CancelActivation)throw new OperationCanceledException("Injected activation cancellation");
                var uuid=Value("uuid");var device=args.Contains("ifname")?Value("ifname"):Exists(uuid)&&ReadProfile(paths[uuid])["connection.type"]=="ethernet"?"eno1":"wlan0";
                active[device]=uuid;
            }
            else if(text.Contains("connection modify"))
            {
                if(FailSave)return Task.FromResult(new ProcessResult(1,"Injected autoconnect persistence failure"));
                var enabled=Value("connection.autoconnect") switch{"yes"=>"true","no"=>"false",_=>throw new InvalidOperationException("Unexpected autoconnect setting")};
                var filename=paths[Value("uuid")];
                File.WriteAllText(filename,File.ReadAllText(filename).Replace("autoconnect=false","autoconnect="+enabled).Replace("autoconnect=true","autoconnect="+enabled));
            }
            else if(text.Contains("connection.id"))
            {
                if(RetirementFailure=="read")throw new OperationCanceledException("Injected profile-inspection timeout: "+Password);
                if(RetirementFailure=="process-race")throw new InvalidOperationException("Injected process kill race: "+Password);
                output=Exists(args[^1])?ReadProfile(paths[args[^1]])["connection.id"]:"external-profile";
            }
            else if(text.Contains("connection delete"))
            {
                if(RetirementFailure=="delete")throw new IOException("Injected profile-deletion process failure: "+Password);
                if(paths.TryGetValue(Value("uuid"),out var filename))File.Delete(filename);
            }
            else if(text.Contains("ipv4.method") && text.Contains("connection show"))output="auto\n\n\n";
            else if(text.Contains("ipv6.method") && text.Contains("connection show"))output="auto\n\n\n";
            else if(text.Contains("connection.controller"))output="";
            else if(text.Contains("CheckpointDestroy")||text.Contains("LastScan"))output="";
            else throw new InvalidOperationException("Unexpected fake command: "+exe+" "+text);
            return Task.FromResult(new ProcessResult(0,output));
        }
    }
}
