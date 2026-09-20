using System.Text.Json;
using Xur.Agent;
using Xur.Control;
using Xur.Domain;

static class NetworkSettingsTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var config=new NetworkConfiguration("eno1","02:00:00:00:00:10",new("manual",["192.0.2.10/24"],"192.0.2.1",["192.0.2.53"]),new("auto"));
        check(NetworkValidation.Validate(config).Ipv4!.Addresses!.Single()=="192.0.2.10/24","Static IPv4 validates host CIDR and gateway subnet");
        var v6=config with {Ipv6=new("manual",["2001:db8:1::10/64"],"fe80::1",["2001:db8::53"])};
        check(NetworkValidation.Validate(v6).Ipv6!.Gateway=="fe80::1","Static IPv6 allows a scoped-to-device link-local gateway");
        foreach(var bad in new[]{
            config with {Ipv4=new("manual",["192.000.002.010/24"])},
            config with {Interface="eno1;reboot"},config with {MacAddress="ff:ff:ff:ff:ff:ff"},
            config with {Ipv4=new("manual",[])},config with {Ipv4=new("manual",["127.0.0.1/8"])},
            config with {Ipv4=new("manual",["192.0.2.0/24"])},config with {Ipv4=new("manual",["192.0.2.255/24"])},
            config with {Ipv4=new("manual",["192.0.2.10/33"])},config with {Ipv4=new("manual",["192.0.2.10/24"],"198.51.100.1")},
            config with {Ipv4=new("auto",["192.0.2.10/24"])},config with {Ipv4=new("disabled"),Ipv6=new("disabled")},
            config with {Ipv4=new("manual",["192.0.2.10/24"],Dns:["https://dns.invalid"])},config with {Ipv6=new("manual",["fe80::10%eno1/64"])}})
        {var rejected=false;try{NetworkValidation.Validate(bad);}catch(InvalidOperationException){rejected=true;}check(rejected,"Reject invalid network configuration "+JsonSerializer.Serialize(bad));}
        var answer=AnswerConfiguration.Parse("""
            schemaVersion: 1
            bootstrapToken: A7K-2M9
            network:
              interfaces:
                - macAddress: '02:00:00:00:00:10'
                  ipv4:
                    method: manual
                    addresses: [192.0.2.10/24]
                    gateway: 192.0.2.1
                    dns: [192.0.2.53]
                  ipv6:
                    method: auto
            """);
        check(answer.BootstrapToken=="A7K2M9" && answer.Network.Single().Ipv4!.Method=="manual","Answer YAML parses static networking together with the existing bootstrap code");
        check(AnswerConfiguration.Parse("network:\n  interfaces:\n    - interface: eno1\n").BootstrapToken==null,"Networking-only answers retain the random bootstrap login");
        foreach(var yaml in new[]{"schemaVersion: 2","schemaVersion: 1\ninstall: true","bootstrapToken: A7K2M9\nnetwork:\n  interfaces:\n    - interface: eno1\n      typo: true","schemaVersion: 1\n---\nschemaVersion: 1","network:\n  interfaces:\n    - interface: eno1\n    - interface: eno1"})
        {var rejected=false;try{AnswerConfiguration.Parse(yaml);}catch{rejected=true;}check(rejected,"Answer YAML rejects unknown, ambiguous or destructive instructions");}
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../.build/evidence/network-unit-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        try
        {
            var example=AnswerConfiguration.Parse(File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../docs/examples/xur.yml"))));
            check(example.Network.Single().Ipv4!.Method=="manual","The downloadable guide answer passes the real parser");
            File.WriteAllText(Path.Combine(root,"xur.yml"),"schemaVersion: 1");
            check(Storage.AnswerFiles(root).Single().Name=="xur.yml","Scanner recognizes the yml answer filename");
            File.WriteAllText(Path.Combine(root,"xur.yaml"),"schemaVersion: 1");
            check(Storage.AnswerFiles(root).Length==2,"Both answer filenames remain two candidates, never silently selected");
            File.Delete(Path.Combine(root,"xur.yaml"));File.CreateSymbolicLink(Path.Combine(root,"xur.yaml"),"missing");
            check(Storage.AnswerFiles(root).Any(f=>f.LinkTarget!=null),"Broken answer symlinks are discovered for rejection rather than treated as no answer");
            var nm=new Nm();var settings=new NetworkSettings(root,nm.Run);
            var devices=(await settings.Read()).Devices;
            check(devices.Single().MacAddress==config.MacAddress && devices.Single().Editable,"NetworkManager inventory exposes stable adapter identity and current settings");
            var changed=await settings.Apply(config,answer:true);
            check(changed.Stage=="Kept" && nm.AutoConnect && nm.Calls.Any(c=>c.Contains("CheckpointDestroy")),"Validated answer networking is activated and persisted without changing disk approval");
            check(nm.Calls.FindIndex(c=>c.Contains("CheckpointCreate"))<nm.Calls.FindIndex(c=>c.Contains("connection add")),"Rollback checkpoint exists before any candidate profile is created");
            if(File.Exists("/usr/bin/nmcli"))
            {
            var offline=await Processes.Run("nmcli",["--offline",..nm.AddArguments!],10);
            check(offline.ExitCode==0 && offline.Output.Contains("autoconnect=false") && offline.Output.Contains("mac-address=02:00:00:00:00:10") && !offline.Output.Contains("interface-name="),"Real nmcli validates a MAC-bound candidate that cannot autoconnect before confirmation");
            }
            nm.AutoConnect=false;
            var pending=await settings.Apply(v6);
            check(pending.Stage=="Applying" && !nm.AutoConnect,"Interactive change returns before activation and remains unconfirmed");
            for(var i=0;i<40;i++){await Task.Delay(100);if((await settings.Read()).Pending?.Stage=="Confirm")break;}
            await settings.Finish(pending.Id,false);
            check((await settings.Read()).Pending?.Stage=="Reverted" && nm.Calls.Any(c=>c.Contains("CheckpointRollback")),"Explicit revert restores the previous connection and removes the candidate");
            nm.FailActivation=true;var failed=false;
            try{await settings.Apply(config,answer:true);}catch(InvalidOperationException){failed=true;}
            check(failed && (await settings.Read()).Pending?.Stage=="Reverted","Activation failure rolls back and cannot be marked saved");nm.FailActivation=false;
            var before=nm.Calls.Count;failed=false;
            try{await settings.Apply(config with {MacAddress="02:00:00:00:00:11"},answer:true);}catch(InvalidOperationException){failed=true;}
            check(failed && nm.Calls.Skip(before).All(c=>!c.Contains("connection add")),"A replaced or mismatched adapter cannot receive an old request");
            var expired=pending with{Expires=DateTimeOffset.UtcNow.AddSeconds(-1),Stage="Confirm"};
            File.WriteAllText(Path.Combine(root,"network-change.json"),JsonSerializer.Serialize(expired,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var restarted=new NetworkSettings(root,nm.Run);
            check((await restarted.Read()).Pending?.Stage=="Reverted","Agent restart recognizes an expired NetworkManager checkpoint and removes its inactive candidate");
            failed=false;try{await restarted.Finish(expired.Id,true);}catch(InvalidOperationException){failed=true;}
            check(failed,"Expired network changes cannot be kept");
        }
        finally{Directory.Delete(root,true);}
        using var http=new HttpClient(new MenuHandler()){BaseAddress=new Uri("http://network.test")};
        var menu=new ConsoleMaintenance(http);await menu.Open("network");
        await menu.Select((char)256);await menu.Select('4');await menu.Select('s');
        check(menu.Screen.InputValue!=null,"Console static mode opens an address editor");
        LocalConsole.OpenMaintenance(menu.Screen);
        foreach(var c in "192.0.2.20/24")LocalConsole.EditText(c);
        check(LocalConsole.TextValue=="192.0.2.20/24","Console text editor accepts zeroes and IP punctuation as text");
        await menu.Submit(LocalConsole.TextValue);await menu.Select('0');
        check(menu.Screen.Body.Contains("192.0.2.20/24"),"Console preserves the draft before applying it");
        await menu.Select('a');check(menu.Screen.Body.Contains("Confirm"),"Console applies through the same typed network endpoint");
    }
    sealed class MenuHandler:HttpMessageHandler
    {
        NetworkChange? change;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.Method==HttpMethod.Post)change=new("id","eno1","candidate","previous","checkpoint",DateTimeOffset.UtcNow.AddMinutes(2),"Confirm","Keep these settings",["192.0.2.20/24"]);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=System.Net.Http.Json.JsonContent.Create(new NetworkSettingsStatus([new("eno1","02:00:00:00:00:10","connected",[],"uuid",new(),new(),true)],change))});
        }
    }
    sealed class Nm
    {
        public List<string> Calls=[];public string[]? AddArguments;public bool AutoConnect,FailActivation;
        string active="00000000-0000-4000-8000-000000000001",candidate="";
        public Task<ProcessResult> Run(string exe,string[] args,int timeout)
        {
            var text=string.Join(' ',args);Calls.Add(exe+" "+text);var output="";var code=0;
            if(text.Contains("CheckpointCreate"))output="o \"/org/freedesktop/NetworkManager/Checkpoint/1\"";
            else if(text.Contains("CheckpointRollback"))output="a{su} 1 \"/org/freedesktop/NetworkManager/Devices/1\" 0";
            else if(text.Contains("DEVICE,TYPE"))output="eno1:ethernet\ntailscale0:tun\n";
            else if(text.Contains("GENERAL.HWADDR"))output="02:00:00:00:00:10\n100 (connected)\n"+active+"\n";
            else if(text.Contains("GENERAL.DBUS-PATH"))output="/org/freedesktop/NetworkManager/Devices/1";
            else if(text.Contains("GENERAL.CON-UUID"))output=active;
            else if(text.Contains("IP4.ADDRESS"))output="192.0.2.1/24";
            else if(text.Contains("ipv4.method") && text.Contains("connection show"))output="auto\n\n\n\n";
            else if(text.Contains("ipv6.method") && text.Contains("connection show"))output="auto\n\n\n\n";
            else if(text.StartsWith("connection add")){AddArguments=args;candidate=args[Array.IndexOf(args,"connection.uuid")+1];}
            else if(text.Contains("connection up")){if(FailActivation){code=1;output="Activation failed";}else active=candidate;}
            else if(text.Contains("connection modify"))AutoConnect=true;
            else if(text.Contains("connection.id"))output="external profile";
            return Task.FromResult(new ProcessResult(code,output));
        }
    }
}
