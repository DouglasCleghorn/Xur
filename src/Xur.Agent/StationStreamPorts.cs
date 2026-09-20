using Xur.Domain;
namespace Xur.Agent;

// Each reservation owns all Sunshine TCP/UDP offsets. Keep it after a stop so
// paired clients continue to use the same address after a reboot or update.
public sealed class StationStreamPorts(string root="/var/lib/xur-streaming/ports")
{
    static readonly object gate=new();
    public static bool Valid(int port)=>port>=47989&&port<=51089&&(port-47989)%100==0;
    public int Get(string id,bool reserve=true)
    {
        if(!ProfilePolicy.EntityIdentifier(id))throw new InvalidOperationException("Invalid workstation.");
        lock(gate)
        {
            var path=Path.Combine(root,id);
            // Reserve legacy configurations before allocating a new station.
            // Otherwise a new station could take the still-running old port.
            var parent=Path.GetDirectoryName(root)!;
            if(reserve&&Directory.Exists(parent))foreach(var folder in Directory.GetDirectories(parent))
            {
                var legacyId=Path.GetFileName(folder);var config=Path.Combine(folder,"sunshine.conf");
                if(!ProfilePolicy.EntityIdentifier(legacyId)||!File.Exists(config))continue;
                var line=File.ReadLines(config).FirstOrDefault(l=>l.TrimStart().StartsWith("port =",StringComparison.Ordinal));
                if(line==null||!int.TryParse(line.Split('=',2)[1].Trim(),out var legacyPort)||!Valid(legacyPort))throw new InvalidOperationException("Invalid saved workstation streaming port.");
                var legacyPath=Path.Combine(root,legacyId);
                if(File.Exists(legacyPath))continue;
                Directory.CreateDirectory(root);
                using(var f=new FileStream(legacyPath+".tmp",FileMode.Create,FileAccess.Write)){f.Write(System.Text.Encoding.ASCII.GetBytes(legacyPort.ToString(System.Globalization.CultureInfo.InvariantCulture)));f.Flush(true);}
                File.Move(legacyPath+".tmp",legacyPath);
            }
            var used=new Dictionary<int,string>();
            if(Directory.Exists(root))foreach(var file in Directory.GetFiles(root).Where(p=>!p.EndsWith(".tmp",StringComparison.Ordinal)))
            {
                if(!int.TryParse(File.ReadAllText(file),out var number)||!Valid(number)||!used.TryAdd(number,Path.GetFileName(file)))
                    throw new InvalidOperationException("Workstation streaming port reservations conflict. Check Diagnostics before starting streaming.");
            }
            if(File.Exists(path))return int.Parse(File.ReadAllText(path));
            if(!reserve)return 47989; // Existing single-station configuration.
            var port=Enumerable.Range(0,32).Select(n=>47989+n*100).FirstOrDefault(p=>!used.ContainsKey(p));
            if(port==0)throw new InvalidOperationException("All workstation streaming ports are reserved.");
            Directory.CreateDirectory(root);
            using(var f=new FileStream(path+".tmp",FileMode.Create,FileAccess.Write)){f.Write(System.Text.Encoding.ASCII.GetBytes(port.ToString(System.Globalization.CultureInfo.InvariantCulture)));f.Flush(true);}
            File.Move(path+".tmp",path);
            return port;
        }
    }
    public static string[] FirewallPorts(int port)
    {
        if(!Valid(port))throw new InvalidOperationException("Invalid workstation streaming port.");
        return [$"{port-5}/tcp",$"{port}/tcp",$"{port+21}/tcp",$"{port+9}-{port+11}/udp"];
    }
}
