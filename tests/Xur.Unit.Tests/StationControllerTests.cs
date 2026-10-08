using Xur.Agent;
using Xur.Domain;

static class StationControllerTests
{
    public static void Run(Action<bool,string> check)
    {
        var recipe=new Recipe("gaming-workstation","Desktop","host:plasma",[],0,"","Display",1,0,"",Kind:"Workstation",Engine:"Plasma");
        Workload Station(string id,StationDevices devices)=>new(id,id,recipe,[],"desk-"+id,new("user"+id,1000+int.Parse(id)),devices);
        string Id(string serial)=>StationDeviceInventoryReader.ControllerIdentity("045e","0b13",serial,"");
        var receiver=new UsbPeripheral("usb:"+new string('a',64),"Xbox Wireless Adapter","Port","/usb/receiver",false,false,[],[]);
        var pads=new[]{"a","b","c"}.Select((s,n)=>new ControllerPeripheral(Id(s),"Xbox controller","Serial","/usb/receiver/gip"+n,["/dev/input/event"+n],receiver.Id,s)).ToArray();
        var inventory=new StationDeviceInventory([receiver],pads.SelectMany((p,n)=>new[]{new StationPeripheral(p.Nodes[0],"Input",receiver.Id,ControllerId:p.Id),new StationPeripheral("/dev/input/js"+n,"Input",receiver.Id,ControllerId:p.Id),new StationPeripheral("/dev/hidraw"+n,"Hidraw",receiver.Id,ControllerId:p.Id)}).Concat([new StationPeripheral("/dev/input/event9","Input",receiver.Id)]).ToArray(),[],pads);
        var a=Station("1",new(true,Controllers:[pads[0].Id!]));var b=Station("2",new(Controllers:[pads[1].Id!,pads[2].Id!]));
        var plan=StationDevicePolicy.Plan([a,b],inventory);
        check(plan[0].Nodes.SequenceEqual(["/dev/hidraw0","/dev/input/event0","/dev/input/event9","/dev/input/js0"]),"Primary can explicitly own one controller on a shared Xbox adapter");
        check(plan[1].Nodes.SequenceEqual(["/dev/hidraw1","/dev/hidraw2","/dev/input/event1","/dev/input/event2","/dev/input/js1","/dev/input/js2"]),"A secondary can own multiple controllers and their event, joystick and hidraw interfaces on the same adapter");
        check(!plan[0].Nodes.Intersect(plan[1].Nodes).Any(),"Shared receiver controllers have disjoint device permissions");
        var hubOwner=b with{Devices=new(false,[receiver.Id],[pads[1].Id!,pads[2].Id!])};
        var hubPlan=StationDevicePolicy.Plan([a,hubOwner],inventory);
        check(hubPlan[0].Nodes.Contains("/dev/input/event0")&&!hubPlan[1].Nodes.Contains("/dev/input/event0")&&hubPlan[1].Nodes.Contains("/dev/input/event9"),"Individual controller selection overrides whole receiver ownership");
        var unclaimed=StationDevicePolicy.Plan([a,b with{Devices=new(Controllers:[pads[1].Id!])}],inventory);
        check(unclaimed[0].Nodes.Contains("/dev/input/event2"),"Unselected identifiable controllers follow the primary workstation");
        var duplicate=inventory with{Controllers=[..pads,pads[1] with{Path="/usb/other"}]};
        check(StationDevicePolicy.Plan([a,b],duplicate).All(p=>!p.Nodes.Contains("/dev/input/event1")),"Duplicate controller identities are blocked from every desktop");
        var offline=StationDevicePolicy.Plan([a,b],inventory with{Controllers=[pads[0],pads[2]],Devices=inventory.Devices.Where(d=>d.ControllerId!=pads[1].Id).ToArray()});
        check(offline[1].Problems.Any(p=>p.Contains("disconnected"))&&offline[1].Nodes.Contains("/dev/input/event2"),"Disconnected controller assignments remain reserved without blocking another controller");
        var unknown=inventory with{Devices=[..inventory.Devices,new("/dev/input/event10","Input",receiver.Id,ControllerId:"controller:unidentified")]};
        check(StationDevicePolicy.Plan([a,b],unknown).All(p=>!p.Nodes.Contains("/dev/input/event10")),"A receiver controller awaiting its serial cannot briefly enter another desktop");
        check(StationDevicePolicy.Plan([a with{Devices=new(true)},b with{Devices=new(false,[receiver.Id])}],unknown)[1].Nodes.Contains("/dev/input/event10"),"Whole receiver assignment still supports drivers without per-controller identities");
        check(StationDevicePolicy.Plan([a,b],inventory with{Errors=["Discovery failed"]}).All(p=>p.Nodes.Length==0),"Incomplete controller discovery grants no peripherals");
        foreach(var devices in new[]{new StationDevices(Controllers:[pads[0].Id!]),new StationDevices(Controllers:["/dev/input/event0"]),new StationDevices(Controllers:Enumerable.Repeat(pads[1].Id!,65).ToArray())})
        {
            var rejected=false;try{StationDevicePolicy.Validate([a,b with{Devices=devices}]);}catch(InvalidOperationException){rejected=true;}
            check(rejected,"Duplicate, transient or oversized controller selections are rejected");
        }
        check(a.Fingerprint!=(a with{Devices=new(true,Controllers:[pads[1].Id!])}).Fingerprint,"Controller reassignment changes the desktop runtime fingerprint");
        check(b.Fingerprint==(b with{Devices=new(Controllers:[pads[2].Id!,pads[1].Id!])}).Fingerprint,"Controller selection order does not restart a desktop");
        var legacy=a with{Devices=new(true)};
        check(legacy.Fingerprint==Canonical.Hash(new{recipe.Kind,recipe.Image,legacy.Gpus,legacy.User,Devices=new{Primary=true,Usb=Array.Empty<string>()}}),"Profiles without controller selections retain their previous runtime fingerprint");
        check(Id("a")==StationDeviceInventoryReader.ControllerIdentity("045E","0B13","a","another-slot")&&Id("a")!=Id("b"),"Controller serial identity survives receiver slot changes and distinguishes identical models");
        var rules=StationSeats.RulesText([a,b],[],inventory,plan,node=>"/devices/receiver/input/input"+(node.EndsWith('0')?"0":"1")+"/"+Path.GetFileName(node));
        check(rules.Contains("DEVPATH==\"/devices/receiver/input/input0\", ENV{ID_SEAT}:=\""+StationSeats.Seat(a.Id)),"Legacy joystick nodes and input parents receive the same controller seat");
        Inventory(check);
    }

