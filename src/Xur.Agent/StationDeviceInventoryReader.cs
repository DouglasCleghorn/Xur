using System.Text.RegularExpressions;
using System.Globalization;
using Xur.Domain;
namespace Xur.Agent;

public static class StationDeviceInventoryReader
{
    public static StationDeviceInventory Read(GpuDevice[] gpus,string sysRoot="/sys",string devRoot="/dev")
    {
        var errors=new List<string>();var usb=new List<UsbPeripheral>();var devices=new List<StationPeripheral>();var paths=new Dictionary<string,string>();
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
            usb.Add(new(Identity(vendor,product,serial,port),name,serial.Length>0?"Serial":"Port",real,hub,root,[],[],Entries(path).Any(p=>ReadFile(p+"/bInterfaceClass")=="08"),serial.Length>0?serial:null));
        }
        usb=usb.Select(d=>d with{Ancestors=usb.Where(h=>h.Hub&&IsChild(d.Path,h.Path)).Select(h=>h.Path).ToArray()}).ToList();
        foreach(var (subsystem,kind) in new[]{("input","Input"),("hidraw","Hidraw"),("sound","Audio")})
        foreach(var path in Entries(sysRoot+"/class/"+subsystem))
        {
            var name=Path.GetFileName(path);
            if(subsystem=="input"&&!Regex.IsMatch(name,@"^(event|js)\d+$"))continue;
            if(subsystem=="sound"&&!Regex.IsMatch(name,@"^(controlC\d+|pcmC\d+D\d+[pc]|hwC\d+D\d+|midiC\d+D\d+)$"))continue;
            var node=devRoot+(subsystem=="input"?"/input/":subsystem=="sound"?"/snd/":"/")+name;
            if(!File.Exists(node))continue;
            var real=Resolve(path);
            if(real.Length==0){errors.Add("A peripheral changed during discovery. Refresh device inventory.");continue;}
            paths[node]=real;
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
        var controllers=new List<ControllerPeripheral>();
        foreach(var device in devices.Where(d=>d.Kind=="Input"&&d.Station==null&&Regex.IsMatch(Path.GetFileName(d.Node),@"^event\d+$")))
        {
            var input=Path.GetDirectoryName(paths[device.Node])!;
            if(!HasBit(ReadFile(input+"/capabilities/key"),304)||!HasBit(ReadFile(input+"/capabilities/abs"),0))continue;
            // Ignore user-created virtual pads: their identity cannot establish
            // ownership of a physical controller. Sunshine has its own seat tag.
            if(paths[device.Node].StartsWith(sysRoot+"/devices/virtual/",StringComparison.Ordinal))continue;
            var vendor=ReadFile(input+"/id/vendor");var product=ReadFile(input+"/id/product");var bus=ReadFile(input+"/id/bustype");var serial=ReadFile(input+"/uniq");
            if(vendor=="28de"&&serial=="XXXXXXXXXX")serial=""; // hid-steam's failed serial-query placeholder.
            var scope=input;
            for(var parent=new DirectoryInfo(input).Parent;parent!=null&&parent.FullName.StartsWith(sysRoot+"/devices/");parent=parent.Parent)
                if(Regex.IsMatch(parent.Name,@"^(?:gip\d+\.\d+|[0-9a-fA-F]{4}:[0-9a-fA-F]{4}:[0-9a-fA-F]{4}\.[0-9a-fA-F]+)$")){scope=parent.FullName;break;}
            var peripheral=usb.SingleOrDefault(d=>d.Id==device.UsbId&&IsChild(input,d.Path));
            // Receiver slots and gip adapter numbers are allocated on connect,
            // so they cannot stand in for a controller's own serial number.
            var receiver=peripheral!=null&&(
                ReadFile(peripheral.Path+"/idVendor").ToLowerInvariant() is "045e" && ReadFile(peripheral.Path+"/idProduct").ToLowerInvariant() is "0719" or "0291" or "02e6" or "02fe" or "02f9" ||
                ReadFile(peripheral.Path+"/idVendor").ToLowerInvariant()=="28de"&&ReadFile(peripheral.Path+"/idProduct").ToLowerInvariant()=="1142");
            var port=peripheral!=null&&bus=="0003"&&!receiver?peripheral.Id+"/"+ControllerPort(Path.GetRelativePath(peripheral.Path,input)):null;
            var valid=Regex.IsMatch(vendor,@"^[0-9a-fA-F]{4}$")&&Regex.IsMatch(product,@"^[0-9a-fA-F]{4}$");
            var id=valid&&(serial.Length>0||port!=null)?ControllerIdentity(vendor,product,serial,port??""):null;
            var name=ReadFile(input+"/name");
            controllers.Add(new(id,name.Length>0?name:"Game controller",serial.Length>0?"Serial":port!=null?"Port":"Unavailable",scope,[],device.UsbId,serial.Length>0?serial:null,
                id==null?"The driver does not report a stable controller identity. Assign the whole receiver or hub instead.":null));
        }
        // A driver's separate gamepad event interfaces share one physical pad.
        // Identical serials on different physical parents stay separate, so the
        // policy can reject the ambiguous identity instead of merging them.
        controllers=controllers.DistinctBy(c=>(c.Path,c.Id)).ToList();
        devices=devices.Select(d=>{
            if(d.Station!=null)return d;
            var matches=controllers.Where(c=>paths[d.Node]==c.Path||IsChild(paths[d.Node],c.Path)).ToArray();
            if(matches.Length>0)return d with{ControllerId=matches.Length==1?matches[0].Id??"controller:unidentified":"controller:shared"};
            // hid-steam removes its evdev gamepad while a raw HID client is
            // open. Keep those orphaned interfaces out of default ownership
            // when controllers are split, rather than leaking them to primary.
            if(d.Kind=="Hidraw"&&Regex.IsMatch(paths[d.Node],@"/(?:0003|0005):28DE:(?:1102|1142)\.[0-9A-Fa-f]+/",RegexOptions.IgnoreCase))return d with{ControllerId="controller:unidentified"};
            return d;
        }).ToList();
        controllers=controllers.Select(c=>c with{Nodes=devices.Where(d=>paths[d.Node]==c.Path||IsChild(paths[d.Node],c.Path)).Select(d=>d.Node).Order().ToArray(),
            Problem=controllers.Count(other=>other.Path==c.Path)>1?"Multiple controllers share an input interface. Assign the whole receiver instead.":c.Problem}).ToList();
        usb=usb.Select(d=>d with{Nodes=devices.Where(p=>p.UsbId==d.Id).Select(p=>p.Node).Distinct().Order().ToArray()}).ToList();
        return new(usb.OrderBy(d=>d.Name,StringComparer.Ordinal).ThenBy(d=>d.Path,StringComparer.Ordinal).ToArray(),devices.ToArray(),errors.Distinct().ToArray(),controllers.OrderBy(c=>c.Name,StringComparer.Ordinal).ThenBy(c=>c.Path,StringComparer.Ordinal).ToArray());
    }
    static bool IsChild(string path,string parent)=>path.StartsWith(parent.TrimEnd('/')+"/",StringComparison.Ordinal);
    public static string StablePort(string path)
    {
        path=Regex.Replace(path,@"/usb\d+(?=/|$)","/usb");
        return Regex.Replace(path,@"/\d+-(\d+(?:\.\d+)*)(?=/|$)","/port-$1");
    }
    public static string Identity(string vendor,string product,string serial,string port)=>"usb:"+Canonical.Hash(new{Vendor=vendor.ToLowerInvariant(),Product=product.ToLowerInvariant(),Kind=serial.Length>0?"Serial":"Port",Value=serial.Length>0?serial:StablePort(port)});
    public static string ControllerIdentity(string vendor,string product,string serial,string port)=>"controller:"+Canonical.Hash(new{Vendor=vendor.ToLowerInvariant(),Product=product.ToLowerInvariant(),Kind=serial.Length>0?"Serial":"Port",Value=serial.Length>0?serial:port});
    static string ControllerPort(string path)
    {
        path=Regex.Replace(path,@"(?:^|/)input/input\d+$","");
        path=Regex.Replace(path,@"(^|/)\d+-[\d.]+:(\d+\.\d+)","$1interface-$2");
        return Regex.Replace(path,@"([0-9a-fA-F]{4}:[0-9a-fA-F]{4}:[0-9a-fA-F]{4})\.[0-9a-fA-F]+","$1");
    }
    static bool HasBit(string bitmap,int bit)
    {
        var words=bitmap.Split(' ',StringSplitOptions.RemoveEmptyEntries);var index=bit/(IntPtr.Size*8);
        return index<words.Length&&ulong.TryParse(words[words.Length-1-index],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var word)&&(word&(1UL<<(bit%(IntPtr.Size*8))))!=0;
    }
}
