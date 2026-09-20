using Xur.Agent;
using Xur.Domain;

static class NvidiaDeviceTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"xur-nvidia-"+Guid.NewGuid().ToString("N"));
        var proc=root+"/proc";var dev=root+"/dev";
        var info=proc+"/driver/nvidia/gpus/0000:41:00.0/information";
        var gpu=new GpuDevice("0000:41:00.0","NVIDIA","RTX 3090","nvidia","GPU-selected",24576,[],[]);
        var calls=0;var xml="";var exit=0;
        Task<ProcessResult> Run(string exe,string[] args,int timeout)
        {
            calls++;
            check(exe=="nvidia-smi"&&args.SequenceEqual(new[]{"--id=0000:41:00.0","--query","--xml-format"}),"Device fallback uses the PCI-selected XML query, never the selective minor_number query");
            return Task.FromResult(new ProcessResult(exit,xml));
        }
        Task<string> Resolve()=>NvidiaDevice.Resolve(gpu,proc,dev,Run);
        async Task Rejected(string expected,string name)
        {
            try {await Resolve();throw new Exception("Unexpected successful device lookup: "+name);}
            catch(InvalidOperationException e){check(e.Message.Contains(expected),name);}
        }
        string Xml(string minor="3",string pci="00000000:41:00.0",string uuid="GPU-selected")=>
            $"<gpu id='{pci}'><minor_number>{minor}</minor_number><uuid>{uuid}</uuid><pci><pci_bus_id>{pci}</pci_bus_id></pci></gpu>";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(info)!);Directory.CreateDirectory(dev);
            File.WriteAllText(dev+"/nvidia3","");File.WriteAllText(dev+"/nvidia0","");
            File.WriteAllText(info,"Model: NVIDIA GeForce RTX 3090\nGPU UUID: GPU-selected\nBus Location: 0000:41:00.0\nDevice Minor: 3\n");
            check(await Resolve()==dev+"/nvidia3"&&calls==0,"NVIDIA unload/workstation mapping uses driver device minor even when GPU index order differs");
            File.WriteAllText(dev+"/nvidiactl","");
            var startup=new List<string>();
            Task<ProcessResult> Prepare(string exe,string[] args,int timeout)
            {
                var command=exe+" "+string.Join(' ',args);startup.Add(command);
                // Reproduce the host: modules load and udev settles without creating nodes.
                if(command=="nvidia-modprobe --modeset")File.WriteAllText(dev+"/nvidia-modeset","");
                if(command=="nvidia-modprobe --unified-memory --create-nvidia-device-file=0")
                    foreach(var name in new[]{"nvidia-uvm","nvidia-uvm-tools"})File.WriteAllText(dev+"/"+name,"");
                return Task.FromResult(new ProcessResult(0,""));
            }
            var nodes=await NvidiaDevice.WorkstationNodes(gpu,proc,dev,Prepare);
            check(startup.SequenceEqual(new[]{"modprobe -a nvidia_modeset nvidia_uvm","nvidia-modprobe --modeset","nvidia-modprobe --unified-memory --create-nvidia-device-file=0","udevadm settle --timeout=10"})&&nodes.Contains(dev+"/nvidia-modeset")&&nodes.Contains(dev+"/nvidia-uvm-tools")&&nodes.Contains(dev+"/nvidia3")&&!nodes.Contains(dev+"/nvidia0"),"Workstation creates missing modeset and UVM devices before permissions without granting another GPU");
            startup.Clear();await NvidiaDevice.WorkstationNodes(gpu,proc,dev,Prepare);
            check(startup.All(c=>!c.StartsWith("nvidia-modprobe")),"Existing NVIDIA device nodes do not need recreation");
            File.Delete(dev+"/nvidia-modeset");
            try{await NvidiaDevice.WorkstationNodes(gpu,proc,dev,(e,a,t)=>Task.FromResult(new ProcessResult(0,"")));throw new Exception("Missing modeset node was accepted");}
            catch(InvalidOperationException e){check(e.Message.Contains("nvidia-modeset"),"Successful helper without a modeset node still blocks launch");}
            try{await NvidiaDevice.WorkstationNodes(gpu,proc,dev,(e,a,t)=>Task.FromResult(new ProcessResult(e=="nvidia-modprobe"?1:0,"fixture failure")));throw new Exception("Failed helper was accepted");}
            catch(InvalidOperationException e){check(e.Message.Contains("nvidia-modprobe --modeset")&&e.Message.Contains("fixture failure"),"NVIDIA device creation failure identifies the command and driver error");}
            try{await NvidiaDevice.WorkstationNodes(gpu,proc,dev,(e,a,t)=>e=="nvidia-modprobe"?throw new System.ComponentModel.Win32Exception(2):Task.FromResult(new ProcessResult(0,"")));throw new Exception("Missing helper was accepted");}
            catch(InvalidOperationException e){check(e.Message.Contains("host NVIDIA driver tools"),"Missing NVIDIA helper reports a host driver installation error");}
            await NvidiaDevice.WorkstationNodes(gpu,proc,dev,Prepare);
            File.Delete(dev+"/nvidia-uvm-tools");startup.Clear();nodes=await NvidiaDevice.WorkstationNodes(gpu,proc,dev,Prepare);
            check(nodes.Contains(dev+"/nvidia-uvm-tools")&&startup.Contains("nvidia-modprobe --unified-memory --create-nvidia-device-file=0")&&!startup.Contains("nvidia-modprobe --modeset"),"Missing UVM tools node is recreated without disturbing existing modeset node");
            File.WriteAllText(info,File.ReadAllText(info).Replace("Device Minor: 3","Device Minor: 0"));
            check(await Resolve()==dev+"/nvidia0","NVIDIA minor is re-observed instead of cached across driver reloads");
            File.WriteAllText(info,File.ReadAllText(info).Replace("GPU-selected","GPU-replaced"));
            await Rejected("UUID changed","Changed NVIDIA UUID blocks stale device access");
            File.WriteAllText(info,"Bus Location: 0000:81:00.0\nDevice Minor: 0\n");
            await Rejected("PCI identity changed","Mismatched driver PCI identity blocks stale device access");
            File.WriteAllText(info,"Device Minor: 0\nDevice Minor: 3\n");
            await Rejected("duplicate","Ambiguous driver device minors are rejected");
            File.Delete(info);
            xml="<?xml version='1.0'?><!DOCTYPE nvidia_smi_log SYSTEM 'nvsmi_device_v12.dtd'><nvidia_smi_log>"+Xml("0","00000000:01:00.0","GPU-other")+Xml()+"</nvidia_smi_log>";
            check(await Resolve()==dev+"/nvidia3","NVIDIA XML fallback matches the selected PCI identity among multiple GPUs and ignores external DTDs");
            xml="<nvidia_smi_log>"+Xml()+Xml()+"</nvidia_smi_log>";
            await Rejected("exactly one","Duplicate NVIDIA XML identities block mapping");
            foreach(var minor in new[]{"N/A","-1","3, 0","1048576"})
            {
                xml="<nvidia_smi_log>"+Xml(minor)+"</nvidia_smi_log>";
                await Rejected("valid device minor","Invalid NVIDIA minor is rejected: "+minor);
            }
            xml="<nvidia_smi_log>"+Xml(uuid:"GPU-replaced")+"</nvidia_smi_log>";
            await Rejected("UUID changed","XML fallback rejects a replaced GPU");
            xml="<nvidia_smi_log>"+Xml()+"</nvidia_smi_log>";File.Delete(dev+"/nvidia3");
            await Rejected("device node is missing","Missing NVIDIA node is reported without guessing another card");
            xml="not XML";await Rejected("invalid XML","Malformed NVIDIA output remains a visible mapping failure");
            exit=9;await Rejected("code 9","Driver query failures identify the failed query and exit code");
        }
        finally{Directory.Delete(root,true);}
    }
}
