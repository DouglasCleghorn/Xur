using Xur.Domain;
namespace Xur.Agent;

// A desktop's idle timer must not put the shared appliance and its APIs to sleep.
public sealed class ServerPower
{
    public const string Unit = "xur-stay-awake.service";
    public const string Configuration = """
        [Unit]
        Description=Keep the Xur server available
        After=systemd-logind.service
        Requires=systemd-logind.service

        [Service]
        Type=exec
        ExecStart=/usr/bin/systemd-inhibit --what=sleep:idle --mode=block --who=Xur "--why=Xur serves remote desktops and workloads" /usr/bin/sleep infinity
        Restart=always
        RestartSec=5

        [Install]
        WantedBy=multi-user.target
        """;
    public string? Error { get; private set; }
    public async Task Run(CancellationToken cancellation)
    {
        while(!cancellation.IsCancellationRequested)
        {
            try
            {
                const string path="/etc/systemd/system/"+Unit;
                if(!File.Exists(path)||await File.ReadAllTextAsync(path,cancellation)!=Configuration)
                {
                    await File.WriteAllTextAsync(path+".tmp",Configuration,cancellation);
                    File.SetUnixFileMode(path+".tmp",(UnixFileMode)420);
                    File.Move(path+".tmp",path,true);
                    await Check(["daemon-reload"]);
                }
                await Check(["enable","--now",Unit]);
                Error=null;
            }
            catch(Exception e) when(e is not OperationCanceledException){Error=Redaction.Logs(e.Message);}
            await Task.Delay(TimeSpan.FromMinutes(1),cancellation);
        }
    }
    static async Task Check(string[] args)
    {
        var result=await Processes.Run("systemctl",args,15);
        if(result.ExitCode!=0)throw new InvalidOperationException("Server sleep protection: "+result.Output);
    }
    public async Task<object> Status()
    {
        var unit=await Processes.Run("systemctl",["show",Unit,"--property=ActiveState,SubState,Result"],5);
        var inhibitors=await Processes.Run("systemd-inhibit",["--list","--no-pager"],5);
        var journal=await Processes.Run("journalctl",["-b","--no-pager","-n","60","-u","systemd-logind.service","-u","systemd-suspend.service","-u","systemd-hibernate.service","-u",Unit],5);
        return new {error=Error,unit,inhibitors,journal,bootId=File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim()};
    }
}
