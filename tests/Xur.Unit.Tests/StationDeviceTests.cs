using Xur.Agent;
using Xur.Domain;
using Xur.Control;

static class StationDeviceTests
{
    public static void Run(Action<bool,string> check)
    {
        var recipe=new Recipe("gaming-workstation","Desktop","host:plasma",[],0,"","Display",1,0,"",Kind:"Workstation",Engine:"Plasma");
        Workload Station(string id,bool primary=false,string[]? usb=null)=>new(id,"Desktop",recipe,[id=="1"?"0000:01:00.0":"0000:41:00.0"],"station-"+id,new("user"+id,1000+int.Parse(id)),new(primary,usb));
        string Id(string serial,string port="port")=>StationDeviceInventoryReader.Identity("1234","abcd",serial,port);
        var hub=new UsbPeripheral(Id("hub"),"Desk hub","Serial","/usb/hub",true,false,[],[]);
        var keyboard=new UsbPeripheral(Id("keyboard"),"Keyboard","Serial","/usb/hub/keyboard",false,false,[hub.Path],["/dev/input/event5"]);
        var headset=new UsbPeripheral(Id("headset"),"Headset","Serial","/usb/hub/headset",false,false,[hub.Path],["/dev/snd/controlC2"]);
        var inventory=new StationDeviceInventory([hub,keyboard,headset],[new("/dev/input/event5","Input",keyboard.Id),new("/dev/snd/controlC2","Audio",headset.Id),new("/dev/input/event0","Input"),new("/dev/snd/controlC0","Audio"),new("/dev/snd/controlC1","Audio",Gpu:"0000:41:00.0"),new("/dev/sda","Block",headset.Id)],[]);
        var primary=Station("1",true);var secondary=Station("2",usb:[hub.Id]);
        var plan=StationDevicePolicy.Plan([primary,secondary],inventory);
        check(plan[0].Nodes.SequenceEqual(new[]{"/dev/input/event0","/dev/snd/controlC0"}),"Primary receives unassigned input and built-in audio only");
        check(plan[1].Nodes.SequenceEqual(new[]{"/dev/input/event5","/dev/snd/controlC1","/dev/snd/controlC2"}),"Hub follows input/audio children and selected GPU owns display audio");
        check(!plan.Any(p=>p.Nodes.Contains("/dev/sda")),"Hub assignment never grants raw storage access");
        var remote=inventory with{Devices=[..inventory.Devices,new("/dev/input/event40","Input",Station:"2"),new("/dev/hidraw7","Hidraw",Station:"unassigned")]};
        var remotePlan=StationDevicePolicy.Plan([primary,secondary],remote);
        check(remotePlan[1].Nodes.Contains("/dev/input/event40")&&!remotePlan[0].Nodes.Contains("/dev/input/event40")&&!remotePlan.Any(a=>a.Nodes.Contains("/dev/hidraw7")),"Moonlight virtual input belongs only to its station; unknown stream identities never fall back to primary");
        var rules=StationSeats.RulesText([primary,secondary],[],remote,remotePlan,node=>"/devices/test/"+Path.GetFileName(node));
        check(rules.Contains("ATTRS{phys}==\""+StationSeats.Physical("2"))&&rules.Contains("DEVPATH==\"/devices/test/event5\", ENV{ID_SEAT}:=\""+StationSeats.Seat("2")),"Physical USB and virtual Moonlight input use the same dedicated logind seat");
        check(rules.Contains("seat-xur-unassigned")&&StationSeats.Seat("1")!=StationSeats.Seat("2"),"Unclaimed input waits for reconciliation instead of entering another desktop");
        var card=new GpuDevice("0000:41:00.0","NVIDIA","GPU","nvidia","",24576,["/dev/dri/renderD130"],[],["/dev/dri/card3"]);
        var launch=StationRuntime.SessionArguments(secondary,card,1002,"/var/home/user2");
        check(launch.Contains("--property=PAMName=login")&&launch.Contains("--property=Slice=user-1002.slice")&&launch.Contains("--setenv=XDG_SEAT="+StationSeats.Seat("2"))&&launch.Contains("--setenv=KWIN_DRM_DEVICES=/dev/dri/card3")&&launch[^1]=="/usr/bin/startplasma-wayland","Each desktop starts as its own PAM user, logind seat and assigned GPU without a shared display manager");
        var moved=inventory with{Usb=[hub with{Path="/usb/elsewhere"},keyboard with{Path="/usb/elsewhere/keyboard",Ancestors=["/usb/elsewhere"]},headset with{Path="/usb/elsewhere/headset",Ancestors=["/usb/elsewhere"]}]};
        check(StationDevicePolicy.Plan([primary,secondary],moved)[1].Nodes.SequenceEqual(plan[1].Nodes),"Serial hub survives changing USB ports with its attached devices");
        var conflict=StationDevicePolicy.Plan([primary with{Devices=new(true,[keyboard.Id])},secondary],inventory);
        check(conflict.All(p=>p.Problems.Any(m=>m.Contains("overlap")))&&!conflict.Any(p=>p.Nodes.Contains("/dev/input/event5")),"Hub/child overlap is blocked for both owners");
        check(StationDevicePolicy.Plan([primary,secondary],inventory with{Usb=[]})[1].Problems.Length==1,"Missing selected hub is reported instead of silently substituted");
        var duplicate=inventory with{Usb=[..inventory.Usb,hub with{Path="/usb/duplicate"}]};
        check(StationDevicePolicy.Plan([primary,secondary],duplicate)[1].Problems.Any(p=>p.Contains("multiple")),"Duplicate hub serials fail closed");
        check(!StationDevicePolicy.Plan([primary,secondary],duplicate).Any(p=>p.Nodes.Contains("/dev/input/event5")),"Ambiguous hub descendants never leak to the primary station");
        check(StationDevicePolicy.Plan([primary,secondary],inventory with{Errors=["Enumeration failed"]}).All(p=>p.Nodes.Length==0&&p.Problems.Length>0),"Incomplete inventory grants no device nodes");
        var duplicatedKeyboard=inventory with{Usb=[..inventory.Usb,keyboard with{Path="/usb/duplicate-keyboard"}]};
        check(!StationDevicePolicy.Plan([primary,secondary],duplicatedKeyboard).Any(p=>p.Nodes.Contains("/dev/input/event5")),"Ambiguous peripheral identity never falls back to primary");
        check(StationDeviceInventoryReader.Identity("1234","ABCD","serial","first")==Id("serial","second"),"Serial identity is independent of port and USB IDs are normalized");
        check(Id("","devices/pci0000:00/0000:00:14.0/usb1/1-2/1-2.3")==Id("","devices/pci0000:00/0000:00:14.0/usb9/9-2/9-2.3"),"Port identity survives USB bus renumbering");
        check(Id("","devices/pci/usb1/1-2")!=Id("","devices/pci/usb1/1-3"),"Serial-less devices remain bound to the selected physical port");
        foreach(var workloads in new[]{new[]{primary,secondary with{Devices=new(true)}},new[]{primary,secondary with{User=primary.User}},new[]{primary with{Devices=new(true,[hub.Id])},secondary}})
        {
            var blocked=false;try{StationDevicePolicy.Validate(workloads);}catch(InvalidOperationException){blocked=true;}
            check(blocked,"Conflicting primary, account or USB selections are rejected");
        }
        check(primary.Fingerprint!=(primary with{Devices=new(false)}).Fingerprint,"Primary role is part of workstation runtime identity");
        check((primary with{Devices=new(true,[keyboard.Id,hub.Id])}).Fingerprint==(primary with{Devices=new(true,[hub.Id,keyboard.Id])}).Fingerprint,"USB selection order does not restart the workstation");
        var root=Path.Combine(Path.GetTempPath(),"xur-station-ports-"+Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root+"/legacy");File.WriteAllText(root+"/legacy/sunshine.conf","port = 47989\n");
            var ports=new StationStreamPorts(root+"/ports");var first=ports.Get("1");var second=ports.Get("2");
            check(first==48089&&second==48189&&ports.Get("legacy")==47989,"New streaming ports preserve legacy running workstation reservation");
            check(new StationStreamPorts(root+"/ports").Get("1")==first,"Streaming address persists across allocator restart");
            check(!StationStreamPorts.FirewallPorts(first).Intersect(StationStreamPorts.FirewallPorts(second)).Any(),"Each stream has independent firewall ports");
            check(StationLauncher.Create("server","linux",first).Text.Contains("server:48089")&&StationLauncher.Address("fd71::2",second)=="[fd71::2]:48189","Moonlight launchers use station-specific ports including IPv6");
            File.WriteAllText(root+"/ports/3",first.ToString());var blocked=false;try{ports.Get("4");}catch(InvalidOperationException){blocked=true;}check(blocked,"Duplicate persisted streaming reservations block startup");
        }
        finally{Directory.Delete(root,true);}
        InventoryFixture(check);
    }
    static void InventoryFixture(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"xur-usb-"+Guid.NewGuid().ToString("N"));var sys=root+"/sys";var dev=root+"/dev";
        void Write(string path,string value=""){Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,value);}
        void Link(string from,string to){Directory.CreateDirectory(Path.GetDirectoryName(from)!);Directory.CreateDirectory(to);Directory.CreateSymbolicLink(from,to);}
        try
        {
            var hub=sys+"/devices/pci0000:00/0000:00:14.0/usb9/9-2";var keyboard=hub+"/9-2.3";
            foreach(var (path,serial,kind) in new[]{(hub,"hub-serial","09"),(keyboard,"key-serial","00")}){Write(path+"/idVendor","1234");Write(path+"/idProduct","abcd");Write(path+"/serial",serial);Write(path+"/bDeviceClass",kind);Link(sys+"/bus/usb/devices/"+Path.GetFileName(path),path);}
            Link(sys+"/class/input/event7",keyboard+"/9-2.3:1.0/input/input4/event7");Write(dev+"/input/event7");
            Link(sys+"/class/sound/controlC3",sys+"/devices/pci0000:00/0000:00:01.1/0000:01:00.1/sound/card3/controlC3");Write(dev+"/snd/controlC3");
            var gpu=new GpuDevice("0000:01:00.0","NVIDIA","RTX","nvidia","",1,[],[]);
            var virtualInput=sys+"/devices/virtual/input/input20";Write(virtualInput+"/phys",StationSeats.Physical("2")+"/input0");Link(sys+"/class/input/event40",virtualInput+"/event40");Write(dev+"/input/event40");
            var observed=StationDeviceInventoryReader.Read([gpu],sys,dev);
            check(observed.Usb.Single(d=>d.Hub).Serial=="hub-serial","USB inventory exposes the hardware serial for identification");
            check(observed.Usb.Length==2&&observed.Usb.Single(d=>!d.Hub).Ancestors.SequenceEqual([hub]),"USB discovery follows real sysfs parent topology");
            check(observed.Devices.Single(d=>d.Node.EndsWith("event7")).UsbId==observed.Usb.Single(d=>!d.Hub).Id,"Input node maps to its physical USB device through symlinks");
            check(observed.Devices.Single(d=>d.Kind=="Audio").Gpu==gpu.Pci,"HDMI audio sibling function maps to the correct PCI GPU through upstream bridges");
            check(observed.Devices.Single(d=>d.Node.EndsWith("event40")).Station==StationSeats.Physical("2")+"/input0","Virtual input inventory follows the physical seat tag through sysfs parents");
            check(observed.Errors.Length==0,"Read-only device inventory completes without runtime tools");
        }finally{Directory.Delete(root,true);}
    }
}
