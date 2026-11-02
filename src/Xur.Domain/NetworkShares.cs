namespace Xur.Domain;

public record SmbConnection(string Id,string Name,string Server,string Share,string Username,string Domain,int Uid,int Gid,bool ReadOnly=true,bool Encrypt=true);
public record SmbConnectionRequest(string Name,string Server,string Share,string Username,string Password,string Domain="",int Uid=1000,int Gid=1000,bool ReadOnly=true,bool Encrypt=true);
public record SmbHostedShare(string Id,string Name,string Path,string Username,int Uid,int Gid,bool ReadOnly,string FilesystemUuid);
public record SmbHostRequest(string Name,string Path,string Username,string Password,int Uid,int Gid,bool ReadOnly=true);
public record SmbServerSettings(string Address="",bool Enabled=false);
public record SmbAction(string Id,string Action);
public record SmbConnectionStatus(SmbConnection Connection,string Path,bool Mounted,string? Error);
public record NetworkSharesStatus(SmbServerSettings Server,SmbHostedShare[] Hosted,SmbConnectionStatus[] Connections,bool ServerRunning,bool RuntimeReady,bool Busy,string? Message);
public record NetworkShareMount(string ConnectionId,bool ReadOnly=true)
{
    public string Destination=>"/shared/"+ConnectionId;
}
public static class NetworkSharePolicy
{
    public static void Validate(Workload workload)
    {
        var mounts=workload.NetworkShares??[];
        if(mounts.Length==0)return;
        if(workload.Recipe.Kind is not ("Container" or "Model") || mounts.Length>16 || mounts.Select(m=>m.ConnectionId).Distinct().Count()!=mounts.Length)
            throw new InvalidOperationException("Assign at most 16 distinct network folders to container or model workloads.");
        foreach(var mount in mounts)
        {
            if(!System.Text.RegularExpressions.Regex.IsMatch(mount.ConnectionId??"",@"\Asmb-[a-f0-9]{32}\z"))throw new InvalidOperationException("Invalid network connection.");
            if((workload.Recipe.Container?.Mounts??[]).Any(m=>m.Destination=="/shared" || m.Destination==mount.Destination || m.Destination.StartsWith(mount.Destination+"/") || mount.Destination.StartsWith(m.Destination.TrimEnd('/')+"/")))
                throw new InvalidOperationException("A container volume overlaps an assigned network folder.");
        }
    }
}
