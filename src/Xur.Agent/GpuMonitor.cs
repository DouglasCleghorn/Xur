using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Xur.Domain;
namespace Xur.Agent;

public sealed class GpuMonitor(string directory="/var/lib/xur")
{
    readonly object gate=new();
    readonly Dictionary<string,List<GpuReading>> history=new();
    GpuTelemetrySnapshot snapshot=new(null,[]);
    public GpuTelemetrySnapshot Status(int minutes=15)
    {
        lock(gate)return snapshot with {Gpus=snapshot.Gpus.Select(g=>g with {History=g.History.Where(p=>p.At>=DateTimeOffset.UtcNow.AddMinutes(-Math.Clamp(minutes,1,1440))).ToArray()}).ToArray()};
    }
    // Fresh bounded samples for an explicit benchmark; normal monitoring remains slow.
    public async Task<GpuTelemetrySnapshot> Sample()
    {
        var previous=Status(1);var devices=previous.Gpus.Select(g=>g.Device).ToArray();
        if(devices.Length==0)devices=await GpuInventory.Observe();
        var now=DateTimeOffset.UtcNow;string? error=null;
        var nvidia=new Dictionary<string,(GpuReading Reading,string Driver,GpuProcess[] Processes)>();
        if(devices.Any(g=>g.Vendor=="NVIDIA"))
        {
            try{var result=await Processes.Run("nvidia-smi",["--query","--xml-format"],8);if(result.ExitCode==0)nvidia=ParseNvidia(result.Output,now);else error="NVIDIA telemetry unavailable.";}
            catch{error="NVIDIA telemetry unavailable.";}
        }
        return new(now,devices.Select(g=>nvidia.TryGetValue(g.Pci,out var nv)?new GpuTelemetry(g,nv.Driver,nv.Reading,[],nv.Processes):new GpuTelemetry(g,null,ReadSysfs(g,now),[],[])).ToArray(),error);
    }
    public async Task Run(CancellationToken stop)
    {
        try
        {
            var path=Path.Combine(directory,"gpu-history.json");
            if(File.Exists(path))foreach(var pair in JsonSerializer.Deserialize<Dictionary<string,GpuReading[]>>(File.ReadAllText(path)) ?? [])history[pair.Key]=pair.Value.Where(p=>p.At>DateTimeOffset.UtcNow.AddDays(-1)).TakeLast(5760).ToList();
        }catch { /* A partial/unreadable history must not prevent current telemetry. */ }
        int samples=0;
        while(!stop.IsCancellationRequested)
        {
            try
            {
                var devices=await GpuInventory.Observe();var now=DateTimeOffset.UtcNow;
                var nvidia=new Dictionary<string,(GpuReading Reading,string Driver,GpuProcess[] Processes)>();
                if(devices.Any(g=>g.Vendor=="NVIDIA"))try{
                    var r=await Processes.Run("nvidia-smi",["--query","--xml-format"],10);
                    if(r.ExitCode==0)nvidia=ParseNvidia(r.Output,now);
                }catch { }
                var result=new List<GpuTelemetry>();
                foreach(var device in devices)
                {
                    var reading=ReadSysfs(device,now);string? version=null;var processes=Owners(device);
                    if(nvidia.TryGetValue(device.Pci,out var nv))
                    {reading=nv.Reading;version=nv.Driver;processes=nv.Processes.Concat(processes).GroupBy(p=>p.Pid).Select(g=>g.First()).ToArray();}
                    if(!history.TryGetValue(device.Pci,out var points))history[device.Pci]=points=[];
                    points.RemoveAll(p=>p.At<now.AddDays(-1)||p.At>now);points.Add(reading);if(points.Count>5760)points.RemoveRange(0,points.Count-5760);
                    result.Add(new(device,version,reading,points.ToArray(),processes));
                }
                var awaitedTopology=await NvLinkTopology.Observe(devices);
                lock(gate)snapshot=new(now,result.ToArray(),Topology:awaitedTopology);
                if(++samples%4==0)
                {
                    Directory.CreateDirectory(directory);var path=Path.Combine(directory,"gpu-history.json");
                    using(var f=new FileStream(path+".tmp",FileMode.Create,FileAccess.Write)){JsonSerializer.Serialize(f,history);f.Flush(true);}File.Move(path+".tmp",path,true);
                }
            }
            catch {lock(gate)snapshot=snapshot with {Error="GPU readings are temporarily unavailable."};}
            try{await Task.Delay(TimeSpan.FromSeconds(15),stop);}catch(OperationCanceledException){break;}
        }
    }
    public static double? Number(string? text)
    {
        if(text==null)return null;var match=Regex.Match(text,@"^\s*(-?[0-9]+(?:\.[0-9]+)?)(?:\s|$)");
        return match.Success&&double.TryParse(match.Groups[1].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var number)&&double.IsFinite(number)?number:null;
    }
    public static string Pci(string text)
    {var parts=text.ToLowerInvariant().Split(':');if(parts.Length!=3)return text;return parts[0].TrimStart('0').PadLeft(4,'0')+":"+parts[1]+":"+parts[2];}
    public static Dictionary<string,(GpuReading Reading,string Driver,GpuProcess[] Processes)> ParseNvidia(string xml,DateTimeOffset at)
    {
        using var reader=XmlReader.Create(new StringReader(xml),new XmlReaderSettings{DtdProcessing=DtdProcessing.Ignore,XmlResolver=null});
        var document=XDocument.Load(reader);var result=new Dictionary<string,(GpuReading,string,GpuProcess[])>();
        foreach(var gpu in document.Root!.Elements("gpu"))
        {
            string? Get(params string[] names){XElement? e=gpu;foreach(var name in names)e=e?.Element(name);return e?.Value;}
            var pci=Pci(Get("pci","pci_bus_id")??"");if(pci.Length==0)continue;
            var power=gpu.Element("gpu_power_readings")??gpu.Element("power_readings");
            var reading=new GpuReading(at,Number(Get("utilization","gpu_util")),Number(Get("fb_memory_usage","used")),Number(Get("fb_memory_usage","total")),Number(Get("fb_memory_usage","free")),
                Number(power?.Element("power_draw")?.Value)??Number(power?.Element("instant_power_draw")?.Value)??Number(power?.Element("average_power_draw")?.Value),
                Number(power?.Element("power_limit")?.Value)??Number(power?.Element("current_power_limit")?.Value),Number(Get("temperature","gpu_temp")),Number(Get("fan_speed")),null,Number(Get("clocks","graphics_clock")),Number(Get("clocks","mem_clock")),Get("performance_state"));
            var processes=(gpu.Element("processes")?.Elements("process_info")??[]).Where(e=>int.TryParse(e.Element("pid")?.Value,out var pid)&&pid>0).Select(e=>{
                int pid=int.Parse(e.Element("pid")!.Value);return new GpuProcess(pid,ProcessName(pid),Number(e.Element("used_memory")?.Value));
            }).ToArray();
            result[pci]=(reading,document.Root.Element("driver_version")?.Value??"",processes);
        }
        return result;
    }
    static string Read(string path){try{return File.ReadAllText(path).Trim();}catch{return "";}}
    static string ProcessName(int pid){var comm=Read("/proc/"+pid+"/comm");return comm.Length==0?"Process "+pid:comm;}
    public static GpuReading ReadSysfs(GpuDevice gpu,DateTimeOffset at,string sysRoot="/sys")
    {
        var device=Regex.IsMatch(gpu.Pci,@"^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$")?sysRoot+"/bus/pci/devices/"+gpu.Pci:gpu.Cards?.FirstOrDefault() is {} card?sysRoot+"/class/drm/"+Path.GetFileName(card)+"/device":"";
        if(device.Length==0)return new(at);
        string? sensor=null;try{sensor=Directory.GetDirectories(device+"/hwmon").Order().FirstOrDefault();}catch{}
        double? Value(string file)=>Number(Read(device+"/"+file));double? Sensor(string file)=>sensor==null?null:Number(Read(sensor+"/"+file));
        var total=Value("mem_info_vram_total")/1048576;var used=Value("mem_info_vram_used")/1048576;
        return new(at,Value("gpu_busy_percent"),used,total,total!=null&&used!=null?Math.Max(0,total.Value-used.Value):null,
            (Sensor("power1_average")??Sensor("power1_input"))/1000000,Sensor("power1_cap")/1000000,Sensor("temp1_input")/1000,null,Sensor("fan1_input"));
    }
    static GpuProcess[] Owners(GpuDevice gpu)
    {
        var nodes=gpu.Nodes.Concat(gpu.Cards??[]).ToHashSet();var owners=new List<GpuProcess>();
        if(nodes.Count==0)return [];
        foreach(var process in Directory.EnumerateDirectories("/proc"))
        {
            if(!int.TryParse(Path.GetFileName(process),out var pid))continue;
            try{if(Directory.EnumerateFiles(process+"/fd").Any(fd=>nodes.Contains(new FileInfo(fd).LinkTarget??"")))owners.Add(new(pid,ProcessName(pid),null));}catch(IOException){}catch(UnauthorizedAccessException){}
        }
        return owners.ToArray();
    }
}
