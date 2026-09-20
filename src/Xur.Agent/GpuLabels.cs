using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

// Human labels are separate from PCI addresses used for actual device access.
public sealed class GpuLabels(string directory="/var/lib/xur")
{
    static readonly object gate=new();
    public sealed record Entry(int Number,string? HardwareId,string Pci,string Vendor);
    public GpuDevice[] Assign(GpuDevice[] devices)
    {
        if(devices.Length==0)return devices;
        lock(gate) {
            var path=Path.Combine(directory,"gpu-labels.json");
            var entries=File.Exists(path)?JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path))??throw new IOException("GPU label registry is invalid."):[];
            if(entries.Any(e=>e.Number<1)||entries.Select(e=>e.Number).Distinct().Count()!=entries.Count)throw new IOException("GPU label registry is invalid.");
            var before=JsonSerializer.Serialize(entries);var used=new HashSet<int>();var result=new List<GpuDevice>();
            // UUID-bearing cards first, so a moved card keeps its label before
            // an adapter without a hardware identity claims a PCI fallback.
            foreach(var gpu in devices.OrderByDescending(g=>g.RuntimeId.StartsWith("GPU-",StringComparison.Ordinal)).ThenBy(g=>g.Pci,StringComparer.Ordinal)) {
                var hardware=gpu.RuntimeId.StartsWith("GPU-",StringComparison.Ordinal)?gpu.RuntimeId:null;
                var entry=hardware==null?null:entries.FirstOrDefault(e=>e.HardwareId==hardware&&!used.Contains(e.Number));
                entry??=entries.FirstOrDefault(e=>e.Pci==gpu.Pci&&e.Vendor==gpu.Vendor&&!used.Contains(e.Number)&&(hardware==null||e.HardwareId==null));
                if(entry==null){entry=new(entries.Count==0?1:entries.Max(e=>e.Number)+1,hardware,gpu.Pci,gpu.Vendor);entries.Add(entry);}
                else {var updated=entry with{Pci=gpu.Pci,HardwareId=hardware??entry.HardwareId};entries[entries.IndexOf(entry)]=updated;entry=updated;}
                used.Add(entry.Number);result.Add(gpu with{ShortId="GPU "+entry.Number});
            }
            var after=JsonSerializer.Serialize(entries);
            if(before!=after) {
                Directory.CreateDirectory(directory);var temp=path+".tmp";
                using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write)){JsonSerializer.Serialize(stream,entries);stream.Flush(true);}
                File.Move(temp,path,true);
            }
            return result.OrderBy(g=>g.Pci,StringComparer.Ordinal).ToArray();
        }
    }
}
