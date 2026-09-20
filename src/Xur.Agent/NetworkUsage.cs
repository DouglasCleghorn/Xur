using System.Diagnostics;
using Xur.Domain;
namespace Xur.Agent;
public sealed class NetworkUsage
{
    readonly object gate=new();readonly Dictionary<string,(long Rx,long Tx,long Tick)> previous=[];
    readonly Dictionary<string,List<NetworkPoint>> history=[];NetworkUsageSnapshot snapshot=new(DateTimeOffset.UtcNow,[]);
    public NetworkUsageSnapshot Status(int minutes=15){lock(gate)return snapshot with{Adapters=snapshot.Adapters.Select(a=>a with{History=a.History.Where(p=>p.At>DateTimeOffset.UtcNow.AddMinutes(-Math.Clamp(minutes,1,1440))).ToArray()}).ToArray()};}
    public static (double? Receive,double? Send) Rates(long rx,long tx,long oldRx,long oldTx,double seconds)=>seconds<=0||rx<oldRx||tx<oldTx?(null,null):((rx-oldRx)/seconds,(tx-oldTx)/seconds);
    public async Task Run(CancellationToken stop)
    {
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
                points.RemoveAll(p=>p.At<now.AddHours(-24));points.Add(point);adapters.Add(new(name,File.ReadAllText(path+"/operstate").Trim(),point,points.ToArray()));
            }catch(IOException){}catch(FormatException){}
            lock(gate)snapshot=new(now,adapters.ToArray());
            try{await Task.Delay(5000,stop);}catch(OperationCanceledException){break;}
        }
    }
}
