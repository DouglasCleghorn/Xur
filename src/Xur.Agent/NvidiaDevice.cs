using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Xur.Domain;

namespace Xur.Agent;

public static class NvidiaDevice
{
    public static async Task<string[]> WorkstationNodes(GpuDevice gpu,string procRoot="/proc",string devRoot="/dev",
        Func<string,string[],int,Task<ProcessResult>>? run=null)
    {
        run??=(exe,args,timeout)=>Processes.Run(exe,args,timeout);
        var modules=await run("modprobe",["-a","nvidia_modeset","nvidia_uvm"],20);
        if(modules.ExitCode!=0)throw new InvalidOperationException("Could not load NVIDIA workstation modules: "+Redaction.Logs(modules.Output));
        // Loading a module does not guarantee its character devices exist.
        // Create them before systemd resolves the workstation's device allowlist.
        async Task CreateNodes(string[] args)
        {
            ProcessResult result;
            try { result=await run("nvidia-modprobe",args,20); }
            catch(System.ComponentModel.Win32Exception e)
            { throw new InvalidOperationException("Could not run nvidia-modprobe to initialize workstation devices. Check the host NVIDIA driver tools installation.",e); }
            if(result.ExitCode!=0)throw new InvalidOperationException("Could not initialize NVIDIA workstation devices with nvidia-modprobe "+string.Join(' ',args)+" (exit "+result.ExitCode+"): "+Redaction.Logs(result.Output));
        }
        if(!File.Exists(Path.Combine(devRoot,"nvidia-modeset")))await CreateNodes(["--modeset"]);
        // In unified-memory mode, minor 0 is the UVM base, not GPU index 0.
        if(!File.Exists(Path.Combine(devRoot,"nvidia-uvm"))||!File.Exists(Path.Combine(devRoot,"nvidia-uvm-tools")))
            await CreateNodes(["--unified-memory","--create-nvidia-device-file=0"]);
        var settled=await run("udevadm",["settle","--timeout=10"],15);
        if(settled.ExitCode!=0)throw new InvalidOperationException("NVIDIA device setup has not settled. Retry loading the workstation.");
        var shared=new[]{"nvidiactl","nvidia-modeset","nvidia-uvm"}.Select(n=>Path.Combine(devRoot,n)).ToArray();
        foreach(var node in shared)if(!File.Exists(node))throw new InvalidOperationException("Required NVIDIA workstation device is missing: "+node);
        var selected=await Resolve(gpu,procRoot,devRoot,run);
        return new[]{selected}.Concat(shared).Concat(new[]{Path.Combine(devRoot,"nvidia-uvm-tools")}.Where(File.Exists)).ToArray();
    }
    // GPU indices are not device minors. Resolve from current driver observations
    // on each call, for both ownership checks and workstation device permissions.
    public static async Task<string> Resolve(GpuDevice gpu,string procRoot="/proc",string devRoot="/dev",
        Func<string,string[],int,Task<ProcessResult>>? run=null)
    {
        var pci=Pci(gpu.Pci) ?? throw new InvalidOperationException("Invalid NVIDIA PCI identity.");
        var information=Path.Combine(procRoot,"driver/nvidia/gpus",pci,"information");
        string? contents=null;
        try { contents=await File.ReadAllTextAsync(information); }
        catch(IOException){}catch(UnauthorizedAccessException){}
        int? minor=null;
        if(contents!=null)
        {
            string? Field(string name)
            {
                var values=contents.Split('\n').Select(l=>l.Split(':',2,StringSplitOptions.TrimEntries))
                    .Where(p=>p.Length==2&&p[0]==name).Select(p=>p[1]).ToArray();
                if(values.Length>1)throw Failure(pci,"Driver information contains duplicate "+name+" fields.");
                return values.SingleOrDefault();
            }
            if(Field("Bus Location") is { } bus && Pci(bus)!=pci)throw Failure(pci,"Driver PCI identity changed.");
            if(Field("GPU UUID") is { } uuid)CheckUuid(gpu,uuid,pci);
            minor=Minor(Field("Device Minor"));
        }
        if(minor==null)
        {
            run??=(exe,args,timeout)=>Processes.Run(exe,args,timeout);
            var result=await run("nvidia-smi",["--id="+pci,"--query","--xml-format"],10);
            if(result.ExitCode!=0)throw Failure(pci,$"Driver information did not provide a device minor; nvidia-smi XML query exited with code {result.ExitCode}.");
            try
            {
                using var reader=XmlReader.Create(new StringReader(result.Output),new XmlReaderSettings{DtdProcessing=DtdProcessing.Ignore,XmlResolver=null});
                var document=XDocument.Load(reader);
                var matches=document.Root?.Elements("gpu").Where(g=>Pci(g.Element("pci")?.Element("pci_bus_id")?.Value??g.Attribute("id")?.Value)==pci).ToArray()??[];
                if(matches.Length!=1)throw Failure(pci,"NVIDIA XML did not identify exactly one matching GPU.");
                var match=matches[0];
                if(match.Element("uuid") is { } uuid)CheckUuid(gpu,uuid.Value,pci);
                minor=Minor(match.Element("minor_number")?.Value);
                if(minor==null)throw Failure(pci,"NVIDIA XML did not report a valid device minor.");
            }
            catch(XmlException){throw Failure(pci,"nvidia-smi returned invalid XML.");}
        }
        var node=Path.Combine(devRoot,"nvidia"+minor.Value.ToString(CultureInfo.InvariantCulture));
        if(!File.Exists(node))throw Failure(pci,$"The driver maps this GPU to {node}, but that device node is missing.");
        return node;
    }
    static void CheckUuid(GpuDevice gpu,string uuid,string pci)
    {
        if(gpu.RuntimeId.StartsWith("GPU-",StringComparison.Ordinal)&&!string.Equals(gpu.RuntimeId,uuid.Trim(),StringComparison.OrdinalIgnoreCase))
            throw Failure(pci,"Driver GPU UUID changed. Refresh hardware inventory before retrying.");
    }
    static int? Minor(string? value)=>int.TryParse(value?.Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out var n)&&n is >=0 and <=1048575?n:null;
    static string? Pci(string? value)
    {
        value=value?.Trim();
        if(value==null||!Regex.IsMatch(value,@"^(?:0000)?[0-9a-fA-F]{4}:[0-9a-fA-F]{2}:[0-9a-fA-F]{2}\.[0-7]$"))return null;
        return value[^12..].ToLowerInvariant();
    }
    static InvalidOperationException Failure(string pci,string reason)=>new($"Could not resolve the NVIDIA device for {pci}. {reason}");
}
