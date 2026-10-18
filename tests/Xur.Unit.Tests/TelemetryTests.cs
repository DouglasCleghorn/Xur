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
        StorageReading[] storage=[new(at.AddMinutes(4),long.MaxValue-7,9007199254740993,0,79.125,7922816251426433759354395.0335m),new(at.AddMinutes(5),1000,EnduranceRemainingPercent:0),new(at.AddMinutes(6),1000)];
        await store.Write("storage",storage.Select(p=>VictoriaMetrics.Storage("disk:/dev/test-storage",p)));await store.Checkpoint();
        var storedDisks=await store.Read("storage",storage[0].At,storage[^1].At);
        check(storedDisks.Select(VictoriaMetrics.Storage).SequenceEqual(storage),"Storage bytes retain exact integer/decimal precision; missing endurance stays absent and zero remains zero");
        StorageFilesystem fs=new("/dev/test-storage1","btrfs",["/","/var"],1000,400,500);
        StorageDiskUsage disk=new("/dev/test-storage","Test disk",2000,[fs.Source],fs.Mounts,new(80));
        var storageRows=VictoriaMetrics.Storage(new(null,false,[fs],[disk],[]),at.AddMinutes(7));
        await store.Write("storage",storageRows);await store.Checkpoint();
        var storedUsage=await store.Read("storage",at.AddMinutes(7),at.AddMinutes(7));
        check(storedUsage.Length==2 && storedUsage.Single(r=>r.Entity.StartsWith("filesystem:")).Values["used_bytes"]==400 && !storedUsage.Single(r=>r.Entity.StartsWith("disk:")).Values.ContainsKey("used_bytes"),"Storage history records one filesystem usage sample and a separate disk endurance sample without duplicated mount totals");
        check(storageRows.All(r=>r.State==null) && storageRows.All(r=>!r.Values.ContainsKey("endurance_remaining_bytes")),"Storage adds no mount/model/error labels and invents no missing endurance bytes");
        if(real==null)
        {
            var probes=0;var observer=new StorageUsage(store,()=>{probes++;return Task.FromResult((new[]{fs},new[]{disk}));});
            handler.Offline=true;await observer.RefreshDisks();
            check(observer.Current.TelemetryError?.Contains("2 samples buffered")==true && observer.Current.Disks.Length==1 && observer.Current.Categories.Length==0,"Storage outages preserve live disk data and expose the bounded retry buffer without folder scans");
            await observer.RefreshDisks();check(probes==1,"Background and page consumers reuse the five-minute disk sample");
            handler.Offline=false;await observer.RefreshDisks(true);
            check(probes==2 && observer.Current.TelemetryError==null,"Storage retries queued samples and clears its outage notice after recovery");
            var failing=new StorageUsage(store,()=>throw new IOException("fixture"));
            try{await failing.RefreshDisks();}catch(IOException){}
            check(failing.Current.TelemetryError?.Contains("could not be sampled")==true,"Disk sampling failures remain visible instead of creating zero-valued history");
            using var stop=new CancellationTokenSource();var observed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var background=new StorageUsage(store,()=>{observed.TrySetResult();return Task.FromResult((new[]{fs},new[]{disk}));});
            var collecting=background.Run(stop.Token);await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));stop.Cancel();await collecting;
            check(background.Current.Disks.Length==1 && background.Current.Categories.Length==0,"Storage history samples without viewers and does not run directory walks");
        }
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
