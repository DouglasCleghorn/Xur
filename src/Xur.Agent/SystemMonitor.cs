using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xur.Domain;
namespace Xur.Agent;

public sealed class SystemMonitor
{
    readonly SemaphoreSlim gate=new(1,1);
    long previousTotal,previousIdle;
    public static bool Managed(string name) => Regex.IsMatch(name,@"^xur-(workload|station)-[a-zA-Z0-9_.@-]+\.service$");
    public async Task<SystemSnapshot> Observe()
    {
        await gate.WaitAsync();
        try
        {
            var cpu=File.ReadLines("/proc/stat").First().Split(' ',StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).Select(long.Parse).ToArray();
            long total=cpu.Sum(),idle=cpu[3]+cpu[4];
            double usage=previousTotal==0 || total==previousTotal ? 0 : 100d*(total-previousTotal-(idle-previousIdle))/(total-previousTotal);
            previousTotal=total;previousIdle=idle;
            var memory=File.ReadLines("/proc/meminfo").Select(l=>l.Split(' ',StringSplitOptions.RemoveEmptyEntries)).ToDictionary(x=>x[0].TrimEnd(':'),x=>long.Parse(x[1])*1024);
            var disk=new DriveInfo("/var");
            var units=await Processes.Run("systemctl",["list-units","--all","--type=service","--output=json","--no-pager"],10);
            if(units.ExitCode!=0)throw new IOException("Service observation failed");
            using var doc=JsonDocument.Parse(units.Output);
            var services=doc.RootElement.EnumerateArray().Select(u=>new ServiceState(u.GetProperty("unit").GetString()!,u.GetProperty("description").GetString() ?? "",u.GetProperty("active").GetString() ?? "unknown",Managed(u.GetProperty("unit").GetString()!))).OrderBy(s=>s.Name).ToArray();
            return new(Math.Clamp(usage,0,100),memory["MemTotal"]-memory["MemAvailable"],memory["MemTotal"],disk.TotalSize-disk.TotalFreeSpace,disk.TotalSize,
                double.Parse(File.ReadAllText("/proc/uptime").Split(' ')[0],CultureInfo.InvariantCulture),services);
        }
        finally { gate.Release(); }
    }
}
