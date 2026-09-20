using System.Text.Json;
namespace Xur.Agent;
public static class ConfigExport
{
    // Explicit allowlist: never archive /var/lib/xur recursively (it contains live credentials).
    public static Dictionary<string,JsonElement> Read(string directory,string configDirectory="/etc/xur")
    {
        var files=new Dictionary<string,JsonElement>();
        foreach(var relative in new[]{"gpu-power.json","updates/settings.json"})Add(relative);
        var catalog=Path.Combine(directory,"catalog-selected");
        if(Directory.Exists(catalog))foreach(var file in Directory.EnumerateFiles(catalog,"*.json",SearchOption.TopDirectoryOnly))Add("catalog-selected/"+Path.GetFileName(file));
        Add("application-updates.json",configDirectory);
        return files;
        void Add(string relative,string? parent=null)
        {
            var path=Path.Combine(parent??directory,relative);if(!File.Exists(path))return;
            var info=new FileInfo(path);if(info.LinkTarget!=null||info.Length>4*1024*1024)throw new InvalidOperationException("Configuration file cannot be exported: "+relative);
            using var doc=JsonDocument.Parse(File.ReadAllText(path));files[relative]=doc.RootElement.Clone();
        }
    }
}
