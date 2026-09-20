using System.Net;
using System.Text.RegularExpressions;
using Xur.Domain;
namespace Xur.Agent;
public sealed class NtpSettings(string config="/etc/chrony.conf",string sources="/etc/chrony.d/xur.conf",Func<string,string[],int,Task<ProcessResult>>? runner=null)
{
    readonly SemaphoreSlim gate=new(1,1);
    Task<ProcessResult> Run(string exe,string[] args)=>runner!=null?runner(exe,args,20):Processes.Run(exe,args,20);
    async Task Check(string exe,string[] args){var r=await Run(exe,args);if(r.ExitCode!=0)throw new InvalidOperationException("Could not apply NTP settings: "+Redaction.Logs(r.Output));}
    public static string[] Validate(string[] servers)
    {
        if(servers==null||servers.Length>8)throw new InvalidOperationException("Use at most eight NTP servers.");
        return servers.Select(s=>s?.Trim()??"").Select(s=>
        {
            if(s.Length is <1 or >253 || !(IPAddress.TryParse(s,out _)&&!s.Contains('%') || Regex.IsMatch(s,@"^(?=.{1,253}$)[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*\.?$")))
                throw new InvalidOperationException("Enter NTP hostnames or IP addresses, one per line. Do not include URLs or options.");
            return s;
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public async Task Initialize()
    {
        // A managed file (even an empty one) represents an explicit saved choice.
        if(!File.Exists(sources))await Set(new(true,["time.cloudflare.com"]));
    }
    public async Task<NtpStatus> Read()
    {
        if(!File.Exists(config))throw new InvalidOperationException("Chrony configuration is unavailable on this system.");
        var enabled=await Run("systemctl",["is-enabled","chronyd.service"]);
        var active=await Run("systemctl",["is-active","chronyd.service"]);
        var tracking=await Run("chronyc",["tracking"]);
        var servers=File.Exists(sources)?File.ReadAllLines(sources).Where(l=>l.StartsWith("server ")).Select(l=>l.Split(' ',StringSplitOptions.RemoveEmptyEntries)[1]).ToArray():[];
        return new(enabled.ExitCode==0,active.ExitCode==0,tracking.ExitCode==0&&Regex.IsMatch(tracking.Output,@"(?m)^Leap status\s*:\s*Normal\s*$"),servers,Redaction.Logs(tracking.Output));
    }
    static void Write(string path,string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path+".xur-tmp",text);
        File.SetUnixFileMode(path+".xur-tmp",UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.GroupRead|UnixFileMode.OtherRead);
        File.Move(path+".xur-tmp",path,true);
    }
    public async Task<NtpStatus> Set(NtpRequest request)
    {
        var servers=Validate(request.Servers);await gate.WaitAsync();try
        {
            var before=await Read();var original=File.ReadAllText(config);var previous=File.Exists(sources)?File.ReadAllText(sources):null;
            var include="include "+sources;
            try
            {
                Write(sources,"# Preferred NTP sources managed by Xur; OS and DHCP sources remain fallbacks.\n"+string.Join("",servers.Select(s=>"server "+s+" iburst prefer\n")));
                if(!original.Split('\n').Any(l=>l.Trim()==include))Write(config,original.TrimEnd()+"\n\n"+include+"\n");
                await Check("restorecon",[config,sources]);
                await Check("chronyd",["-p","-f",config]);
                await Check("systemctl",[request.Enabled?"enable":"disable","--now","chronyd.service"]);
                if(request.Enabled && before.Active)await Check("systemctl",["restart","chronyd.service"]);
                return await Read();
            }
            catch
            {
                Write(config,original);if(previous==null)File.Delete(sources);else Write(sources,previous);
                await Run("restorecon",previous==null?[config]:[config,sources]);
                await Run("systemctl",[before.Enabled?"enable":"disable","chronyd.service"]);
                await Run("systemctl",[before.Active?"restart":"stop","chronyd.service"]);throw;
            }
        }finally{gate.Release();}
    }
}
