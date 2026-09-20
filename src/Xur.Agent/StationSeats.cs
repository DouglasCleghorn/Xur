using System.Text.Json;
using System.Text.RegularExpressions;
using Xur.Domain;
namespace Xur.Agent;

// One intent document covers the complete profile, including stations whose
// startup failed. Their peripherals remain reserved instead of leaking to primary.
public static class StationSeats
{
    const string Intent="/var/lib/xur/station-seats.json",Root="/run/xur/seats",Rules="/run/udev/rules.d/72-xur-seats.rules";
    static readonly SemaphoreSlim gate=new(1,1);
    static readonly Dictionary<string,string> applied=new();
    record Session(Workload Workload,int Uid,string[] Graphics);
    public static string Seat(string id)=>"seat-xur-"+Canonical.Hash(id)[..12];
    public static string Physical(string id)=>"xur/"+Seat(id);
    public static bool Registered(string id)=>File.Exists(Root+"/"+id+".json");
    static Workload[] Intentions()=>File.Exists(Intent)?JsonSerializer.Deserialize<Workload[]>(File.ReadAllText(Intent))!:[];
    static Session[] Sessions()=>Directory.Exists(Root)?Directory.GetFiles(Root,"*.json").Select(p=>JsonSerializer.Deserialize<Session>(File.ReadAllText(p))!).ToArray():[];
    static async Task Run(string exe,string[] args)
    {var r=await Processes.Run(exe,args,30);if(r.ExitCode!=0)throw new InvalidOperationException("Workstation seat configuration failed: "+exe+". "+Redaction.Logs(r.Output));}
    static void Save<T>(string file,T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using(var stream=new FileStream(file+".tmp",new FileStreamOptions{Mode=FileMode.Create,Access=FileAccess.Write,UnixCreateMode=(UnixFileMode)384}))
        {JsonSerializer.Serialize(stream,value);stream.Flush(true);}
        File.Move(file+".tmp",file,true);
    }
    public static async Task SetIntent(Workload[] workloads)
    {
        StationDevicePolicy.Validate(workloads);
        await gate.WaitAsync();try{if(workloads.Length==0&&!File.Exists(Intent)&&Sessions().Length==0)return;Save(Intent,workloads);await ReconcileLocked();}finally{gate.Release();}
    }
    public static async Task Register(Workload w,GpuDevice gpu,int uid)
    {
        await gate.WaitAsync();try
        {
            var desired=Intentions();if(!desired.Any(s=>s.Id==w.Id)){desired=[..desired,w];StationDevicePolicy.Validate(desired);Save(Intent,desired);}
            var graphics=StationDeviceAccess.Nodes(gpu).Concat(gpu.Vendor=="NVIDIA"?await NvidiaDevice.WorkstationNodes(gpu):[]).Distinct().ToArray();
            Save(Root+"/"+w.Id+".json",new Session(w,uid,graphics));
            await ReconcileLocked();
        }finally{gate.Release();}
    }
    public static async Task Remove(string id)
    {
        await gate.WaitAsync();try
        {
            File.Delete(Root+"/"+id+".json");File.Delete(Root+"/"+id+".status");applied.Remove(id);
            await new StationDeviceAccess(root:"/var/lib/xur/station-peripheral-access").Revoke(id);
            await ReconcileLocked();
        }finally{gate.Release();}
    }
    public static StationDeviceAllocation[] Status()
    {
        var result=new List<StationDeviceAllocation>();
        if(Directory.Exists(Root))foreach(var path in Directory.GetFiles(Root,"*.status"))
            try{result.Add(JsonSerializer.Deserialize<StationDeviceAllocation>(File.ReadAllText(path))!);}
            catch(FileNotFoundException){/* A concurrent stop removed this station. */}
        return result.ToArray();
    }
    public static async Task Watch(CancellationToken token)
    {
        while(!token.IsCancellationRequested)
        {
            try{await gate.WaitAsync(token);try{await ReconcileLocked();}finally{gate.Release();}}
            catch(OperationCanceledException){break;}
            catch(Exception e){Console.Error.WriteLine("Workstation peripheral reconciliation: "+Redaction.Logs(e.Message));}
            try{await Task.Delay(2000,token);}catch(OperationCanceledException){break;}
        }
    }
    static string? DevicePath(string node)
    {
        var name=Path.GetFileName(node);var subsystem=node.StartsWith("/dev/dri/")?"drm":node.StartsWith("/dev/snd/")?"sound":node.StartsWith("/dev/input/")?"input":node.StartsWith("/dev/hidraw")?"hidraw":null;
        if(subsystem==null)return null;var real=SysfsPaths.Resolve("/sys/class/"+subsystem+"/"+name);
        return real.StartsWith("/sys/devices/")?real[4..]:null;
    }
    public static string RulesText(Workload[] desired,GpuDevice[] gpus,StationDeviceInventory inventory,StationDeviceAllocation[] allocations,Func<string,string?> resolve)
    {
        if(desired.Length==0)return "# No Xur workstation allocations\n";
        var lines=new List<string>{"# Generated from Xur workstation identities. Unclaimed devices wait for reconciliation.","SUBSYSTEM==\"input\", ENV{ID_SEAT}=\"seat-xur-unassigned\"","SUBSYSTEM==\"hidraw\", ENV{ID_SEAT}=\"seat-xur-unassigned\"","SUBSYSTEM==\"sound\", ENV{ID_SEAT}=\"seat-xur-unassigned\""};
        void Device(string node,string seat)
        {
            var path=resolve(node);if(path==null)return;
            if(!Regex.IsMatch(path,@"^/devices/[a-zA-Z0-9_./:+-]+$"))throw new InvalidOperationException("Invalid peripheral sysfs path.");
            lines.Add("DEVPATH==\""+path+"\", ENV{ID_SEAT}:=\""+seat+"\", TAG+=\"seat\", TAG+=\"uaccess\"");
        }
        foreach(var w in desired)
        {
            var seat=Seat(w.Id);
            lines.Add("SUBSYSTEM==\"input\", ATTRS{phys}==\""+Physical(w.Id)+"*\", ENV{ID_SEAT}:=\""+seat+"\", TAG+=\"seat\", TAG+=\"uaccess\"");
            lines.Add("SUBSYSTEM==\"hidraw\", ATTRS{phys}==\""+Physical(w.Id)+"*\", ENV{ID_SEAT}:=\""+seat+"\", TAG+=\"seat\", TAG+=\"uaccess\"");
            foreach(var gpu in gpus.Where(g=>w.Gpus.Contains(g.Pci)))foreach(var node in (gpu.Cards??[]).Concat(gpu.Nodes))Device(node,seat);
            foreach(var node in allocations.Single(a=>a.WorkloadId==w.Id).Nodes){Device(node,seat);var card=Regex.Match(node,@"^/dev/snd/(?:control|pcm|hw|midi)C(\d+)");if(card.Success)Device("/dev/snd/card"+card.Groups[1].Value,seat);}
        }
        return string.Join('\n',lines.Distinct())+"\n";
    }
    static async Task ReconcileLocked()
    {
        var sessions=Sessions();var desired=Intentions();
        // After the last session stops, return physical devices to normal host policy.
        if(sessions.Length==0)
        {
            if(File.Exists(Rules))
            {
                // udev retains properties in its database after a rule disappears.
                await File.WriteAllTextAsync(Rules,"ENV{ID_SEAT}==\"seat-xur-*\", ENV{ID_SEAT}=\"\"\n");
                await ReloadDevices();File.Delete(Rules);await Run("udevadm",["control","--reload"]);
            }
            return;
        }
        var gpus=await GpuInventory.Observe();var inventory=StationDeviceInventoryReader.Read(gpus);inventory=inventory with {Devices=inventory.Devices.Select(d=>d.Station==null?d:d with{Station=desired.SingleOrDefault(w=>(Physical(w.Id)==d.Station||d.Station.StartsWith(Physical(w.Id)+"/")))?.Id??"unassigned"}).ToArray()};var allocations=StationDevicePolicy.Plan(desired,inventory);
        foreach(var session in sessions)
        {
            var allocation=allocations.SingleOrDefault(a=>a.WorkloadId==session.Workload.Id);
            try
            {
            var nodes=allocation?.Nodes??[];
            var identities=nodes.Length==0?"":(await Processes.Run("stat",["--format=%n:%t:%T:%i",..nodes],5)).Output;
            var signature=Canonical.Hash(new{session.Graphics,nodes,identities});
            if(applied.GetValueOrDefault(session.Workload.Id)!=signature)
            {
                await ApplyBoundary(session,session.Graphics.Concat(nodes).Distinct().ToArray());
                await new StationDeviceAccess(root:"/var/lib/xur/station-peripheral-access").SetNodes(session.Workload.Id,session.Uid,nodes);
                applied[session.Workload.Id]=signature;
            }
            }
            catch(Exception error) when(error is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                applied.Remove(session.Workload.Id);
                var problem="Peripheral assignment failed: "+Redaction.Logs(error.Message);
                try
                {
                    await ApplyBoundary(session,session.Graphics);
                    await new StationDeviceAccess(root:"/var/lib/xur/station-peripheral-access").Revoke(session.Workload.Id);
                }
                catch(Exception cleanup) {problem+="; device access cleanup failed: "+Redaction.Logs(cleanup.Message);}
                allocation=(allocation??new StationDeviceAllocation(session.Workload.Id,false,[],[],[],[])) with{Nodes=[],Audio=[],Problems=[..allocation?.Problems??[],problem]};
                allocations=allocations.Select(a=>a.WorkloadId==session.Workload.Id?allocation:a).ToArray();
            }
            Save(Root+"/"+session.Workload.Id+".status",allocation??new StationDeviceAllocation(session.Workload.Id,false,[],[],[],["Workstation no longer belongs to the selected profile."]));
            await AudioDefault(session,allocation?.Audio??[],inventory);
        }
        var rules=RulesText(desired,gpus,inventory,allocations,DevicePath);
        if(!File.Exists(Rules)||File.ReadAllText(Rules)!=rules){Directory.CreateDirectory(Path.GetDirectoryName(Rules)!);await File.WriteAllTextAsync(Rules,rules);await ReloadDevices();}
    }
    static async Task ApplyBoundary(Session session,string[] nodes)
    {
        var unit="user-"+session.Uid+".slice";
        var folder="/run/systemd/system/"+unit+".d";Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(folder+"/50-xur.conf","[Slice]\nDevicePolicy=closed\nDeviceAllow=\n"+string.Join('\n',nodes.Select(d=>"DeviceAllow="+d+" rw"))+"\n");
        await Run("systemctl",["daemon-reload"]);
        if((await Processes.Run("systemctl",["is-active",unit],5)).ExitCode==0)
        {
            await Run("systemctl",["set-property","--runtime",unit,"DevicePolicy=closed","DeviceAllow="]);
            foreach(var node in nodes)await Run("systemctl",["set-property","--runtime",unit,"DeviceAllow="+node+" rw"]);
        }
    }
    static async Task ReloadDevices()
    {
        await Run("udevadm",["control","--reload"]);
        foreach(var subsystem in new[]{"drm","input","sound","hidraw"})await Run("udevadm",["trigger","--action=change","--subsystem-match="+subsystem]);
        await Run("udevadm",["settle","--timeout=10"]);
    }
    static async Task AudioDefault(Session session,string[] audio,StationDeviceInventory inventory)
    {
        var cards=audio.Select(p=>Regex.Match(p,@"^/dev/snd/(?:control|pcm|hw|midi)C(\d+)")).Where(m=>m.Success).Select(m=>m.Groups[1].Value).ToHashSet();
        if(cards.Count==0)return;
        string[] Args(params string[] args)=>["-u",StationAccounts.Username(session.Workload),"--","env","XDG_RUNTIME_DIR=/run/user/"+session.Uid,"pactl",..args];
        var sinks=await Processes.Run("runuser",Args("--format=json","list","sinks"),5);if(sinks.ExitCode!=0)return;
        try
        {
            using var doc=JsonDocument.Parse(sinks.Output);
            int Priority(JsonElement sink)
            {
                var card=sink.GetProperty("properties").GetProperty("alsa.card").ToString();
                var device=inventory.Devices.FirstOrDefault(d=>audio.Contains(d.Node)&&Regex.IsMatch(d.Node,@"^/dev/snd/(?:control|pcm|hw|midi)C"+card+@"(?:D|$)"));
                return device?.UsbId!=null?0:device?.Gpu!=null?1:2;
            }
            var allowed=doc.RootElement.EnumerateArray().Where(s=>!s.TryGetProperty("ports",out var ports)||ports.ValueKind!=JsonValueKind.Array||!ports.EnumerateArray().Any()||ports.EnumerateArray().Any(p=>!p.TryGetProperty("availability",out var a)||a.GetString() is not ("not available" or "no"))).Where(s=>s.TryGetProperty("properties",out var p)&&p.TryGetProperty("alsa.card",out var c)&&cards.Contains(c.ToString())).OrderBy(Priority).Select(s=>(Name:s.GetProperty("name").GetString()!,Rank:Priority(s))).ToArray();
            if(allowed.Length==0)return;
            var current=await Processes.Run("runuser",Args("get-default-sink"),5);
            if(!allowed.Any(s=>s.Name==current.Output.Trim()&&s.Rank==allowed[0].Rank))await Processes.Run("runuser",Args("set-default-sink",allowed[0].Name),5);
        }catch(JsonException){}
    }
}
