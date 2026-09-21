using Xur.Agent;
using Xur.Domain;

static class GpuInventoryTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"xur-drm-"+Guid.NewGuid().ToString("N"));
        var sys=root+"/sys";var dev=root+"/dev";
        void Dir(string path)=>Directory.CreateDirectory(path);
        void Write(string path,string text=""){Dir(Path.GetDirectoryName(path)!);File.WriteAllText(path,text);}
        void Link(string path,string target){Dir(Path.GetDirectoryName(path)!);Directory.CreateSymbolicLink(path,Path.GetRelativePath(Path.GetDirectoryName(path)!,target));}
        void Card(string owner,string name,bool connected=true)
        {
            Dir(owner+"/drm/"+name);Link(owner+"/drm/"+name+"/device",owner);
            Link(sys+"/class/drm/"+name,owner+"/drm/"+name);Write(dev+"/dri/"+name);
            Write(sys+"/class/drm/"+name+"-Virtual-1/status",connected?"connected":"disconnected");
        }
        try
        {
            var guid="12345678-1234-5678-9012-123456789abc";
            var hyperv=sys+"/devices/LNXSYSTM:00/LNXSYBUS:00/ACPI0004:00/MSFT1000:00/"+guid;
            Dir(hyperv);Dir(sys+"/bus/vmbus/drivers/hyperv_drm");
            Link(hyperv+"/subsystem",sys+"/bus/vmbus");Link(hyperv+"/driver",sys+"/bus/vmbus/drivers/hyperv_drm");Card(hyperv,"card0");
            var diagnostic=await DisplayDiagnostics.Collect(sys,dev,root+"/proc",false);
            var card=diagnostic["drm"]!.AsArray().Single(d=>d!["name"]!.GetValue<string>()=="card0")!;
            check(card["device"]!.GetValue<string>()==hyperv && card["driver"]!.GetValue<string>()==sys+"/bus/vmbus/drivers/hyperv_drm" && card["subsystem"]!.GetValue<string>()==sys+"/bus/vmbus","Relative sysfs links resolve through the DRM class alias to their full device and driver paths");
            var observed=await GpuInventory.Observe(sys,dev);var gpu=observed.Single();
            check(gpu.Pci=="vmbus:"+guid && gpu.Name=="Hyper-V virtual display" && gpu.Cards!.Single()=="/dev/dri/card0" && gpu.Displays!.Length==1 && gpu.Nodes.Length==0,"Hyper-V VMBus display is discovered without PCI or a render node");
            var recipe=new Recipe("gaming-workstation","Gaming workstation","host:plasma",[],0,"","Display",1,0,"",Kind:"Workstation",Engine:"Plasma");
            ProfilePolicy.Validate(new("1","Desktop",1,[new("1","Desktop",recipe,[gpu.Pci],"desktop")]),new("fixture",observed,[]));
            check(gpu.Problems.Length>0 && gpu.MemoryMiB==0,"Display-only device remains ineligible for compute");
            Directory.Delete(sys+"/class/drm/card0");Directory.Delete(sys+"/class/drm/card0-Virtual-1",true);File.Delete(dev+"/dri/card0");Card(hyperv,"card7");
            var renumbered=(await GpuInventory.Observe(sys,dev)).Single();
            check(renumbered.Pci==gpu.Pci && renumbered.Cards!.Single()=="/dev/dri/card7","VMBus identity survives DRM card renumbering");
            var pci=sys+"/devices/pci0000:00/0000:00:02.0";Dir(pci);Link(sys+"/bus/pci/devices/0000:00:02.0",pci);
            Write(pci+"/class","0x030000");Write(pci+"/vendor","0x8086");Write(pci+"/device","0x1234");Card(pci,"card1");
            Dir(pci+"/drm/renderD128");Link(pci+"/drm/renderD128/device",pci);Link(sys+"/class/drm/renderD128",pci+"/drm/renderD128");Write(dev+"/dri/renderD128");
            var mixed=await GpuInventory.Observe(sys,dev);
            check(mixed.Length==2 && mixed.Single(g=>g.Vendor=="Intel").Nodes.Single()=="/dev/dri/renderD128","PCI and VMBus displays coexist without duplicate PCI entries");
            Write(sys+"/class/drm/card7-Virtual-1/status","disconnected");
            check((await GpuInventory.Observe(sys,dev)).Single(g=>g.Pci==gpu.Pci).Displays!.Length==0,"Disconnected virtual output remains an observed adapter");
            var platform=sys+"/devices/platform/simple-framebuffer.0";Dir(platform);Dir(sys+"/bus/platform/drivers/simple-framebuffer");
            Link(platform+"/subsystem",sys+"/bus/platform");Link(platform+"/driver",sys+"/bus/platform/drivers/simple-framebuffer");Card(platform,"card2");
            var firmware=(await GpuInventory.Observe(sys,dev)).Single(g=>g.Driver=="simple-framebuffer");
            check(firmware.Pci=="sysfs:platform/simple-framebuffer.0" && firmware.Name=="Firmware display","Platform DRM display is discovered with a stable device identity");
            var report=await DisplayDiagnostics.Collect(sys,dev,root+"/proc",false);
            var excluded=report["inventory"]!.AsArray().Single(g=>g!["gpu"]!["pci"]!.GetValue<string>()==gpu.Pci)!;
            check(excluded["workstationEligible"]!.GetValue<bool>() && excluded["excludedBecause"]!.AsArray().Count==0,"Diagnostics retain disconnected display-capable adapters");
            var fbSys=root+"/framebuffer/sys";var fbDev=root+"/framebuffer/dev";var fbProc=root+"/framebuffer/proc";
            Write(fbSys+"/class/graphics/fb0/name","hyperv_fb");Write(fbDev+"/fb0");Dir(fbSys+"/module/hyperv_fb");
            Write(fbProc+"/cmdline","bootstrapToken=DO-NOT-COLLECT");
            var fbReport=await DisplayDiagnostics.Collect(fbSys,fbDev,fbProc,false);
            check(fbReport["inventory"]!.AsArray().Count==0 && fbReport["framebuffer"]!.AsArray().Count==1 && fbReport["summary"]!.GetValue<string>().Contains("framebuffer") && !fbReport.ToJsonString().Contains("DO-NOT-COLLECT"),"Diagnostics distinguish framebuffer-only Hyper-V and omit the kernel command line");
            Write(dev+"/input/event7");Write(dev+"/snd/controlC0");Write(dev+"/hidraw2");Write(dev+"/uinput");Write(dev+"/uhid");Write(dev+"/private-key");
            File.CreateSymbolicLink(dev+"/input/event8",dev+"/private-key");
            string[] aclArgs=[];
            var permissions=System.Text.Json.JsonSerializer.SerializeToNode(await DisplayDiagnostics.DevicePermissions(dev,(exe,args,timeout)=>
            {check(exe=="getfacl"&&timeout==10,"Device diagnostics use a bounded read-only ACL probe");aclArgs=args;return Task.FromResult(new ProcessResult(0,"# file: "+dev+"/input/event7\nmask::---\n"));}))!;
            check(new[]{"dri/card1","input/event7","snd/controlC0","hidraw2","uinput","uhid"}.All(n=>aclArgs.Contains(dev+"/"+n))&&!aclArgs.Any(a=>a.EndsWith("private-key")||a.EndsWith("event8")),"ACL diagnostics include graphics and peripherals but exclude other files and aliases");
            check(permissions["acl"]!.GetValue<string>().Contains("mask::---")&&permissions["exitCode"]!.GetValue<int>()==0,"Diagnostics preserve ACL masks with their device paths");
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