    static void Inventory(Action<bool,string> check)
    {
        var root=Path.GetFullPath(".build/evidence/controller-inventory-"+Guid.NewGuid().ToString("N"));var sys=root+"/sys";var dev=root+"/dev";
        void Write(string file,string value=""){Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllText(file,value);}
        void Link(string file,string target){Directory.CreateDirectory(Path.GetDirectoryName(file)!);Directory.CreateDirectory(target);Directory.CreateSymbolicLink(file,target);}
        var receiver=sys+"/devices/pci0000:00/0000:00:14.0/usb9/9-2";
        void Pad(int adapter,int input,int eventId,string serial)
        {
            var path=receiver+"/9-2:1.0/gip"+adapter+"/gip"+adapter+".0/input/input"+input;
            foreach(var (file,value) in new[]{("name","Microsoft Xbox Controller"),("uniq",serial),("id/vendor","045e"),("id/product","0b13"),("id/bustype","0006"),("capabilities/key","1000000000000 0 0 0 0"),("capabilities/abs","3")})Write(path+"/"+file,value);
            Link(sys+"/class/input/event"+eventId,path+"/event"+eventId);Write(dev+"/input/event"+eventId);
            Link(sys+"/class/input/js"+eventId,path+"/js"+eventId);Write(dev+"/input/js"+eventId);
        }
        string Wired(int bus,int hid,int eventId)
        {
            var usb=sys+"/devices/pci0000:00/0000:00:14.0/usb"+bus+"/"+bus+"-3";var path=usb+"/"+bus+"-3:1.0/0003:045E:028E."+hid.ToString("X4")+"/input/input"+eventId;
            Write(usb+"/idVendor","045e");Write(usb+"/idProduct","028e");Link(sys+"/bus/usb/devices/"+bus+"-3",usb);
            foreach(var (file,value) in new[]{("name","Wired Xbox pad"),("id/vendor","045e"),("id/product","028e"),("id/bustype","0003"),("capabilities/key","1000000000000 0 0 0 0"),("capabilities/abs","3")})Write(path+"/"+file,value);
            Link(sys+"/class/input/event"+eventId,path+"/event"+eventId);Write(dev+"/input/event"+eventId);return usb;
        }
        try
        {
            Write(receiver+"/idVendor","045e");Write(receiver+"/idProduct","02fe");Write(receiver+"/product","Xbox Wireless Adapter");Link(sys+"/bus/usb/devices/9-2",receiver);
            Pad(0,7,7,"serial-a");Pad(1,9,9,"serial-b");Pad(2,10,10,"");
            var observed=StationDeviceInventoryReader.Read([],sys,dev);
            check(observed.Errors.Length==0&&observed.Controllers?.Length==3,"One Xbox Wireless Adapter discovers separate controllers, including a controller without a serial");
            var a=observed.Controllers!.Single(c=>c.Serial=="serial-a");var b=observed.Controllers!.Single(c=>c.Serial=="serial-b");
            check(a.Id!=b.Id&&a.UsbId==b.UsbId&&a.Nodes.Length==2,"Xbox inventory groups each controller's event and joystick nodes under its own identity");
            check(observed.Controllers!.Single(c=>c.Serial==null).Id==null&&observed.Devices.Single(d=>d.Node.EndsWith("event10")).ControllerId=="controller:unidentified","Serial-less wireless controllers never use a transient GIP slot as a saved identity");
            Directory.Delete(sys+"/class",true);Directory.Delete(receiver+"/9-2:1.0",true);
            Pad(5,80,40,"serial-b");Pad(6,81,41,"serial-a");
            var reconnected=StationDeviceInventoryReader.Read([],sys,dev);
            check(reconnected.Controllers!.Single(c=>c.Serial=="serial-a").Id==a.Id&&reconnected.Controllers!.Single(c=>c.Serial=="serial-b").Id==b.Id,"Controller inventory survives reconnecting in reverse order with new GIP, input and event numbers");
            var steam=receiver+"/9-2:1.1/0003:28DE:1142.0099";var input=steam+"/input/input90";
            foreach(var (file,value) in new[]{("name","Wireless Steam Controller"),("uniq","steam-serial"),("id/vendor","28de"),("id/product","1142"),("id/bustype","0003"),("capabilities/key","1000000000000 0 0 0 0"),("capabilities/abs","3")})Write(input+"/"+file,value);
            Link(sys+"/class/input/event90",input+"/event90");Write(dev+"/input/event90");Link(sys+"/class/hidraw/hidraw5",steam+"/hidraw/hidraw5");Write(dev+"/hidraw5");
            var withSteam=StationDeviceInventoryReader.Read([],sys,dev);var pad=withSteam.Controllers!.Single(c=>c.Serial=="steam-serial");
            check(pad.Nodes.Contains(dev+"/hidraw5")&&withSteam.Devices.Single(d=>d.Node==dev+"/hidraw5").ControllerId==pad.Id,"Original Steam Controller discovery groups its gamepad and controller-specific HID interface");
            Directory.Delete(sys+"/class/input/event90");Directory.Delete(input,true);
            var rawOnly=StationDeviceInventoryReader.Read([],sys,dev);
            check(rawOnly.Devices.Single(d=>d.Node==dev+"/hidraw5").ControllerId=="controller:unidentified","Steam Controller hidraw cannot fall back to another workstation when Steam removes its gamepad interface");
            var wired=Wired(9,1,91);var firstWired=StationDeviceInventoryReader.Read([],sys,dev).Controllers!.Single(c=>c.Name=="Wired Xbox pad");
            Directory.Delete(sys+"/class/input/event91");Directory.Delete(sys+"/bus/usb/devices/9-3");Directory.Delete(wired,true);
            Wired(12,99,95);var secondWired=StationDeviceInventoryReader.Read([],sys,dev).Controllers!.Single(c=>c.Name=="Wired Xbox pad");
            check(firstWired.Id!=null&&firstWired.Identity=="Port"&&firstWired.Id==secondWired.Id,"Wired controller port identity survives USB bus, HID instance and input numbering changes");
        }
        finally{Directory.Delete(root,true);}
    }
}
