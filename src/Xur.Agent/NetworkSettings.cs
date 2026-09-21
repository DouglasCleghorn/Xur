using System.Text.Json;
using System.Text.RegularExpressions;
using Xur.Domain;
namespace Xur.Agent;

public sealed partial class NetworkSettings(string directory="/run/xur",Func<string,string[],int,Task<ProcessResult>>? runner=null,string profilesDirectory="/etc/NetworkManager/system-connections")
{
    readonly SemaphoreSlim gate=new(1,1);
    const string Bus="org.freedesktop.NetworkManager",BusPath="/org/freedesktop/NetworkManager";
    string PendingFile=>Path.Combine(directory,"network-change.json");
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    Task<ProcessResult> Run(string exe,string[] args,int timeout=20)=>runner!=null?runner(exe,args,timeout):Processes.Run(exe,args,timeout);
    async Task<string> Check(string exe,string[] args,int timeout=20)
    {
        var result=await Run(exe,args,timeout);
        if(result.ExitCode!=0)throw new InvalidOperationException("NetworkManager could not apply the network settings. "+Redaction.Logs(result.Output).Trim());
        return result.Output.Trim();
    }
    Task<string> Nm(params string[] args)=>Check("nmcli",args);
    Task<string> BusCall(string method,params string[] args)=>Check("busctl",["call",Bus,BusPath,Bus,method,..args]);
    NetworkChange? Pending()=>File.Exists(PendingFile)?JsonSerializer.Deserialize<NetworkChange>(File.ReadAllText(PendingFile),Json):null;
    void Save(NetworkChange change)
    {
        Directory.CreateDirectory(directory);File.WriteAllText(PendingFile+".tmp",JsonSerializer.Serialize(change,Json));
        File.SetUnixFileMode(PendingFile+".tmp",UnixFileMode.UserRead|UnixFileMode.UserWrite);File.Move(PendingFile+".tmp",PendingFile,true);
    }
    static string[] Lines(string s)=>s.Split('\n',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
    static string[] List(string s)=>s.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Where(v=>v!="--").ToArray();
    async Task<IpConfiguration> Family(string uuid,string prefix)
    {
        var values=await Nm("--escape","no","-g",prefix+".method,"+prefix+".addresses,"+prefix+".gateway,"+prefix+".dns","connection","show","uuid",uuid);
        var parts=values.Split('\n');string Part(int i)=>i<parts.Length && parts[i]!="--"?parts[i]:"";
        return new(Part(0),List(Part(1)),Part(2),List(Part(3)));
    }
    public async Task<NetworkDevice[]> Devices()
    {
        var devices=new List<NetworkDevice>();
        foreach(var line in Lines(await Nm("-t","-f","DEVICE,TYPE","device","status")))
        {
            var fields=line.Split(':');if(fields.Length!=2 || fields[1]!="ethernet" || !Regex.IsMatch(fields[0],@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,14}$"))continue;
            var name=fields[0];
            var values=(await Nm("--escape","no","-g","GENERAL.HWADDR,GENERAL.STATE,GENERAL.CON-UUID","device","show",name)).Split('\n');
            if(values.Length<2)continue;
            var mac=values[0].Trim().ToUpperInvariant();var uuid=values.Length>2 && Guid.TryParse(values[2],out _)?values[2]:null;
            var addresses=Lines(await Nm("--escape","no","-g","IP4.ADDRESS,IP6.ADDRESS","device","show",name));
            var v4=uuid==null?new IpConfiguration():await Family(uuid,"ipv4");var v6=uuid==null?new IpConfiguration():await Family(uuid,"ipv6");
            var controller=uuid==null?"":await Nm("-g","connection.controller","connection","show","uuid",uuid);
            devices.Add(new(name,mac,values[1],addresses,uuid,v4,v6,controller is "" or "--"));
        }
        var duplicate=devices.GroupBy(d=>d.MacAddress).Where(g=>g.Count()>1).Select(g=>g.Key).ToHashSet();
        return devices.Select(d=>d with {Editable=d.Editable && !duplicate.Contains(d.MacAddress)}).ToArray();
    }
    public static NetworkDevice Resolve(NetworkConfiguration config,NetworkDevice[] devices)
    {
        var matches=devices.Where(d=>(config.Interface.Length==0 || d.Interface==config.Interface) && (config.MacAddress.Length==0 || d.MacAddress==config.MacAddress)).ToArray();
        if(matches.Length!=1 || !matches[0].Editable)throw new InvalidOperationException("The selected wired adapter is missing, ambiguous, or belongs to a bridge/bond. Refresh the interface list.");
        return matches[0];
    }
    public async Task<NetworkSettingsStatus> Read()
    {
        await gate.WaitAsync();try{await Expire();return new(await Devices(),Pending());}finally{gate.Release();}
    }
    async Task Expire()
    {
        var pending=Pending();
        if(pending==null || pending.Stage is "Kept" or "Reverted" or "Failed" || pending.Expires>DateTimeOffset.UtcNow)return;
        // NM rolls back even if this process is stopped. An unconfirmed profile never autoconnects.
        await Rollback(pending,"Confirmation timed out. The previous connection was restored.");
    }
    public async Task<NetworkChange> Apply(NetworkConfiguration request,bool answer=false)
    {
        var config=NetworkValidation.Validate(request);await gate.WaitAsync();try
        {
            await Expire();
            if(Pending() is {Stage:"Applying" or "Confirm"})throw new InvalidOperationException("Keep or revert the pending network change first.");
            var device=Resolve(config,await Devices());var id=Guid.NewGuid().ToString("N");var uuid=Guid.NewGuid().ToString();
            var devicePath=await Nm("-g","GENERAL.DBUS-PATH","device","show",device.Interface);
            if(!Regex.IsMatch(devicePath,@"^/org/freedesktop/NetworkManager/Devices/\d+$"))throw new InvalidOperationException("NetworkManager device identity is unavailable.");
            var checkpoint=await BusCall("CheckpointCreate","aouu","1",devicePath,"120","0");
            var match=Regex.Match(checkpoint,"^o \"(/org/freedesktop/NetworkManager/Checkpoint/[0-9]+)\"$");
            if(!match.Success)throw new InvalidOperationException("NetworkManager did not create a rollback checkpoint.");
            var change=new NetworkChange(id,device.Interface,uuid,device.Connection,match.Groups[1].Value,DateTimeOffset.UtcNow.AddSeconds(120),"Applying","Applying settings. Reconnect and keep them within two minutes, or they will revert.",[..config.Ipv4!.Addresses!,..config.Ipv6!.Addresses!]);
            try
            {
                var args=new List<string>{"connection","add","type","ethernet","con-name","xur-network-"+id,"connection.uuid",uuid,"ifname","*","802-3-ethernet.mac-address",device.MacAddress,"connection.autoconnect","no","connection.autoconnect-priority","999"};
                foreach(var (prefix,ip) in new[]{("ipv4",config.Ipv4),("ipv6",config.Ipv6)})
                    args.AddRange([prefix+".method",ip!.Method,prefix+".addresses",string.Join(',',ip.Addresses!),prefix+".gateway",ip.Gateway!,prefix+".dns",string.Join(',',ip.Dns!),prefix+".ignore-auto-dns",ip.Dns!.Length>0?"yes":"no"]);
                await Nm(args.ToArray());Save(change);
            }
            catch{await Rollback(change,"Could not prepare the network change.");throw;}
            if(answer){await Activate(change);await KeepUnlocked(id);return Pending()!;}
            _=Task.Run(async()=>{await Task.Delay(1500);await gate.WaitAsync();try{await Activate(change);}catch{}finally{gate.Release();}});
            return change;
        }finally{gate.Release();}
    }
    async Task Activate(NetworkChange change)
    {
        try
        {
            await Check("nmcli",["--wait","30","connection","up","uuid",change.Candidate,"ifname",change.Interface],40);
            Save(change with {Stage="Confirm",Message="Settings applied. Keep them before the deadline, or the previous connection will be restored."});
        }
        catch{await Rollback(change,"Could not activate these settings. The previous connection was restored.");throw;}
    }
    async Task Rollback(NetworkChange change,string message)
    {
        var restored=await Run("busctl",["call",Bus,BusPath,Bus,"CheckpointRollback","o",change.Checkpoint]);
        var results=Regex.Matches(restored.Output,"\"/org/freedesktop/NetworkManager/Devices/[0-9]+\" ([0-9]+)");
        var okay=restored.ExitCode==0 && results.Count>0 && results.All(m=>m.Groups[1].Value=="0");
        if(!okay && change.Previous!=null)okay=(await Run("nmcli",["--wait","20","connection","up","uuid",change.Previous],30)).ExitCode==0;
        await Run("busctl",["call",Bus,BusPath,Bus,"CheckpointDestroy","o",change.Checkpoint]);
        var removed=await Run("nmcli",["connection","delete","uuid",change.Candidate]);
        okay=(okay || change.Previous==null) && removed.ExitCode==0;
        Save(change with {Stage=okay?"Reverted":"Failed",Message=okay?message:"Could not restore the previous network connection. Use the local console to review the adapter."});
    }
    public async Task<NetworkChange> Finish(string id,bool keep)
    {
        await gate.WaitAsync();try
        {
            await Expire();var change=Pending();
            if(change==null || change.Id!=id || change.Stage!="Confirm")throw new InvalidOperationException("This network change is no longer awaiting confirmation. Refresh status.");
            if(keep)await KeepUnlocked(id);else await Rollback(change,"The previous connection was restored.");
            return Pending()!;
        }finally{gate.Release();}
    }
    async Task KeepUnlocked(string id)
    {
        var change=Pending();
        if(change==null || change.Id!=id || change.Stage!="Confirm" || change.Expires<=DateTimeOffset.UtcNow)throw new InvalidOperationException("Network confirmation expired.");
        if(await Nm("-g","GENERAL.CON-UUID","device","show",change.Interface)!=change.Candidate)throw new InvalidOperationException("The candidate connection is no longer active.");
        await BusCall("CheckpointDestroy","o",change.Checkpoint);
        try{await Nm("connection","modify","uuid",change.Candidate,"connection.autoconnect","yes");}
        catch
        {
            if(change.Previous!=null)await Run("nmcli",["--wait","20","connection","up","uuid",change.Previous]);
            await Run("nmcli",["connection","delete","uuid",change.Candidate]);
            Save(change with {Stage="Failed",Message="Could not save the connection for boot. The previous profile was retained."});throw;
        }
        Save(change with {Stage="Kept",Message="Network settings saved for this machine and future boots."});
        // Retire only the previously active Xur-created candidate; preserve external profiles.
        if(change.Previous!=null)
        {
            var previous=await Run("nmcli",["-g","connection.id","connection","show","uuid",change.Previous]);
            if(previous.ExitCode==0 && Regex.IsMatch(previous.Output.Trim(),@"^xur-network-[a-f0-9]{32}$"))await Run("nmcli",["connection","delete","uuid",change.Previous]);
        }
    }
}
