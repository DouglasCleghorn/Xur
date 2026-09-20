using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

public sealed class StationAccounts(string directory="/var/lib/xur/station-users")
{
    static readonly SemaphoreSlim gate=new(1,1);
    public static StationAccount[] Read(string passwd="/etc/passwd",bool includeTemporary=false)=>File.ReadLines(passwd).Select(line=>line.Split(':')).Where(p=>p.Length==7 && int.TryParse(p[2],out var uid) && uid>=1000 && uid<65534 && ProfilePolicy.UserName(p[0]) && (includeTemporary||!p[0].StartsWith("xurtmp")) && p[5]=="/var/home/"+p[0] && p[6].StartsWith("/") && p[6] is not ("/bin/false" or "/usr/bin/false" or "/sbin/nologin" or "/usr/sbin/nologin")).Select(p=>new StationAccount(p[0],int.Parse(p[2]),string.IsNullOrWhiteSpace(p[4])?p[0]:p[4].Split(',')[0],p[5])).OrderBy(p=>p.Name).ToArray();
    public static StationAccount Resolve(StationUser user)=>Read().SingleOrDefault(a=>a.Username==user.Username && a.Uid==user.Uid) ?? throw new InvalidOperationException("The workstation user changed or is no longer available. Select a user again.");
    public static string Username(Workload w)=>w.User is {Temporary:false} u?u.Username:(w.User?.Temporary==true?"xurtmp":"xurws")+Canonical.Hash(w.Id)[..12];
    static async Task Run(string command,string[] args){var r=await Processes.Run(command,args,30);if(r.ExitCode!=0)throw new InvalidOperationException("Could not create or remove the workstation account. Check system logs.");}
    static void Write(string path,StationAccount account)
    {Directory.CreateDirectory(Path.GetDirectoryName(path)!);using(var f=new FileStream(path+".tmp",FileMode.Create,FileAccess.Write)){JsonSerializer.Serialize(f,account);f.Flush(true);}File.Move(path+".tmp",path,true);}
    static StationAccount? Find(string name)
    {var p=File.ReadLines("/etc/passwd").Select(l=>l.Split(':')).SingleOrDefault(p=>p.Length==7&&p[0]==name);return p==null?null:new(p[0],int.Parse(p[2]),p[4],p[5]);}
    public async Task<StationAccount> Create(string name)
    {
        if(string.IsNullOrWhiteSpace(name)||name.Length>60||name.Any(c=>char.IsControl(c)||c is ':' or ','))throw new InvalidOperationException("Enter a user name of 1–60 characters.");
        await gate.WaitAsync();try{
            if(Read().Any(a=>string.Equals(a.Name,name.Trim(),StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("That user name already exists. Select it from the list.");
            var username="xuruser"+Guid.NewGuid().ToString("N")[..12];var home="/var/home/"+username;
            await Run("useradd",["--create-home","--home-dir",home,"--shell","/bin/bash","--comment",name.Trim(),username]);
            var account=Find(username) ?? throw new InvalidOperationException("The new account is not available.");Write(Path.Combine(directory,username+".json"),account);return account;
        }finally{gate.Release();}
    }
    public async Task<StationAccount> Prepare(Workload w)
    {
        if(w.User is {Temporary:false} selected)return Resolve(selected);
        var user=Username(w);var path=Path.Combine(directory,user+".json");
        await gate.WaitAsync();try{
            var account=Find(user);
            if(w.User?.Temporary==true && account!=null){await RemoveTemporaryUnlocked(user,path);account=null;}
            if(account!=null){if(account.Uid<1000||account.Home!="/var/home/"+user)throw new InvalidOperationException("Workstation account identity changed.");return account;}
            var home="/var/home/"+user;await Run("useradd",["--create-home","--home-dir",home,"--shell","/bin/bash","--comment",w.User?.Temporary==true?"Temporary workstation":"Workstation",user]);
            account=Find(user) ?? throw new InvalidOperationException("The workstation account is not available.");Write(path,account);return account;
        }finally{gate.Release();}
    }
    public async Task RemoveTemporary(Workload w)
    {if(w.User?.Temporary!=true)return;await gate.WaitAsync();try{await RemoveTemporaryUnlocked(Username(w),Path.Combine(directory,Username(w)+".json"));}finally{gate.Release();}}
    static async Task RemoveTemporaryUnlocked(string user,string path)
    {
        var actual=Find(user);if(actual==null){File.Delete(path);return;}
        var saved=File.Exists(path)?JsonSerializer.Deserialize<StationAccount>(File.ReadAllText(path)):null;
        if(saved==null||saved.Username!=user||saved.Uid!=actual.Uid||actual.Uid<1000||actual.Home!="/var/home/"+user||!user.StartsWith("xurtmp"))throw new InvalidOperationException("Temporary account identity changed; its files were kept.");
        if((await Processes.Run("pgrep",["-u",user],10)).ExitCode!=1)throw new InvalidOperationException("Temporary user processes are still running.");
        await Run("userdel",["--remove",user]);File.Delete(path);
    }
    public static void ValidateForStop(Workload w)
    {
        var account=Find(Username(w));if(account==null)return;
        if(account.Uid<1000||account.Home!="/var/home/"+account.Username||w.User is {Temporary:false} u && u.Uid!=account.Uid)throw new InvalidOperationException("Workstation user identity changed; stop was blocked.");
    }
}
