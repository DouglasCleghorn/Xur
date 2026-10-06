using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using Xur.Agent;
using Xur.Control;
using Xur.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

static class DisplayPowerTests
{
    static async Task Denied(Func<Task> action,Action<bool,string> check,string name)
    {var denied=false;try{await action();}catch(InvalidOperationException){denied=true;}check(denied,name);}
    public static async Task Run(Action<bool,string> check)
    {
        Protocol(check);
        var root=Path.GetFullPath(".build/evidence/display-power-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var sys=root+"/sys";var drm=sys+"/class/drm";Directory.CreateDirectory(drm);
            void Connector(int card,string gpu,int connector)
            {
                var device=sys+"/devices/"+gpu;Directory.CreateDirectory(device);Directory.CreateDirectory(drm+"/card"+card);
                Directory.CreateSymbolicLink(drm+"/card"+card+"/device",device);
                var path=drm+"/card"+card+"-HDMI-A-1";Directory.CreateDirectory(path);
                File.WriteAllText(path+"/status","connected");File.WriteAllText(path+"/connector_id",connector.ToString());File.WriteAllBytes(path+"/edid",Edid());
            }
            Connector(0,"0000:01:00.0",41);Connector(1,"0000:02:00.0",42);Connector(2,"0000:03:00.0",43);
            var adapters=new Dictionary<string,CecInfo>{[root+"/dev/cec0"]=new("native","Native TV",262,0,41),[root+"/dev/cec1"]=new("usb","External TV",7,null,null)};
            foreach(var node in new[]{"cec0","cec1"}){Directory.CreateDirectory(sys+"/class/cec/"+node+"/device");}
            var sent=new List<(string Path,bool On,ushort? Address)>();var fail=false;var replace=false;var nativeOpens=0;
            ICecDevice Open(string path)
            {
                var info=adapters[path];if(replace&&path.EndsWith("cec0")&&++nativeOpens==2)info=info with{Card=null,Connector=null,Name="Replacement"};
                return new Device(info,(on,address)=>{if(fail)throw new InvalidOperationException("TV did not acknowledge");sent.Add((path,on,address));});
            }
            Dictionary<string,string> Owners()=>new(){["0000:02:00.0"]="desk"};
            var power=new DisplayPower(root+"/state",root+"/run",sys,root+"/dev",Open,Owners);
            var status=await power.Status();var console=status.Displays.Single(d=>d.Gpu=="0000:01:00.0");var station=status.Displays.Single(d=>d.Gpu=="0000:02:00.0");var unsupported=status.Displays.Single(d=>d.Gpu=="0000:03:00.0");
            check(status.Displays.Length==3&&console.CanControl&&!station.CanControl&&!unsupported.CanControl,"Displays enumerate native CEC and explain unsupported connectors without hiding them");
            check((await power.Status("desk")).Displays.Select(d=>d.Id).SequenceEqual([station.Id])&&(await power.Status(consoleOnly:true)).Displays.Length==2,"Workstation and console display discovery enforce ownership");
            await Denied(()=>power.Set(new(console.Id,"off","desk")),check,"A workstation cannot turn another seat's display off");
            await Denied(()=>power.Set(new(station.Id,"off",ConsoleOnly:true)),check,"Console power controls cannot affect a workstation display");
            await Denied(()=>power.Set(new(console.Id,"toggle")),check,"Power actions use a fixed on/off allowlist");
            await Denied(()=>power.Set(new("../../cec0","off")),check,"Client paths cannot select CEC devices");
            await Denied(()=>power.Set(new(unsupported.Id,"off")),check,"Unsupported display power fails clearly without sending a command");
            replace=true;await Denied(()=>power.Set(new(console.Id,"off")),check,"Hotplug replacement between observation and transmit cannot receive a stale power command");replace=false;
            check(sent.Count==0,"CEC adapter replacement is rejected before transmission");
            await power.Assign(new(station.Id,status.Adapters.Single(a=>a.Card==null).Id));
            check((await power.Status("desk")).Displays.Single().CanControl,"An explicitly assigned external adapter enables per-display controls");
            await Denied(()=>power.Assign(new(unsupported.Id,status.Adapters.Single(a=>a.Card==null).Id)),check,"An external CEC adapter cannot be assigned to two displays");
            await power.Set(new(console.Id,"off",ConsoleOnly:true));
            check(sent.Single() is {On:false,Address:0x1000}&&File.Exists(DisplayPower.SleepMarker(root+"/run",console.Connector)),"Standby targets the selected adapter and marks intentional display sleep");
            await power.Status();await power.Status();check(sent.Count==1,"Status polling cannot wake a sleeping TV");
            check((await power.Wake(new("desk"))).Woken==0&&sent.Count==1,"Controller input on another workstation cannot wake a console TV");
            File.WriteAllText(drm+"/"+console.Connector+"/status","disconnected");File.Delete(drm+"/"+console.Connector+"/edid");
            var restarted=new DisplayPower(root+"/state",root+"/run",sys,root+"/dev",Open,Owners);
            check((await restarted.Status()).Displays.Any(d=>d.Id==console.Id&&d.Power=="Standby requested"),"Sleeping TVs remain addressable after hotplug drops and an agent restart");
            Directory.Move(sys+"/class/cec/cec0",sys+"/class/cec/missing");
            var unavailable=await restarted.Wake(new(ConsoleOnly:true));
            check(unavailable.ConsumeInput&&unavailable.Woken==0&&File.Exists(DisplayPower.SleepMarker(root+"/run",console.Connector)),"A temporarily disconnected CEC adapter preserves standby and consumes blind input");
            Directory.Move(sys+"/class/cec/missing",sys+"/class/cec/cec0");
            check((await restarted.Wake(new(ConsoleOnly:true))).Woken==1&&sent.Last().On&&sent.Last().Address==null&&!File.Exists(DisplayPower.SleepMarker(root+"/run",console.Connector)),"Input wakes a deliberately sleeping TV even without an EDID and clears recovery protection");
            check((await restarted.Wake(new(ConsoleOnly:true))).Woken==0&&sent.Count==2,"Later input does not repeatedly send CEC wake commands");
            await power.Set(new(station.Id,"off","desk"));fail=true;
            var failedWake=await power.Wake(new("desk"));
            check(failedWake.ConsumeInput&&failedWake.Woken==0&&failedWake.Message.Contains("did not acknowledge")&&File.Exists(DisplayPower.SleepMarker(root+"/run",station.Connector)),"Failed controller wake consumes its input and retains standby for a retry");
            fail=false;await power.Set(new(station.Id,"on","desk"));
            fail=true;await Denied(()=>power.Set(new(station.Id,"off","desk")),check,"A TV transmission failure remains visible to the caller");
            check(!File.Exists(DisplayPower.SleepMarker(root+"/run",station.Connector)),"Failed standby is never recorded as successful sleep");
            check(DisplayPower.HdmiAddress(Edid())==0x1000&&DisplayPower.HdmiAddress(new byte[128])==null&&DisplayPower.HdmiAddress(Edid(true))==null,"HDMI address parsing rejects missing vendor blocks and corrupt extension checksums");
            await Console(check,console,unsupported);
            await Endpoints(check,console);
        }
        finally{Directory.Delete(root,true);}
    }
    static byte[] Edid(bool corrupt=false)
    {
        var bytes=new byte[256];new byte[]{0,255,255,255,255,255,255,0}.CopyTo(bytes,0);bytes[126]=1;bytes[128]=2;bytes[130]=10;
        bytes[132]=0x65;bytes[133]=3;bytes[134]=12;bytes[136]=0x10;
        bytes[255]=(byte)(256-bytes.AsSpan(128,127).ToArray().Sum(b=>(int)b)%256);if(corrupt)bytes[255]++;return bytes;
    }
    static void Protocol(Action<bool,string> check)
    {
        var sent=new List<byte[]>();var address=(ushort)0x1000;var configured=false;var nack=false;var calls=new List<uint>();
        using var device=new CecDevice((request,data)=>{
            calls.Add(request);var number=request&255;
            switch(number)
            {
                case 0:check(request==0xc04c6100,"CEC capability ioctl matches Linux UAPI");BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(68),6);break;
                case 1:BinaryPrimitives.WriteUInt16LittleEndian(data,address);break;
                case 3:if(configured){data[7]=1;data[0]=4;}break;
                case 4:check(request==0xc05c6104&&data[6]==5&&data[7]==1&&data[31]==4&&data[35]==3,"CEC claims a single playback address with the kernel ABI");configured=true;data[0]=4;break;
                case 5:check(request==0xc0386105&&BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16))==2,"CEC transmission uses the Linux message ABI");sent.Add(data[32..34]);data[50]=nack?(byte)4:(byte)1;break;
                default:throw new Exception("Unexpected CEC ioctl");
            }
            return 0;
        });
        device.Power(false,null);device.Power(true,null);
        check(sent[0].SequenceEqual(new byte[]{0x40,0x36})&&sent[1].SequenceEqual(new byte[]{0x40,0x04}),"CEC uses TV-directed Standby and Image View On without broadcasting or selecting an input");
        check(calls.Count(c=>(c&255)==4)==1,"Already configured CEC adapters retain their logical addresses");
        address=0xffff;device.Power(true,null);
        check(sent.Last().SequenceEqual(new byte[]{0xf0,0x04}),"A TV dropping hotplug can be woken from the unregistered CEC address");
        var rejected=false;try{device.Power(false,null);}catch(InvalidOperationException){rejected=true;}check(rejected,"CEC standby cannot use an invalid physical address");
        address=0x1000;nack=true;rejected=false;try{device.Power(true,null);}catch(InvalidOperationException){rejected=true;}check(rejected,"A CEC NACK is surfaced even when the ioctl succeeds");
    }
    static async Task Console(Action<bool,string> check,ConnectedDisplay supported,ConnectedDisplay unsupported)
    {
        using var handler=new ConsoleAgent(new([supported,unsupported],[]));using var client=new HttpClient(handler){BaseAddress=new Uri("http://fixture")};
        var console=new ConsoleDisplays(client,local:true);await console.Open();await console.Select('1');
        check(handler.Sent.Count==0&&console.Screen.Options.Any(o=>o.Label=="CEC screen off"),"Console display selection exposes power controls without sending a command");
        await console.Select('s');check(handler.Sent.Single()==new DisplayPowerRequest(supported.Id,"off",ConsoleOnly:true),"Console screen off preserves the console-only ownership scope");
        await console.Select('0');await console.Select('2');await console.Select('s');check(handler.Sent.Count==1&&console.Screen.Body.Contains("No CEC"),"Unsupported console display controls stay disabled with a reason");
        await console.Select('0');await console.Select('0');check(console.Closed,"Console Back returns from display controls without shutting down");
        check(ApiKeys.Allows("diagnostics","GET","/api/displays")&&!ApiKeys.Allows("diagnostics","POST","/api/displays/power")&&ApiKeys.Allows("automation","POST","/api/displays/power"),"Display power respects diagnostic and automation API scopes");
    }
    sealed class Device(CecInfo info,Action<bool,ushort?> send):ICecDevice
    {public CecInfo Info()=>info;public void Power(bool on,ushort? address)=>send(on,address);public void Dispose(){}}
    sealed class ConsoleAgent(DisplayPowerStatus status):HttpMessageHandler
    {
        public List<DisplayPowerRequest> Sent=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        {
            object data=status;
            if(request.Method==HttpMethod.Post){Sent.Add((await request.Content!.ReadFromJsonAsync<DisplayPowerRequest>(cancellation))!);data=new DisplayPowerResult("Command acknowledged.");}
            return new(HttpStatusCode.OK){Content=JsonContent.Create(data)};
        }
    }
    static async Task Endpoints(Action<bool,string> check,ConnectedDisplay display)
    {
        using var handler=new ConsoleAgent(new([display],[]));using var agent=new HttpClient(handler){BaseAddress=new Uri("http://agent")};
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app=builder.Build();app.MapDisplayPower(agent);await app.StartAsync();
        try
        {
            using var client=new HttpClient{BaseAddress=new Uri(app.Urls.Single())};
            using var local=await client.PostAsJsonAsync("/local/displays/power",new DisplayPowerRequest(display.Id,"off","spoofed",false));
            check(local.IsSuccessStatusCode&&handler.Sent.Last()==new DisplayPowerRequest(display.Id,"off",ConsoleOnly:true),"The local proxy forces console scope regardless of caller-supplied ownership");
            using var web=await client.PostAsJsonAsync("/api/displays/power",new DisplayPowerRequest(display.Id,"on","spoofed",true));
            check(web.IsSuccessStatusCode&&handler.Sent.Last()==new DisplayPowerRequest(display.Id,"on"),"Authenticated web power controls address the selected display with administrator scope");
            using var read=await client.GetAsync("/api/displays");check(read.IsSuccessStatusCode,"Web display inventory uses the agent response");
        }
        finally{await app.StopAsync();}
    }
}
