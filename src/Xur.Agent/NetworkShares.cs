using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xur.Domain;
namespace Xur.Agent;

// Configuration and secrets are separate. All mutations are serialized with workload
// starts by WorkloadRuntime; server lifetime is independent of the selected profile.
public sealed class NetworkShares(string stateDirectory,string systemDirectory="/etc/systemd/system",string mountRoot="/var/mnt/xur-smb",
    Func<string,string[],int,Task<ProcessResult>>? runner=null,Func<string,string>? hostedPathValidator=null)
{
    const string Image="localhost/xur-smb:latest";
    const string Service="xur-smb.service";
    string ServerRoot=>Path.Combine(root,"server");
    readonly string root=Path.Combine(stateDirectory,"network-shares");
    readonly Func<string,string[],int,Task<ProcessResult>> run=runner??((exe,args,seconds)=>Processes.Run(exe,args,seconds));
    readonly SemaphoreSlim gate=new(1,1);
    Task? build;string? message;
    record Configuration(SmbServerSettings Server,SmbHostedShare[] Hosted,SmbConnection[] Connections);
    Configuration Read()=>File.Exists(Path.Combine(root,"settings.json"))?JsonSerializer.Deserialize<Configuration>(File.ReadAllText(Path.Combine(root,"settings.json")))??throw new IOException("Invalid network share configuration."):new(new(),[],[]);
    void Save(Configuration config)=>Write(Path.Combine(root,"settings.json"),JsonSerializer.Serialize(config));
    public static void Write(string path,string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using(var stream=new FileStream(path+".tmp",new FileStreamOptions{Mode=FileMode.Create,Access=FileAccess.Write,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite}))
        {var bytes=Encoding.UTF8.GetBytes(value);stream.Write(bytes);stream.Flush(true);}
        File.SetUnixFileMode(path+".tmp",UnixFileMode.UserRead|UnixFileMode.UserWrite);
        File.Move(path+".tmp",path,true);
    }
    void PrivateRoot()
    {
        Directory.CreateDirectory(root);File.SetUnixFileMode(root,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
    }
    async Task Require(string exe,string[] args,int seconds=30)
    {
        if((await run(exe,args,seconds)).ExitCode!=0)throw new InvalidOperationException($"SMB operation failed ({Path.GetFileName(exe)}). Check the server address, permissions and system journal; no credentials are included here.");
    }
    static string Id()=>"smb-"+Guid.NewGuid().ToString("N");
    static void Text(string value,int max,string field)
    {
        if(string.IsNullOrWhiteSpace(value)||value.Length>max||value.Any(char.IsControl))throw new InvalidOperationException("Invalid "+field+".");
    }
    static void Account(string user,int uid,int gid)
    {
        if(!ProfilePolicy.UserName(user)||uid is <1000 or >=65534||gid is <1000 or >=65534)throw new InvalidOperationException("Use a non-system account and UID/GID between 1000 and 65533.");
    }
    static void Password(string password){Text(password,1024,"password");}
    static string Quote(string text)=>"\""+text.Replace("\\","\\\\").Replace("\"","\\\"").Replace("%","%%").Replace("$","$$")+"\"";
    static string Shell(string text)=>"'"+text.Replace("'","'\"'\"'")+"'";
    string Credentials(string id)=>Path.Combine(root,id+".credentials");
    public string MountPath(string id)=>Path.Combine(mountRoot,id);
    public void CleanInterruptedPassword()
    {
        File.Delete(Path.Combine(ServerRoot,"password"));File.Delete(Path.Combine(ServerRoot,"password.tmp"));File.Delete(Path.Combine(ServerRoot,"account.sh"));
    }
    public static string[] WorkloadUnitProperties(string name,string[] units)=>units.Length==0?[]:
        ["--property=Requires="+string.Join(' ',units),"--property=After="+string.Join(' ',units),"--property=BindsTo="+string.Join(' ',units),
         "--property=ExecStop=/usr/bin/podman stop --time 10 "+name];
    public async Task<string> Unit(string id)
    {
        var r=await run("systemd-escape",["--path","--suffix=mount",MountPath(id)],10);
        if(r.ExitCode!=0 || !Regex.IsMatch(r.Output.Trim(),@"\A[a-zA-Z0-9\\_.-]+\.mount\z"))throw new InvalidOperationException("Could not name the SMB mount unit.");
        return r.Output.Trim();
    }
    async Task<bool> Mounted(SmbConnection connection)
    {
        var r=await run("findmnt",["--json","--mountpoint",MountPath(connection.Id),"--output","SOURCE,FSTYPE"],10);
        if(r.ExitCode!=0)return false;
        using var doc=JsonDocument.Parse(r.StandardOutput);
        return doc.RootElement.GetProperty("filesystems").EnumerateArray().Any(f=>f.GetProperty("fstype").GetString()=="cifs" && f.GetProperty("source").GetString()=="//"+connection.Server+"/"+connection.Share);
    }
    public async Task<NetworkSharesStatus> Status()
    {
        await gate.WaitAsync();try
        {
            var c=Read();var connections=new List<SmbConnectionStatus>();
            foreach(var connection in c.Connections)
            {
                try{var mounted=await Mounted(connection);connections.Add(new(connection,MountPath(connection.Id),mounted,mounted?null:"Not mounted. Connect or start an assigned workload."));}
                catch{connections.Add(new(connection,MountPath(connection.Id),false,"Mount status unavailable."));}
            }
            return new(c.Server,c.Hosted,connections.ToArray(),(await run("systemctl",["is-active",Service],5)).ExitCode==0,
                (await run("podman",["image","exists",Image],10)).ExitCode==0,build is {IsCompleted:false},message);
        }finally{gate.Release();}
    }
    // Resolve current Fedora packages on explicit install/update, retaining the previous
    // image on build failure. No build or internet access is needed on ordinary startup.
    public async Task InstallRuntime()
    {
        await gate.WaitAsync();try
        {
            if(build is {IsCompleted:false})throw new InvalidOperationException("SMB runtime installation is already running.");
            PrivateRoot();var context=Path.Combine(root,"runtime");Directory.CreateDirectory(context);
            Write(Path.Combine(context,"Containerfile"),"FROM quay.io/fedora/fedora:latest\nRUN dnf -y install samba samba-client samba-common-tools shadow-utils && dnf clean all\n");
            message="Installing the current Fedora Samba packages…";
            build=Task.Run(async()=>{
                try{await Require("podman",["build","--pull=always","--no-cache","--tag",Image,context],1800);message="SMB runtime ready. Restart hosting to use an updated runtime.";}
                catch(Exception e){message=e.Message;}
            });
        }finally{gate.Release();}
    }
    public async Task<SmbConnection> AddConnection(SmbConnectionRequest request)
    {
        await gate.WaitAsync();try
        {
            PrivateRoot();Text(request.Name,80,"connection name");Text(request.Username,128,"username");Password(request.Password);
            if(!Regex.IsMatch(request.Server??"",@"\A[a-zA-Z0-9][a-zA-Z0-9.-]{0,252}\z") || !Regex.IsMatch(request.Share??"",@"\A[a-zA-Z0-9_][a-zA-Z0-9_. -]{0,79}\z") || request.Share is "." or ".." || request.Domain==null || request.Domain.Length>128 || request.Domain.Any(char.IsControl) || request.Uid is <1000 or >=65534 || request.Gid is <1000 or >=65534)
                throw new InvalidOperationException("Use a DNS name or IPv4 server, share name, and non-system UID/GID.");
            var c=Read();if(c.Connections.Length>=32)throw new InvalidOperationException("At most 32 connections are supported.");
            if((await run("sh",["-c","command -v mount.cifs >/dev/null"],10)).ExitCode!=0)
                throw new InvalidOperationException("This host needs cifs-utils (mount.cifs) in its OS image to connect SMB storage.");
            if((await run("podman",["image","exists",Image],10)).ExitCode!=0)throw new InvalidOperationException("Install the SMB runtime first; it verifies remote access before workload starts.");
            var connection=new SmbConnection(Id(),request.Name,request.Server!,request.Share!,request.Username,request.Domain,request.Uid,request.Gid,request.ReadOnly,request.Encrypt);
            Write(Credentials(connection.Id),$"username={request.Username}\npassword={request.Password}\ndomain={request.Domain}\n");
            // Save intent before activating units; a failed connection remains manageable.
            Save(c with{Connections=[..c.Connections,connection]});
            await ConfigureMount(connection);
            return connection;
        }finally{gate.Release();}
    }
    async Task ConfigureMount(SmbConnection c)
    {
        Directory.CreateDirectory(mountRoot);
        File.SetUnixFileMode(mountRoot,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute|UnixFileMode.GroupRead|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherExecute);
        if(!Directory.Exists(MountPath(c.Id)))Directory.CreateDirectory(MountPath(c.Id),UnixFileMode.None);
        var unit=await Unit(c.Id);
        var options=$"credentials={Credentials(c.Id)},vers=3,uid={c.Uid},gid={c.Gid},forceuid,forcegid,file_mode=0660,dir_mode=0770,nosuid,nodev,noexec"+(c.ReadOnly?",ro":",rw")+(c.Encrypt?",seal":"")+",context=system_u:object_r:container_file_t:s0";
        // systemd values are not shell arguments. Validated remote names and generated paths
        // cannot introduce new directives; percent specifiers must be escaped.
        Write(Path.Combine(systemDirectory,unit),$"[Unit]\nDescription=Xur SMB connection\nWants=network-online.target\nAfter=network-online.target\n[Mount]\nWhat=//{c.Server}/{c.Share}\nWhere={MountPath(c.Id)}\nType=cifs\nOptions={options}\nTimeoutSec=30\n");
        Write(Path.Combine(systemDirectory,unit.Replace(".mount",".automount")),$"[Unit]\nDescription=Xur SMB on-demand connection\n[Automount]\nWhere={MountPath(c.Id)}\nDirectoryMode=0000\n[Install]\nWantedBy=multi-user.target\n");
        await Require("systemctl",["daemon-reload"]);
        await Require("systemctl",["enable","--now",unit.Replace(".mount",".automount")]);
    }
    public async Task ConnectionAction(SmbAction request,Workload[] active)
    {
        await gate.WaitAsync();try
        {
            var config=Read();var c=config.Connections.SingleOrDefault(c=>c.Id==request.Id)??throw new InvalidOperationException("Connection no longer exists.");
            if(request.Action is "disconnect" or "remove" && active.Any(w=>(w.NetworkShares??[]).Any(m=>m.ConnectionId==c.Id)))throw new InvalidOperationException("Unload every workload using this connection first, including pending workloads.");
            var unit=await Unit(c.Id);
            switch(request.Action)
            {
                case "connect": await ConfigureMount(c);await Require("systemctl",["start",unit],45);await Check(c);break;
                case "test": await Require("systemctl",["start",unit],45);await Check(c);break;
                case "disconnect": case "remove":
                    // Stop both in one transaction: the automount must not remount during unmount.
                    await Require("systemctl",["stop",unit.Replace(".mount",".automount"),unit],45);
                    await Require("systemctl",["disable",unit.Replace(".mount",".automount")]);
                    if(request.Action=="remove")
                    {
                        File.Delete(Path.Combine(systemDirectory,unit));File.Delete(Path.Combine(systemDirectory,unit.Replace(".mount",".automount")));
                        await Require("systemctl",["daemon-reload"]);Save(config with{Connections=config.Connections.Where(x=>x.Id!=c.Id).ToArray()});File.Delete(Credentials(c.Id));
                    }
                    break;
                default:throw new InvalidOperationException("Unknown connection action.");
            }
        }finally{gate.Release();}
    }
    async Task Check(SmbConnection c)
    {
        // A mounted CIFS filesystem may be offline with cached metadata. A fresh SMB
        // session verifies authentication/tree access before admitting a workload.
        await Require("podman",["run","--rm","--network=host","--security-opt=label=disable","--pull=never",
            "--volume",Credentials(c.Id)+":/run/smb-auth:ro",Image,"smbclient","//"+c.Server+"/"+c.Share,
            "--authentication-file=/run/smb-auth","--option=client min protocol=SMB3_00",
            "--option=client smb encrypt="+(c.Encrypt?"required":"if_required"),"--timeout=8","--command=quit"],20);
        if(!await Mounted(c))throw new InvalidOperationException("The expected SMB filesystem is not mounted. Workload startup is blocked.");
        await Require("stat",["--file-system","--",MountPath(c.Id)],10);
    }
    public async Task<(string[] Arguments,string[] Units)> RequireMounts(Workload w)
    {
        NetworkSharePolicy.Validate(w);if(w.NetworkShares is not {Length:>0})return([],[]);
        await gate.WaitAsync();try
        {
            var config=Read();var args=new List<string>();var units=new List<string>();var groups=new HashSet<int>();
            foreach(var mount in w.NetworkShares??[])
            {
                var c=config.Connections.SingleOrDefault(c=>c.Id==mount.ConnectionId)??throw new InvalidOperationException("An assigned network connection has been removed.");
                if(c.ReadOnly&&!mount.ReadOnly)throw new InvalidOperationException("A workload requests write access to a read-only connection.");
                var unit=await Unit(c.Id);await Require("systemctl",["start",unit],45);await Check(c);
                if(groups.Add(c.Gid))args.AddRange(["--group-add",c.Gid.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                // Private bind propagation retains the mounted filesystem, never the empty
                // underlying directory, if the host mount is subsequently detached.
                args.AddRange(["--mount",$"type=bind,source={MountPath(c.Id)},destination={mount.Destination},ro={mount.ReadOnly.ToString().ToLowerInvariant()},bind-propagation=rprivate,bind-nonrecursive"]);units.Add(unit);
            }
            return(args.ToArray(),units.ToArray());
        }finally{gate.Release();}
    }
    public static string ValidateHostedPath(string path)
    {
        if(path==null || !Regex.IsMatch(path,@"\A/(var/home|var/mnt|var/srv|mnt|srv)/[a-zA-Z0-9_./ -]+\z") || path.Split('/').Any(p=>p is ".." or ".") || path.EndsWith('/') || path.Contains("//"))throw new InvalidOperationException("Choose a local data folder beneath /var/home, /var/mnt, /var/srv, /mnt or /srv.");
        var current="/";foreach(var part in path.Split('/',StringSplitOptions.RemoveEmptyEntries))
        {current=Path.Combine(current,part);if(new DirectoryInfo(current).LinkTarget!=null)throw new InvalidOperationException("Hosted folders must not contain symlink path components.");}
        if(!Directory.Exists(path))throw new InvalidOperationException("The hosted folder does not exist.");
        return path;
    }
    string HostedPath(string path)=>(hostedPathValidator??ValidateHostedPath)(path);
    async Task<string> Filesystem(string path)
    {
        var r=await run("findmnt",["--json","--target",path,"--output","FSTYPE,UUID"],10);
        if(r.ExitCode!=0)throw new InvalidOperationException("Could not verify the hosted folder's filesystem.");
        using var doc=JsonDocument.Parse(r.StandardOutput);var fs=doc.RootElement.GetProperty("filesystems")[0];
        var uuid=fs.GetProperty("uuid").GetString();
        if(fs.GetProperty("fstype").GetString() is not ("ext4" or "xfs" or "btrfs") || string.IsNullOrEmpty(uuid) || !Regex.IsMatch(uuid,@"\A[a-zA-Z0-9-]+\z"))throw new InvalidOperationException("Host shares from an existing local ext4, XFS or Btrfs filesystem.");
        return uuid;
    }
    public static string[] AllowedClients(string[]? entries)
    {
        if(entries is null)return [];
        if(entries.Length>64)throw new InvalidOperationException("Use at most 64 client addresses or subnets per share.");
        return entries.Select(entry=>
        {
            if(entry==null || !Regex.IsMatch(entry,@"\A(?:0|[1-9][0-9]{0,2})(?:\.(?:0|[1-9][0-9]{0,2})){3}(?:/(?:[1-9]|[12][0-9]|3[0-2]))?\z"))
                throw new InvalidOperationException("Allowed systems must be IPv4 addresses or CIDR subnets with prefixes 1–32. Hostnames, wildcards and /0 are not supported.");
            var parts=entry.Split('/');
            if(!IPAddress.TryParse(parts[0],out var address)||address.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork||(address.Equals(IPAddress.Any)&&parts.Length==1)||address.Equals(IPAddress.Broadcast))
                throw new InvalidOperationException("Invalid allowed client address.");
            var prefix=parts.Length==1?32:int.Parse(parts[1]);var bytes=address.GetAddressBytes();
            uint number=((uint)bytes[0]<<24)|((uint)bytes[1]<<16)|((uint)bytes[2]<<8)|bytes[3];
            number &= uint.MaxValue<<(32-prefix);
            var network=new IPAddress(new[]{(byte)(number>>24),(byte)(number>>16),(byte)(number>>8),(byte)number}).ToString();
            return prefix==32?network:network+"/"+prefix;
        }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
    public async Task SetAccess(SmbShareAccess request)
    {
        var clients=AllowedClients(request.AllowedClients);
        await gate.WaitAsync();try
        {
            var config=Read();if(config.Server.Enabled)throw new InvalidOperationException("Stop hosting before changing allowed systems. Stopping disconnects sessions using the previous permissions.");
            if(!config.Hosted.Any(s=>s.Id==request.Id))throw new InvalidOperationException("Hosted share no longer exists.");
            Save(config with{Hosted=config.Hosted.Select(s=>s.Id==request.Id?s with{AllowedClients=clients}:s).ToArray()});
        }finally{gate.Release();}
    }
    static string ClientAccess(SmbHostedShare share)
    {
        var clients=AllowedClients(share.AllowedClients);
        // Explicit loopback denial suppresses Samba's localhost exception. An
        // explicit allow match takes precedence, including an intentionally allowed localhost.
        return "hosts deny = ALL 127.0.0.1 ::1\n"+(clients.Length==0?"":"hosts allow = "+string.Join(' ',clients)+"\n");
    }
    public async Task<SmbHostedShare> AddHosted(SmbHostRequest r)
    {
        await gate.WaitAsync();try
        {
            PrivateRoot();Account(r.Username,r.Uid,r.Gid);Password(r.Password);var clients=AllowedClients(r.AllowedClients);
            if(r.Name==null || !Regex.IsMatch(r.Name,@"\A[a-zA-Z0-9][a-zA-Z0-9_-]{0,47}\z") || new[]{"global","homes","printers","ipc$"}.Contains(r.Name.ToLowerInvariant()))throw new InvalidOperationException("Use a simple, unique share name.");
            var config=Read();if(config.Server.Enabled)throw new InvalidOperationException("Stop hosting before changing shares.");
            if(config.Hosted.Length>=32 || config.Hosted.Any(s=>s.Name.Equals(r.Name,StringComparison.OrdinalIgnoreCase)||s.Username==r.Username))throw new InvalidOperationException("Use a unique share name and SMB username (one account per share).");
            var path=HostedPath(r.Path);var uuid=await Filesystem(path);
            var share=new SmbHostedShare(Id(),r.Name,path,r.Username,r.Uid,r.Gid,r.ReadOnly,uuid,clients);
            var secret=Path.Combine(ServerRoot,"password");Write(secret,r.Password+"\n"+r.Password+"\n");
            try
            {
                // Account creation is isolated from host login accounts. Only Samba's password
                // database is retained, never the plaintext hosting password.
                Write(Path.Combine(ServerRoot,"account.sh"),$"set -eu\ngetent group {r.Gid} >/dev/null || groupadd -g {r.Gid} sharegroup\nuseradd -M -u {r.Uid} -g {r.Gid} -s /sbin/nologin {r.Username}\nsmbpasswd -s -a -c /state/account.conf {r.Username} < /state/password\n");
                Write(Path.Combine(ServerRoot,"account.conf"),"[global]\npassdb backend = tdbsam:/state/passdb.tdb\nprivate dir = /state/private\n");
                await Require("podman",["run","--rm","--network=none","--security-opt=label=disable","--volume",ServerRoot+":/state:rw","--pull=never",Image,"sh","/state/account.sh"],30);
            }finally{File.Delete(secret);File.Delete(Path.Combine(ServerRoot,"account.sh"));}
            Save(config with{Hosted=[..config.Hosted,share]});return share;
        }finally{gate.Release();}
    }
    public async Task RemoveHosted(string id)
    {
        await gate.WaitAsync();try
        {
            var c=Read();if(c.Server.Enabled)throw new InvalidOperationException("Stop hosting before changing shares.");
            Save(c with{Hosted=c.Hosted.Where(s=>s.Id!=id).ToArray()});
            // Stale passdb entries cannot log in: service users and valid users are generated
            // solely from current shares. Keep the database for crash-safe recreation.
        }finally{gate.Release();}
    }
    public static string SambaConfiguration(SmbHostedShare[] shares)=>"[global]\nserver role = standalone server\nsecurity = user\nmap to guest = Never\nserver min protocol = SMB3_00\nserver smb encrypt = required\nsmb ports = 445\nload printers = no\ndisable spoolss = yes\npassdb backend = tdbsam:/state/passdb.tdb\nprivate dir = /state/private\nlog file = /dev/stdout\nlogging = file\n"+string.Join("\n",shares.Select(s=>$"[{s.Name}]\npath = /shares/{s.Id}\nvalid users = {s.Username}\n{ClientAccess(s)}read only = {(s.ReadOnly?"yes":"no")}\nwide links = no\nfollow symlinks = no\n"));
    public async Task SetServer(SmbServerSettings settings)
    {
        await gate.WaitAsync();try
        {
            PrivateRoot();var config=Read();
            if(!settings.Enabled)
            {
                if(File.Exists(Path.Combine(systemDirectory,Service)))await Require("systemctl",["disable","--now",Service],45);
                Save(config with{Server=settings});return;
            }
            if(!IPAddress.TryParse(settings.Address,out var ip)||ip.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork||IPAddress.Any.Equals(ip)||IPAddress.Loopback.Equals(ip))throw new InvalidOperationException("Select a specific LAN or VPN IPv4 address to listen on.");
            if(config.Hosted.Length==0)throw new InvalidOperationException("Add a hosted folder first.");
            foreach(var share in config.Hosted)if(await Filesystem(HostedPath(share.Path))!=share.FilesystemUuid)throw new InvalidOperationException("A hosted folder's filesystem has changed. Hosting remains stopped.");
            Write(Path.Combine(ServerRoot,"smb.conf"),SambaConfiguration(config.Hosted));
            var script="set -eu\n"+string.Join("\n",config.Hosted.Select(s=>$"getent group {s.Gid} >/dev/null || groupadd -g {s.Gid} g{s.Gid}\nuseradd -M -u {s.Uid} -o -g {s.Gid} -s /sbin/nologin {s.Username}"))+"\nexec smbd --foreground --no-process-group --configfile=/state/smb.conf\n";
            Write(Path.Combine(ServerRoot,"start.sh"),script);
            var verify="#!/bin/sh\nset -eu\n"+string.Join("\n",config.Hosted.Select(s=>$"test \"$(findmnt -n -o UUID --target {Shell(s.Path)})\" = {Shell(s.FilesystemUuid)}\ntest -d {Shell(s.Path)}\ntest \"$(readlink -e -- {Shell(s.Path)})\" = {Shell(s.Path)}"))+"\n";
            Write(Path.Combine(ServerRoot,"verify.sh"),verify);
            var args=new List<string>{"run","--rm","--name=xur-smb","--pull=never","--security-opt=label=disable","--publish",settings.Address+":445:445/tcp","--volume",ServerRoot+":/state:rw"};
            foreach(var s in config.Hosted)args.AddRange(["--mount",$"type=bind,source={s.Path},destination=/shares/{s.Id},ro={s.ReadOnly.ToString().ToLowerInvariant()},bind-propagation=rprivate,bind-nonrecursive"]);
            args.AddRange([Image,"sh","/state/start.sh"]);
            Write(Path.Combine(systemDirectory,Service),"[Unit]\nDescription=Xur SMB hosting\nAfter=network-online.target\nWants=network-online.target\nRequiresMountsFor="+string.Join(' ',config.Hosted.Select(s=>Quote(s.Path)))+"\n[Service]\nExecStartPre=/bin/sh "+Quote(Path.Combine(ServerRoot,"verify.sh"))+"\nExecStart=/usr/bin/podman "+string.Join(' ',args.Select(Quote))+"\nExecStop=/usr/bin/podman stop --time 10 xur-smb\nRestart=on-failure\nRestartSec=10\nTimeoutStartSec=60\nTimeoutStopSec=30\n[Install]\nWantedBy=multi-user.target\n");
            await Require("systemctl",["daemon-reload"]);await Require("systemctl",["enable",Service]);
            Save(config with{Server=settings});await Require("systemctl",["restart",Service],75);
            await Require("systemctl",["is-active",Service]);
        }finally{gate.Release();}
    }
}
