using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;
public sealed class StorageTrim(string directory,Func<string,string[],int,Task<ProcessResult>>? runner=null)
{
    readonly object gate=new();Task? worker;
    readonly Dictionary<string,TrimResult> results=Load(directory);
    readonly Func<string,string[],int,Task<ProcessResult>> run=runner??((exe,args,timeout)=>Processes.Run(exe,args,timeout));
    static Dictionary<string,TrimResult> Load(string directory)
    {
        try{return (JsonSerializer.Deserialize<TrimResult[]>(File.ReadAllText(Path.Combine(directory,"trim-history.json")))??[]).ToDictionary(r=>r.Id,r=>r with{FinishedAt=r.FinishedAt??DateTimeOffset.UtcNow,Success=r.FinishedAt==null?false:r.Success,Message=r.FinishedAt==null?"Interrupted before completion was recorded.":r.Message});}catch{return [];}
    }
    void Save(){Directory.CreateDirectory(directory);var path=Path.Combine(directory,"trim-history.json");File.WriteAllText(path+".tmp",JsonSerializer.Serialize(results.Values.OrderByDescending(r=>r.StartedAt).Take(100)));File.Move(path+".tmp",path,true);}
    public async Task<TrimStatus> Read()
    {
        var mounts=await StorageMounts.Read(run);
        var timer=await run("systemctl",["show","fstrim.timer","--property=ActiveState,NextElapseUSecRealtime","--no-pager"],10);
        var last=await run("systemctl",["show","fstrim.service","--property=ExecMainExitTimestamp,Result","--no-pager"],10);
        lock(gate)return new(worker is {IsCompleted:false},timer.ExitCode==0?timer.Output.Trim():"Unavailable",last.ExitCode==0?last.Output.Trim():"No scheduled run reported",mounts,results.Values.OrderByDescending(r=>r.StartedAt).ToArray());
    }
    public async Task<TrimResult> Start(string id)
    {
        var mount=(await StorageMounts.Read(run)).SingleOrDefault(m=>m.Id==id&&m.TrimSupported)??throw new InvalidOperationException("This mounted filesystem is not eligible for SSD TRIM. Refresh storage and try again.");
        lock(gate)
        {
            if(worker is {IsCompleted:false})throw new InvalidOperationException("A TRIM operation is already running.");
            var result=new TrimResult(id,DateTimeOffset.UtcNow,null,null,"TRIM is running.");results[id]=result;Save();
            worker=Task.Run(async()=>{
                try
                {
                    // Reobserve immediately before execution; never accept an arbitrary path from HTTP.
                    if(!(await StorageMounts.Read(run)).Any(m=>m.Id==id&&m.TrimSupported))throw new InvalidOperationException("The mount changed before TRIM could start.");
                    var r=await run("fstrim",["--verbose","--",mount.Path],300);
                    lock(gate){results[id]=result with{FinishedAt=DateTimeOffset.UtcNow,Success=r.ExitCode==0,Message=Redaction.Logs(r.Output).Trim()};Save();}
                }
                catch(Exception e){lock(gate){results[id]=result with{FinishedAt=DateTimeOffset.UtcNow,Success=false,Message=e.Message};Save();}}
            });return result;
        }
    }
}
