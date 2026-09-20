using System.Text.RegularExpressions;
using Xur.Domain;
namespace Xur.Agent;

public static class StationDeviceInventoryReader
{
    public static StationDeviceInventory Read(GpuDevice[] gpus,string sysRoot="/sys",string devRoot="/dev")
    {
        var errors=new List<string>();var usb=new List<UsbPeripheral>();var devices=new List<StationPeripheral>();
        string ReadFile(string path)
        {try{return File.Exists(path)?File.ReadAllText(path).Trim():"";}catch(Exception e) when(e is IOException or UnauthorizedAccessException){errors.Add("Unable to read "+path);return "";}}
        string[] Entries(string path)
        {try{return Directory.Exists(path)?Directory.GetDirectories(path).Order(StringComparer.Ordinal).ToArray():[];}catch(Exception e) when(e is IOException or UnauthorizedAccessException){errors.Add("Unable to enumerate "+path);return [];}}
        string Resolve(string path)=>SysfsPaths.Resolve(path);
        foreach(var path in Entries(sysRoot+"/bus/usb/devices"))
        {
            var vendor=ReadFile(path+"/idVendor");var product=ReadFile(path+"/idProduct");
            if(!Regex.IsMatch(vendor,@"^[0-9a-fA-F]{4}$")||!Regex.IsMatch(product,@"^[0-9a-fA-F]{4}$"))continue;
            var serial=ReadFile(path+"/serial");var real=Resolve(path);
            if(real.Length==0){errors.Add("A USB device changed during discovery. Refresh device inventory.");continue;}
            var port=StablePort(Path.GetRelativePath(sysRoot,real));
            var hub=ReadFile(path+"/bDeviceClass")=="09"||Entries(path).Any(p=>ReadFile(p+"/bInterfaceClass")=="09");
            var root=Regex.IsMatch(Path.GetFileName(path),@"^usb\d+$");
            var name=string.Join(' ',new[]{ReadFile(path+"/manufacturer"),ReadFile(path+"/product")}.Where(x=>x.Length>0));
            if(name.Length==0)name=(hub?"USB hub ":"USB device ")+vendor+":"+product;
            usb.Add(new(Identity(vendor,product,serial,port),name,serial.Length>0?"Serial":"Port",real,hub,root,[],[],Entries(path).Any(p=>ReadFile(p+"/bInterfaceClass")=="08")));
        }
        usb=usb.Select(d=>d with{Ancestors=usb.Where(h=>h.Hub&&IsChild(d.Path,h.Path)).Select(h=>h.Path).ToArray()}).ToList();
        foreach(var (subsystem,kind) in new[]{("input","Input"),("hidraw","Hidraw"),("sound","Audio")})
        foreach(var path in Entries(sysRoot+"/class/"+subsystem))
        {
            var name=Path.GetFileName(path);
            if(subsystem=="input"&&!Regex.IsMatch(name,@"^event\d+$"))continue;
            if(subsystem=="sound"&&!Regex.IsMatch(name,@"^(controlC\d+|pcmC\d+D\d+[pc]|hwC\d+D\d+|midiC\d+D\d+)$"))continue;
            var node=devRoot+(subsystem=="input"?"/input/":subsystem=="sound"?"/snd/":"/")+name;
            if(!File.Exists(node))continue;
            var real=Resolve(path);
            if(real.Length==0){errors.Add("A peripheral changed during discovery. Refresh device inventory.");continue;}
            var peripheral=usb.Where(d=>IsChild(real,d.Path)).OrderByDescending(d=>d.Path.Length).FirstOrDefault();
            string? gpu=null;
            if(peripheral==null&&kind=="Audio")
            {
                var pci=Regex.Matches(real,@"(?:^|/)([0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7])(?=/|$)").Cast<Match>().LastOrDefault()?.Groups[1].Value;
                // Discrete GPU HDMI/DP audio is a sibling PCI function. Do not
                // assign unrelated chipset/built-in audio by vendor alone.
                if(pci!=null)gpu=gpus.SingleOrDefault(g=>g.Pci[..^1]==pci[..^1])?.Pci;
            }
            string? station=null;
            if(peripheral==null && kind is "Input" or "Hidraw") {
                for(var parent=new DirectoryInfo(real);parent!=null && parent.FullName.StartsWith(sysRoot+"/devices/");parent=parent.Parent) {
                    var physical=ReadFile(parent.FullName+"/phys");
                    if(physical.StartsWith("xur/seat-xur-")){station=physical;break;}
                }
            }
            // The seat token is mapped to a workload only by the reconciler.
            devices.Add(new(node,kind,peripheral?.Id,gpu,station));
        }
        usb=usb.Select(d=>d with{Nodes=devices.Where(p=>p.UsbId==d.Id).Select(p=>p.Node).Distinct().Order().ToArray()}).ToList();
        return new(usb.OrderBy(d=>d.Name,StringComparer.Ordinal).ThenBy(d=>d.Path,StringComparer.Ordinal).ToArray(),devices.ToArray(),errors.Distinct().ToArray());
    }
    static bool IsChild(string path,string parent)=>path.StartsWith(parent.TrimEnd('/')+"/",StringComparison.Ordinal);
    public static string StablePort(string path)
    {
        path=Regex.Replace(path,@"/usb\d+(?=/|$)","/usb");
        return Regex.Replace(path,@"/\d+-(\d+(?:\.\d+)*)(?=/|$)","/port-$1");
    }
    public static string Identity(string vendor,string product,string serial,string port)=>"usb:"+Canonical.Hash(new{Vendor=vendor.ToLowerInvariant(),Product=product.ToLowerInvariant(),Kind=serial.Length>0?"Serial":"Port",Value=serial.Length>0?serial:StablePort(port)});
}
