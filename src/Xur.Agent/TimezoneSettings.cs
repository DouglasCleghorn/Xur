using Xur.Domain;
using System.Text.Json;
namespace Xur.Agent;

public sealed class TimezoneSettings(string directory,Func<string,string[],int,Task<ProcessResult>>? runner=null, HttpClient? client=null)
{
    readonly SemaphoreSlim gate=new(1,1);
    readonly Func<string,string[],int,Task<ProcessResult>> run=runner??((exe,args,timeout)=>Processes.Run(exe,args,timeout));
    static readonly HttpClient geoClient=new(){Timeout=TimeSpan.FromSeconds(15)};
    string ModeFile=>Path.Combine(directory,"timezone-mode");
    bool Automatic=>File.Exists(ModeFile)?File.ReadAllText(ModeFile).Trim()=="automatic":!File.Exists(Path.Combine(directory,"timezone"));
    DateTimeOffset? refreshedAt;string? error;
    void SaveMode(bool automatic){Directory.CreateDirectory(directory);File.WriteAllText(ModeFile+".tmp",automatic?"automatic":"manual");File.Move(ModeFile+".tmp",ModeFile,true);}
    public async Task Initialize(CancellationToken stopping)
    {
        // Network availability at boot is variable. Never delay the web manager.
        foreach(var seconds in new[]{0,15,45,120})
        {
            try{await Task.Delay(TimeSpan.FromSeconds(seconds),stopping);if(!Automatic)return;await Refresh();return;}
            catch(OperationCanceledException) when(stopping.IsCancellationRequested){return;}
            catch(Exception e){error="Automatic timezone unavailable; keeping the current zone. "+e.Message;}
        }
    }
    public async Task<TimezoneStatus> Refresh()
    {
        await gate.WaitAsync();try
        {
            if(!Automatic)throw new InvalidOperationException("Enable Automatic timezone before refreshing.");
            SaveMode(true);
            try
            {
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var response=await (client??geoClient).GetAsync("https://ipwho.is/",HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                response.EnsureSuccessStatusCode();
                await response.Content.LoadIntoBufferAsync(65536,timeout.Token);
                using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                var root=json.RootElement;
                if(!root.TryGetProperty("success",out var success)||success.ValueKind!=JsonValueKind.True||!root.TryGetProperty("timezone",out var zone)||!zone.TryGetProperty("id",out var id))throw new InvalidOperationException("IP timezone lookup did not return a timezone.");
                await Apply(id.GetString()??"");refreshedAt=DateTimeOffset.UtcNow;error=null;return await Read();
            }
            catch(Exception e) when(e is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
            {error="Could not detect timezone; the previous zone is retained.";throw new InvalidOperationException(error,e);}
        }finally{gate.Release();}
    }
    public async Task<TimezoneStatus> Set(TimezoneRequest request)
    {
        if(!request.Automatic)return await Set(request.Zone);
        await gate.WaitAsync();try{SaveMode(true);}finally{gate.Release();}
        return await Refresh();
    }
    public async Task<TimezoneStatus> Read()
    {
        var zones=await run("timedatectl",["list-timezones"],10);
        var current=await run("timedatectl",["show","--property=Timezone","--value"],10);
        if(zones.ExitCode!=0||current.ExitCode!=0)throw new InvalidOperationException("Could not read system timezone settings.");
        return new(current.Output.Trim(),zones.Output.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Append("UTC").Distinct().Order(StringComparer.Ordinal).ToArray(),Automatic,refreshedAt,error);
    }
    public async Task<TimezoneStatus> Set(string zone)
    {
        await gate.WaitAsync();try
        {
            await Apply(zone);SaveMode(false);error=null;return await Read();
        }finally{gate.Release();}
    }
    async Task Apply(string zone)
    {
            var status=await Read();
            if(string.IsNullOrEmpty(zone)||!status.Zones.Contains(zone,StringComparer.Ordinal))throw new InvalidOperationException("Select a timezone from the list.");
            var result=await run("timedatectl",["set-timezone",zone],15);
            if(result.ExitCode!=0)throw new InvalidOperationException("Could not save the system timezone: "+Redaction.Logs(result.Output));
            var after=await Read();if(after.Current!=zone)throw new InvalidOperationException("System timezone did not match the requested setting.");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory,"timezone.tmp"),zone+"\n");
            File.Move(Path.Combine(directory,"timezone.tmp"),Path.Combine(directory,"timezone"),true);
            TimeZoneInfo.ClearCachedData();
    }
    // Podman copies the host zone file, so images do not need their own tzdata.
    // Explicit TZ overrides images that bake in UTC. Containers are recreated by
    // profile stop/start; changing the setting never kills continuing workloads.
    public static string[] ContainerArguments()=>["--tz=local","--env=TZ=:/etc/localtime"];
    public const string StationEnvironment="TZ=:/etc/localtime";
}
