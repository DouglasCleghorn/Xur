using System.Diagnostics;
using Xur.Domain;
namespace Xur.Agent;
public sealed class NetworkUsage(VictoriaMetrics? metrics=null)
{
    readonly VictoriaMetrics store=metrics??VictoriaMetrics.Local();
    string? storageError;
    readonly object gate=new();readonly Dictionary<string,(long Rx,long Tx,long Tick)> previous=[];
    readonly Dictionary<string,List<NetworkPoint>> history=[];NetworkUsageSnapshot snapshot=new(DateTimeOffset.UtcNow,[]);
    public NetworkUsageSnapshot Status(int minutes=15){lock(gate)return snapshot with{Adapters=snapshot.Adapters.Select(a=>a with{History=a.History.Where(p=>p.At>DateTimeOffset.UtcNow.AddMinutes(-Math.Clamp(minutes,1,1440))).ToArray()}).ToArray()};}
    public async Task<NetworkUsageSnapshot> Read(int minutes=15,CancellationToken stop=default)
    {
        var current=Status(minutes);var end=DateTimeOffset.UtcNow;
        try
        {
            var saved=(await store.ReadEntities("network",end.AddMinutes(-Math.Clamp(minutes,1,1440)),end,current.Adapters.Select(a=>a.Name),stop)).GroupBy(r=>r.Entity).ToDictionary(g=>g.Key,g=>g.Select(VictoriaMetrics.Network));
            return current with{Adapters=current.Adapters.Select(a=>a with{History=saved.GetValueOrDefault(a.Name,[]).Concat(a.History).DistinctBy(p=>p.At.ToUnixTimeMilliseconds()).OrderBy(p=>p.At).ToArray()}).ToArray(),Error=storageError};
        }
        catch(Exception e)when(VictoriaMetrics.Unavailable(e)){return current with{Error="Telemetry history storage is unavailable; showing recent memory samples."};}
    }
    public static (double? Receive,double? Send) Rates(long rx,long tx,long oldRx,long oldTx,double seconds)=>seconds<=0||rx<oldRx||tx<oldTx?(null,null):((rx-oldRx)/seconds,(tx-oldTx)/seconds);
    public async Task Run(CancellationToken stop)
    {
        var buffer=new TelemetryBuffer(store,"network");
        while(!stop.IsCancellationRequested)
        {
            var adapters=new List<NetworkAdapterUsage>();var now=DateTimeOffset.UtcNow;var tick=Stopwatch.GetTimestamp();
            foreach(var path in Directory.GetDirectories("/sys/class/net"))try
            {
                var name=Path.GetFileName(path);if(name=="lo")continue;
                long rx=long.Parse(File.ReadAllText(path+"/statistics/rx_bytes")),tx=long.Parse(File.ReadAllText(path+"/statistics/tx_bytes"));
                (double? Receive,double? Send) rates=previous.TryGetValue(name,out var old)?Rates(rx,tx,old.Rx,old.Tx,(tick-old.Tick)/(double)Stopwatch.Frequency):(null,null);
                previous[name]=(rx,tx,tick);var point=new NetworkPoint(now,rates.Receive,rates.Send,rx,tx);
                if(!history.TryGetValue(name,out var points))history[name]=points=[];
                points.RemoveAll(p=>p.At<now.AddHours(-24));points.Add(point);if(points.Count>17280)points.RemoveRange(0,points.Count-17280);adapters.Add(new(name,File.ReadAllText(path+"/operstate").Trim(),point,points.ToArray()));
            }catch(IOException){}catch(FormatException){}
            lock(gate)snapshot=new(now,adapters.ToArray());
            await buffer.Append(adapters.Select(a=>VictoriaMetrics.Network(a.Name,a.Reading)),stop);storageError=buffer.Error;
            foreach(var key in history.Where(p=>p.Value.Count==0||p.Value[^1].At<now.AddDays(-1)).Select(p=>p.Key).ToArray()){history.Remove(key);previous.Remove(key);}
            try{await Task.Delay(5000,stop);}catch(OperationCanceledException){break;}
        }
    }
}
