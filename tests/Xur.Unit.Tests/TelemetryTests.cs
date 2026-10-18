using System.Net;
using System.Text.Json;
using Xur.Agent;
using Xur.Domain;

static class TelemetryTests
{
    // Protocol fixture for ordinary tests. The same assertions run against the pinned real server below.
    public sealed class Handler:HttpMessageHandler
    {
        public bool Offline;public int Imports;public int Snapshots;readonly List<string> lines=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(Offline)return new(HttpStatusCode.ServiceUnavailable);
            var path=request.RequestUri!.AbsolutePath;string body="";
            lock(lines)
            {
                if(path=="/api/v1/export")
                {
                    var selector=Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query)["match[]"].ToString();
                    body=string.Join('\n',lines.Where(line=>{using var doc=JsonDocument.Parse(line);var m=doc.RootElement.GetProperty("metric");var prefix="{kind="+m.GetProperty("kind").GetRawText()+",run="+m.GetProperty("run").GetRawText();if(selector==prefix+"}")return true;if(!selector.StartsWith(prefix+",entity=~",StringComparison.Ordinal))return false;var pattern=JsonSerializer.Deserialize<string>(selector[(prefix.Length+9)..^1])!;return System.Text.RegularExpressions.Regex.IsMatch(m.GetProperty("entity").GetString()!,"^(?:"+pattern+")$");}));
                }
                if(path=="/snapshot/create"){Snapshots++;body="{\"status\":\"ok\",\"snapshot\":\"fixture\"}";}
            }
            if(path=="/api/v1/import")
            {
                var data=await request.Content!.ReadAsStringAsync(ct);
                lock(lines){Imports++;lines.AddRange(data.Split('\n',StringSplitOptions.RemoveEmptyEntries));}
            }
            return new(HttpStatusCode.OK){Content=new StringContent(body)};
        }
    }
    public static async Task Run(Action<bool,string> check,VictoriaMetrics? real=null)
    {
        var handler=new Handler();using var http=new HttpClient(handler){BaseAddress=new Uri("http://metrics")};var store=real??new(http);
        var at=DateTimeOffset.UtcNow.AddHours(-1).AddTicks(1234);
        GpuReading[] readings=[new(at,0,1200.25,24576,23375.75,35.55,200,42,0,0,210,405,"P8"),new(at.AddSeconds(15)),new(at.AddSeconds(30),PowerWatts:135.1,FanRpm:1200,PerformanceState:"P2")];
        var rows=readings.Select(p=>VictoriaMetrics.Gpu("0000:01:00.0",p)).ToArray();
        await store.ImportGpu(rows);
        var read=await store.Read("gpu",at,at.AddSeconds(31));
        check(read.Select(VictoriaMetrics.Gpu).SequenceEqual(readings),"VictoriaMetrics preserves numeric fields, nulls, zeroes, states and submillisecond timestamps");
        await store.ImportGpu(rows);
        check((await store.Read("gpu",at,at.AddSeconds(31))).Length==3,"Retried imports produce one reading per device and timestamp");
        var net=new NetworkPoint(at.AddMinutes(1),123.25,null,long.MaxValue-7,9007199254740993);
        await store.Write("network",[VictoriaMetrics.Network("eth0",net)]);
        await store.Checkpoint();
        var network=await store.Read("network",net.At,net.At);
        check(network.Length==1&&VictoriaMetrics.Network(network[0])==net,"Network counters retain every bit above the double exact-integer range and nullable rates survive");
        await store.Write("gpu",[VictoriaMetrics.Gpu("0000:41:00.0",new(at.AddMinutes(2),TemperatureC:23))]);await store.Checkpoint();
        check((await store.Read("gpu",at,at.AddMinutes(3))).Select(p=>p.Entity).Distinct().Count()==2,"New devices and measurements can appear without rewriting old history");
        if(real==null)
        {
            check(handler.Snapshots==4,"Legacy imports use durable snapshots instead of mistaking search flushes for persistence");
            handler.Offline=true;var buffer=new TelemetryBuffer(store,"gpu",2);
            await buffer.Append(rows,default);check(buffer.Dropped==1&&buffer.Error!.Contains("2 samples buffered"),"An outage uses bounded RAM and reports dropped samples");
            handler.Offline=false;await buffer.Append([],default);check(buffer.Error!.Contains("recovered"),"Recovery retries retained readings and keeps data-loss information visible");
        }
        var names=Enumerable.Range(0,17).Select(n=>"veth.test-"+n).ToArray();
        await store.Write("network",names.Select(n=>VictoriaMetrics.Network(n,net)));await store.Checkpoint();
        var interfaces=await store.ReadEntities("network",net.At,net.At,names);
        check(interfaces.Length==17&&interfaces.All(p=>VictoriaMetrics.Network(p)==net),"History queries split growing interface sets into bounded requests without losing readings");
        var directory=Path.Combine(Environment.CurrentDirectory,".build/evidence/telemetry-tests/migration-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var legacy=Path.Combine(directory,"gpu-history.json");var original=JsonSerializer.Serialize(new Dictionary<string,GpuReading[]>{{"0000:01:00.0",readings}});await File.WriteAllTextAsync(legacy,original);
        var monitor=new GpuMonitor(directory,store);await monitor.Migrate();await monitor.Migrate();
        check(File.ReadAllText(legacy)==original&&File.Exists(legacy+".victoriametrics-imported"),"GPU JSON migration is idempotent and preserves the original file");
        if(real==null)
        {
            File.Delete(legacy+".victoriametrics-imported");handler.Offline=true;
            try{await monitor.Migrate();}catch(HttpRequestException){}
            check(!File.Exists(legacy+".victoriametrics-imported")&&File.ReadAllText(legacy)==original,"A failed migration never marks or removes unverified history");
        }
    }
}
