namespace Xur.Domain;

// Persist identities, never Linux bus numbers, ALSA indices or /dev node names.
public record StationDevices(bool Primary=false,string[]? Usb=null);
public record UsbPeripheral(string Id,string Name,string Identity,string Path,bool Hub,bool RootHub,
    string[] Ancestors,string[] Nodes,bool Storage=false);
public record StationPeripheral(string Node,string Kind,string? UsbId=null,string? Gpu=null);
public record StationDeviceInventory(UsbPeripheral[] Usb,StationPeripheral[] Devices,string[] Errors);
public record StationDeviceAllocation(string WorkloadId,bool Primary,string[] Usb,string[] Nodes,string[] Audio,string[] Problems);

public static class StationDevicePolicy
{
    public static void Validate(Workload[] workloads)
    {
        var stations=workloads.Where(w=>w.Recipe.Kind=="Workstation").ToArray();
        if(stations.Count(w=>w.Devices?.Primary==true)>1)throw new InvalidOperationException("Choose one primary workstation.");
        var users=new HashSet<int>();var assigned=new HashSet<string>(StringComparer.Ordinal);
        foreach(var w in workloads)
        {
            if(w.Devices!=null&&w.Recipe.Kind!="Workstation")throw new InvalidOperationException("Only workstations can select USB devices.");
            if(w.Recipe.Kind!="Workstation")continue;
            if(w.User is {Temporary:false} user&&!users.Add(user.Uid))throw new InvalidOperationException("Each running workstation needs a different user. Temporary users are separate for each workstation.");
            var usb=w.Devices?.Usb??[];
            if(usb.Length>64||usb.Any(id=>id==null||!System.Text.RegularExpressions.Regex.IsMatch(id,@"^usb:[0-9a-f]{64}$")))throw new InvalidOperationException("Select USB devices from the device inventory.");
            if(usb.Any(id=>!assigned.Add(id)))throw new InvalidOperationException("A USB device or hub can belong to only one workstation.");
        }
    }

    public static StationDeviceAllocation[] Plan(Workload[] workloads,StationDeviceInventory inventory)
    {
        Validate(workloads);
        var stations=workloads.Where(w=>w.Recipe.Kind=="Workstation").OrderBy(w=>w.Id,StringComparer.Ordinal).ToArray();
        // A lone legacy station keeps its existing default role. For multiple
        // stations a primary is explicit; no list-order-dependent inheritance.
        var primary=stations.SingleOrDefault(w=>w.Devices?.Primary==true)?.Id??(stations.Length==1?stations[0].Id:null);
        var claims=new Dictionary<string,HashSet<string>>(StringComparer.Ordinal);
        var blockedPaths=new HashSet<string>(StringComparer.Ordinal);
        var problems=stations.ToDictionary(w=>w.Id,_=>new List<string>());
        void Claim(string id,string station)
        {if(!claims.TryGetValue(id,out var owners))claims[id]=owners=[];owners.Add(station);}
        foreach(var w in stations)
        foreach(var id in w.Devices?.Usb??[])
        {
            var matches=inventory.Usb.Where(d=>d.Id==id).ToArray();
            if(matches.Length!=1)
            {
                problems[w.Id].Add(matches.Length==0?"An assigned USB device is disconnected.":"An assigned USB identity matches multiple devices.");
                foreach(var candidate in matches)
                foreach(var item in inventory.Usb.Where(d=>d.Path==candidate.Path||candidate.Hub&&d.Ancestors.Contains(candidate.Path)))blockedPaths.Add(item.Path);
                continue;
            }
            var selected=matches[0];
            if(selected.RootHub){problems[w.Id].Add("Select an external hub or individual device, not a root USB controller.");continue;}
            foreach(var item in inventory.Usb.Where(d=>d.Path==selected.Path||selected.Hub&&d.Ancestors.Contains(selected.Path)))Claim(item.Path,w.Id);
        }
        foreach(var conflict in claims.Where(c=>c.Value.Count>1))
            foreach(var owner in conflict.Value)problems[owner].Add("USB assignments overlap through a selected hub.");
        var result=new List<StationDeviceAllocation>();
        foreach(var w in stations)
        {
            var nodes=new HashSet<string>(StringComparer.Ordinal);var audio=new HashSet<string>(StringComparer.Ordinal);var usb=new HashSet<string>(StringComparer.Ordinal);
            foreach(var device in inventory.Devices)
            {
                string? owner=null;
                if(device.UsbId!=null)
                {
                    var matches=inventory.Usb.Where(d=>d.Id==device.UsbId).ToArray();
                    if(matches.Length!=1)continue; // Duplicate serials never fall back to primary.
                    var physical=matches[0];
                    if(blockedPaths.Contains(physical.Path))continue;
                    if(claims.TryGetValue(physical.Path,out var owners)){if(owners.Count==1)owner=owners.Single();}
                    else owner=primary;
                }
                else if(device.Gpu!=null)owner=stations.SingleOrDefault(s=>s.Gpus.Contains(device.Gpu))?.Id;
                else owner=primary;
                if(owner!=w.Id)continue;
                // Native desktops get input/audio interfaces, never raw disks,
                // USB controllers or USB filesystem access from a hub selection.
                if(device.Kind is not ("Input" or "Hidraw" or "Audio"))continue;
                nodes.Add(device.Node);if(device.Kind=="Audio")audio.Add(device.Node);
                if(device.UsbId!=null)usb.Add(device.UsbId);
            }
            // Keep selected hubs/devices visible even when they have no input/audio interface.
            foreach(var item in inventory.Usb.Where(d=>claims.TryGetValue(d.Path,out var owners)&&owners.SetEquals([w.Id])))usb.Add(item.Id);
            result.Add(new(w.Id,w.Id==primary,usb.Order().ToArray(),inventory.Errors.Length==0?nodes.Order().ToArray():[],inventory.Errors.Length==0?audio.Order().ToArray():[],problems[w.Id].Concat(inventory.Errors).Distinct().ToArray()));
        }
        return result.ToArray();
    }
}
