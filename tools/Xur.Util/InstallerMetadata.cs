using System.Text;
using System.Text.Json.Nodes;

namespace Xur.Util;

public static class InstallerMetadata
{
    public static string BundleId(string metadata)
    {
        var id=JsonValues.Text(DurableFiles.ReadObject(metadata)["id"]);
        if(id is null||id.Length!=64||id.Any(c=>c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))throw new UserError("Invalid application bundle identity.");
        return id;
    }
    public static void Save(string root,string channelFile,string? release,DurableFiles files)
    {
        if(!Path.IsPathFullyQualified(root)||!Directory.Exists(Path.Combine(root,"var")))throw new UserError("Installed root required.");
        var channel=File.Exists(channelFile)?File.ReadAllText(channelFile).Trim():"stable";
        if(channel is not ("stable" or "nightly"))throw new UserError("Invalid installer channel.");
        long? sequence=null;
        if(release is not null)
        {
            sequence=JsonValues.Integer(DurableFiles.ReadObject(release)["sequence"]);
            if(sequence is null or <0)throw new UserError("Invalid release sequence.");
        }
        files.Write(Path.Combine(root,"etc/xur/application-updates.json"),Encoding.UTF8.GetBytes(new JsonObject{["channel"]=channel,["development"]=false}.ToJsonString()+"\n"),0x1a4);
        if(sequence is not null)files.WriteJson(Path.Combine(root,"var/lib/xur/app/highest-sequence.json"),JsonValue.Create(sequence.Value));
    }
}
