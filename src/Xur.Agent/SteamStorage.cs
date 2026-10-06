using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

// Low-priority, bounded passes. Game files remain separate Unix inodes and the
// kernel verifies equality atomically before sharing any physical extents.
public sealed class SteamStorage(string directory,Func<StationAccount[]>? accounts=null,Func<string,IEnumerable<string>,int,CancellationToken,Task<ProcessResult>>? run=null)
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    readonly Func<string,IEnumerable<string>,int,CancellationToken,Task<ProcessResult>> execute=run??((exe,args,seconds,token)=>Processes.Run(exe,args,seconds,token));
    readonly object gate=new();
    bool running;
    DateTimeOffset? finished;
    JsonElement? report;
    string? error;
    public object Status(){lock(gate)return new{running,finishedAt=finished,report,error};}
    public async Task Run(CancellationToken stopping)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2),stopping);
            while(!stopping.IsCancellationRequested)
            {
                await Scan(stopping);
                await Task.Delay(TimeSpan.FromMinutes(15),stopping);
            }
        }
        catch(OperationCanceledException) when(stopping.IsCancellationRequested){}
    }
    public async Task Scan(CancellationToken stopping)
    {
        lock(gate){if(running)return;running=true;error=null;}
        try
        {
            Directory.CreateDirectory(directory);
            File.SetUnixFileMode(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            var accountData=JsonSerializer.Serialize(accounts?.Invoke()??StationAccounts.Read(includeTemporary:true),Json);
            var result=await execute("ionice",["-c","3","nice","-n","19",StationUtility.SourcePath,"steam","share","--",Path.Combine(directory,"pairs.sqlite"),accountData],630,stopping);
            if(result.ExitCode!=0)throw new InvalidOperationException("Steam block sharing could not complete; check storage and kernel support.");
            using var data=JsonDocument.Parse(result.Output);
            lock(gate){report=data.RootElement.Clone();finished=DateTimeOffset.UtcNow;}
            Console.Error.WriteLine("Steam block sharing: "+data.RootElement.GetRawText());
        }
        catch(OperationCanceledException) when(stopping.IsCancellationRequested){throw;}
        catch(Exception e) when(e is IOException or InvalidOperationException or JsonException or OperationCanceledException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            const string message="Steam block sharing could not complete; check storage and kernel support.";
            lock(gate){error=message;finished=DateTimeOffset.UtcNow;}
            Console.Error.WriteLine(message);
        }
        finally{lock(gate)running=false;}
    }
}
