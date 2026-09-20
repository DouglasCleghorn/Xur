using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

// Workstation sessions share the host network. Decline its network-control
// request without opening a root authentication dialog in a streamed session.
// Other polkit actions retain their normal policy.
public sealed class StationNetworkPolicy(string directory="/etc/polkit-1/rules.d")
{
    string PathFor(string id)
    {
        if(!ProfilePolicy.EntityIdentifier(id))throw new InvalidOperationException("Invalid workstation ID.");
        return Path.Combine(directory,"00-xur-workstation-network-"+id+".rules");
    }
    public static string Rule(string user)
    {
        if(!ProfilePolicy.UserName(user))throw new InvalidOperationException("Invalid workstation user.");
        return "polkit.addRule(function(action, subject) {\n"+
            "    if (subject.user === "+JsonSerializer.Serialize(user)+" &&\n"+
            "        action.id === \"org.freedesktop.NetworkManager.network-control\")\n"+
            "        return polkit.Result.NO;\n"+
            "});\n";
    }
    public void Apply(string id,string user)
    {
        var path=PathFor(id);var rule=Rule(user);Directory.CreateDirectory(directory);
        File.WriteAllText(path+".tmp",rule);
        File.SetUnixFileMode(path+".tmp",UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.GroupRead|UnixFileMode.OtherRead);
        File.Move(path+".tmp",path,true);
    }
    public void Remove(string id)=>File.Delete(PathFor(id));
}
