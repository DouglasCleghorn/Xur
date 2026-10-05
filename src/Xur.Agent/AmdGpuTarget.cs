using System.Globalization;
using System.Text.RegularExpressions;
namespace Xur.Agent;

public static class AmdGpuTarget
{
    // KFD reports the compute target separately from the graphics IP version.
    // Match PCI domain and BDF; topology node numbers change across boots.
    public static string? Read(string pci,string sysRoot="/sys")
    {
        var match=Regex.Match(pci,@"\A([0-9a-fA-F]{4}):([0-9a-fA-F]{2}):([0-9a-fA-F]{2})\.([0-7])\z");
        if(!match.Success)return null;
        uint Hex(int group)=>uint.Parse(match.Groups[group].Value,NumberStyles.HexNumber,CultureInfo.InvariantCulture);
        var domain=Hex(1);var device=Hex(3);if(device>31)return null;
        var location=(Hex(2)<<8)|(device<<3)|Hex(4);
        var nodes=Path.Combine(sysRoot,"class/kfd/kfd/topology/nodes");
        if(!Directory.Exists(nodes))return null;
        foreach(var node in Directory.GetDirectories(nodes))
        {
            try
            {
                var values=new Dictionary<string,uint>(StringComparer.Ordinal);
                foreach(var line in File.ReadLines(Path.Combine(node,"properties")).Take(256))
                {
                    var parts=line.Split(' ',StringSplitOptions.RemoveEmptyEntries);
                    if(parts.Length==2&&uint.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out var value))values.TryAdd(parts[0],value);
                }
                if(values.GetValueOrDefault("vendor_id")!=0x1002 || !values.TryGetValue("domain",out var nodeDomain) || nodeDomain!=domain || !values.TryGetValue("location_id",out var nodeLocation) || nodeLocation!=location || !values.TryGetValue("gfx_target_version",out var target))continue;
                var major=target/10000;var minor=target/100%100;var revision=target%100;
                if(major is >=9 and <=12 && minor<16 && revision<16)return "gfx"+major.ToString(CultureInfo.InvariantCulture)+minor.ToString("x",CultureInfo.InvariantCulture)+revision.ToString("x",CultureInfo.InvariantCulture);
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException){}
        }
        return null;
    }
    public static bool NeedsNativeVllm(string[] gpus,string sysRoot="/sys")
    {
        var targets=gpus.Select(p=>Read(p,sysRoot)).ToArray();
        if(!targets.Contains("gfx1103"))return false;
        if(targets.Any(t=>t!="gfx1103"))throw new InvalidOperationException("The native gfx1103 vLLM image requires all selected GPUs to have that compute target.");
        return true;
    }
}
