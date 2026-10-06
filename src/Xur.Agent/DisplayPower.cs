using System.Text.Json;
using System.Text.RegularExpressions;
using Xur.Domain;

namespace Xur.Agent;

public sealed class DisplayPower(string stateDirectory,string runDirectory,string sysRoot="/sys",string devRoot="/dev",
    Func<string,ICecDevice>? open=null,Func<Dictionary<string,string>>? owners=null)
{
    readonly SemaphoreSlim gate=new(1,1);
    record SleepingDisplay(ConnectedDisplay Display,string Adapter);
    static SleepingDisplay[] ReadSleeping(string run)
    {var path=Path.Combine(run,"cec-sleep.json");return File.Exists(path)?JsonSerializer.Deserialize<SleepingDisplay[]>(File.ReadAllText(path))??[]:[];}
    readonly Dictionary<string,ConnectedDisplay> sleeping=ReadSleeping(runDirectory).ToDictionary(d=>d.Display.Id,d=>d.Display);
    readonly Dictionary<string,string> sleepingAdapters=ReadSleeping(runDirectory).ToDictionary(d=>d.Display.Id,d=>d.Adapter);
    readonly Dictionary<string,string> lastPower=[];
    string Settings=>Path.Combine(stateDirectory,"display-adapters.json");
    void SaveSleeping()
    {
        Directory.CreateDirectory(runDirectory);var path=Path.Combine(runDirectory,"cec-sleep.json");
        File.WriteAllText(path+".tmp",JsonSerializer.Serialize(sleeping.Select(p=>new SleepingDisplay(p.Value,sleepingAdapters[p.Key]))));File.Move(path+".tmp",path,true);
    }
    ICecDevice Open(string path)=>open?.Invoke(path)??new CecDevice(path);
    string AdapterId(string device,CecInfo info)
    {
        var path=Path.Combine(sysRoot,"class/cec",Path.GetFileName(device),"device");
        var identity=SysfsPaths.Resolve(path);
        return Canonical.Hash(new{identity,info.Driver,info.Name,info.Card,info.Connector});
    }
    static string Read(string path){try{return File.ReadAllText(path).Trim();}catch(IOException){return "";}catch(UnauthorizedAccessException){return "";}}
    static byte[] Edid(string path){try{return File.ReadAllBytes(path);}catch(IOException){return [];}catch(UnauthorizedAccessException){return [];}}
    Dictionary<string,string> Mappings()=>File.Exists(Settings)?JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(Settings))??[]:[];
    public static string SleepMarker(string run,string connector)=>Path.Combine(run,"cec-sleep-"+Canonical.Hash(connector)[..16]);
    DisplayPowerStatus Observe()
    {
        var adapters=new List<CecAdapterStatus>();var cecRoot=Path.Combine(sysRoot,"class/cec");
        if(Directory.Exists(cecRoot))foreach(var path in Directory.GetDirectories(cecRoot).Order())
        {
            var node=Path.GetFileName(path);if(!Regex.IsMatch(node,@"\Acec\d+\z"))continue;
            var device=Path.Combine(devRoot,node);var identity=SysfsPaths.Resolve(Path.Combine(path,"device"));
            var id=Canonical.Hash(identity.Length==0?SysfsPaths.Resolve(path):identity);
            try{using var adapter=Open(device);var info=adapter.Info();id=AdapterId(device,info);adapters.Add(new(id,info.Name,device,(info.Capabilities&4)!=0,info.Card,info.Connector));}
            catch(InvalidOperationException e){adapters.Add(new(id,node,device,false,null,null,e.Message));}
        }
        var mappings=Mappings();var assigned=owners?.Invoke()??StationSeats.DisplayOwners();var displays=new List<ConnectedDisplay>();
        var drm=Path.Combine(sysRoot,"class/drm");
        if(Directory.Exists(drm))foreach(var path in Directory.GetDirectories(drm).Order())
        {
            var connector=Path.GetFileName(path);var match=Regex.Match(connector,@"\Acard(\d+)-(.+)\z");if(!match.Success)continue;
            var gpu=Path.GetFileName(SysfsPaths.Resolve(Path.Combine(drm,"card"+match.Groups[1].Value,"device")));
            if(gpu.Length==0)continue;
            var id=Canonical.Hash(new{gpu,connector=match.Groups[2].Value});
            var connected=Read(Path.Combine(path,"status"))=="connected";
            if(!connected&&!sleeping.ContainsKey(id))continue;
            var connectorId=int.TryParse(Read(Path.Combine(path,"connector_id")),out var n)?n:-1;
            var native=adapters.Where(a=>a.Card==int.Parse(match.Groups[1].Value)&&a.Connector==connectorId).ToArray();
            CecAdapterStatus? adapter=native.Length==1?native[0]:null;
            if(adapter==null&&mappings.TryGetValue(id,out var mapped))adapter=adapters.SingleOrDefault(a=>a.Id==mapped&&a.Card==null);
            var name=DisplayName(Edid(Path.Combine(path,"edid")));if(name.Length==0)name=sleeping.GetValueOrDefault(id)?.Name??match.Groups[2].Value;
            var power=lastPower.GetValueOrDefault(id,sleeping.ContainsKey(id)?"Standby requested":"Unknown");
            var problem=adapter==null?"No CEC adapter assigned. HDMI alone does not guarantee CEC support.":adapter.Error??(!adapter.CanTransmit?"This CEC adapter cannot transmit.":null);
            displays.Add(new(id,connector,gpu,name,assigned.GetValueOrDefault(gpu),adapter?.Id,problem==null,problem,power));
        }
        // Never reuse a sleeping connector after the adapter is replaced.
        foreach(var display in displays.Where(d=>sleeping.ContainsKey(d.Id)&&d.Adapter!=null&&sleepingAdapters.GetValueOrDefault(d.Id)!=d.Adapter).ToArray())
        {sleeping.Remove(display.Id);sleepingAdapters.Remove(display.Id);lastPower.Remove(display.Id);File.Delete(SleepMarker(runDirectory,display.Connector));SaveSleeping();}
        return new(displays.ToArray(),adapters.ToArray());
    }
    static bool Allowed(ConnectedDisplay display,string? workstation,bool consoleOnly)=>
        workstation!=null?display.Workstation==workstation:!consoleOnly||display.Workstation==null;
    public async Task<DisplayPowerStatus> Status(string? workstation=null,bool consoleOnly=false)
    {
        await gate.WaitAsync();try{var status=Observe();return status with{Displays=status.Displays.Where(d=>Allowed(d,workstation,consoleOnly)).ToArray()};}finally{gate.Release();}
    }
    public async Task<DisplayPowerResult> Set(DisplayPowerRequest request)
    {
        if(request.Action is not ("on" or "off"))throw new InvalidOperationException("Choose screen on or screen off.");
        await gate.WaitAsync();try
        {
            var status=Observe();var display=status.Displays.SingleOrDefault(d=>d.Id==request.Id&&Allowed(d,request.Workstation,request.ConsoleOnly))
                ??throw new InvalidOperationException("This display is disconnected or belongs to another workstation. Refresh the display list.");
            Send(status,display,request.Action=="on");
            return new(request.Action=="on"?"CEC screen-on command acknowledged.":"CEC screen-off command acknowledged. Workloads keep running.");
        }finally{gate.Release();}
    }
    void Send(DisplayPowerStatus status,ConnectedDisplay display,bool on)
    {
        if(!display.CanControl)throw new InvalidOperationException(display.UnavailableReason??"CEC is unavailable.");
        var adapter=status.Adapters.Single(a=>a.Id==display.Adapter);
        using var device=Open(adapter.Device);
        // Recheck the kernel's connector binding on the descriptor used to send.
        var info=device.Info();
        if(AdapterId(adapter.Device,info)!=adapter.Id)throw new InvalidOperationException("CEC adapter changed. Refresh the display list.");
        var match=Regex.Match(display.Connector,@"\Acard(\d+)-");
        if(info.Card!=null&&(info.Card!=int.Parse(match.Groups[1].Value)||info.Connector!=int.Parse(Read(Path.Combine(sysRoot,"class/drm",display.Connector,"connector_id")))))
            throw new InvalidOperationException("CEC adapter assignment changed. Refresh the display list.");
        device.Power(on,HdmiAddress(Edid(Path.Combine(sysRoot,"class/drm",display.Connector,"edid"))));
        lastPower[display.Id]=on?"On requested":"Standby requested";
        if(on){sleeping.Remove(display.Id);sleepingAdapters.Remove(display.Id);File.Delete(SleepMarker(runDirectory,display.Connector));}
        else{sleeping[display.Id]=display;sleepingAdapters[display.Id]=adapter.Id;Directory.CreateDirectory(runDirectory);File.WriteAllText(SleepMarker(runDirectory,display.Connector),"");}
        SaveSleeping();
    }
    public async Task<DisplayPowerResult> Wake(DisplayWakeRequest request)
    {
        await gate.WaitAsync();try
        {
            // Background observation never sends power commands. Input wakes
            // only displays deliberately put in standby through Xur.
            if(sleeping.Count==0)return new("No CEC displays to wake.");
            var status=Observe();var count=0;var errors=new List<string>();
            var targets=status.Displays.Where(d=>sleeping.ContainsKey(d.Id)&&Allowed(d,request.Workstation,request.ConsoleOnly)).ToArray();
            foreach(var display in targets)
                try{Send(status,display,true);count++;}catch(InvalidOperationException e){errors.Add(display.Name+": "+e.Message);}
            return new(errors.Count>0?string.Join("\n",errors):"CEC wake command acknowledged.",count,targets.Length>0);
        }finally{gate.Release();}
    }
    public async Task Assign(DisplayAdapterRequest request)
    {
        await gate.WaitAsync();try
        {
            var status=Observe();var display=status.Displays.SingleOrDefault(d=>d.Id==request.Id)??throw new InvalidOperationException("Refresh the display list before assigning an adapter.");
            if(sleeping.ContainsKey(display.Id))throw new InvalidOperationException("Wake this display before changing its CEC adapter.");
            var mappings=Mappings();
            if(request.Adapter is {Length:>0})
            {
                var adapter=status.Adapters.SingleOrDefault(a=>a.Id==request.Adapter&&a.Card==null&&a.CanTransmit)
                    ??throw new InvalidOperationException("Choose an available external CEC adapter. Native adapters are assigned automatically.");
                if(status.Displays.Any(d=>d.Id!=display.Id&&d.Adapter==adapter.Id)||mappings.Any(p=>p.Key!=display.Id&&p.Value==adapter.Id))
                    throw new InvalidOperationException("This adapter is already assigned to another display.");
                mappings[display.Id]=adapter.Id;
            }
            else mappings.Remove(display.Id);
            Directory.CreateDirectory(stateDirectory);File.WriteAllText(Settings+".tmp",JsonSerializer.Serialize(mappings));File.Move(Settings+".tmp",Settings,true);
        }finally{gate.Release();}
    }
    public static ushort? HdmiAddress(byte[] edid)
    {
        if(edid.Length<128||!edid.AsSpan(0,8).SequenceEqual(new byte[]{0,255,255,255,255,255,255,0}))return null;
        for(var extension=1;extension<=edid[126]&&(extension+1)*128<=edid.Length;extension++)
        {
            var offset=extension*128;if(edid[offset]!=2)continue;var end=edid[offset+2];if(end is <4 or >127)continue;
            if(edid.AsSpan(offset,128).ToArray().Sum(b=>(int)b)%256!=0)continue;
            for(var i=4;i<end;)
            {
                var length=edid[offset+i]&31;var tag=edid[offset+i]>>5;if(i+1+length>end)break;
                if(tag==3&&length>=5&&edid[offset+i+1]==3&&edid[offset+i+2]==12&&edid[offset+i+3]==0)
                {var address=(ushort)((edid[offset+i+4]<<8)|edid[offset+i+5]);return address==0xffff?null:address;}
                i+=1+length;
            }
        }
        return null;
    }
    static string DisplayName(byte[] edid)
    {
        if(edid.Length<128)return "";
        for(var i=54;i+18<=126;i+=18)if(edid[i]==0&&edid[i+1]==0&&edid[i+3]==0xfc)
            return new string(System.Text.Encoding.ASCII.GetString(edid,i+5,13).Where(c=>c>=' '&&c<='~').ToArray()).Trim();
        return "";
    }
}
