using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Xur.Domain;

// One raw-sample codec for all Xur telemetry. No resampling, interpolation or JSON history files.
public sealed class VictoriaMetrics(HttpClient http)
{
    public static VictoriaMetrics Local() => new(Xur.IO.HttpClients.Unix(
        Environment.GetEnvironmentVariable("XUR_METRICS_SOCKET") ?? "/run/xur-metrics/metrics.sock", TimeSpan.FromSeconds(10)));
    public record Row(string Entity, DateTimeOffset At, Dictionary<string,double> Values, string? State=null);
    sealed record Series(Dictionary<string,string> metric, double[] values, long[] timestamps);
    static readonly string[] GpuFields=["utilization_percent","memory_used_mib","memory_total_mib","memory_free_mib","power_watts","power_limit_watts","temperature_c","fan_percent","fan_rpm","graphics_clock_mhz","memory_clock_mhz"];
    public static Row Gpu(string pci,GpuReading p)
    {
        double?[] values=[p.Utilization,p.MemoryUsedMiB,p.MemoryTotalMiB,p.MemoryFreeMiB,p.PowerWatts,p.PowerLimitWatts,p.TemperatureC,p.FanPercent,p.FanRpm,p.GraphicsClockMHz,p.MemoryClockMHz];
        return new(pci,p.At,GpuFields.Zip(values).Where(x=>x.Second is {} v&&double.IsFinite(v)).ToDictionary(x=>x.First,x=>x.Second!.Value),p.PerformanceState);
    }
    public static GpuReading Gpu(Row p)
    {
        double? V(string key)=>p.Values.TryGetValue(key,out var v)?v:null;
        return new(p.At,V(GpuFields[0]),V(GpuFields[1]),V(GpuFields[2]),V(GpuFields[3]),V(GpuFields[4]),V(GpuFields[5]),V(GpuFields[6]),V(GpuFields[7]),V(GpuFields[8]),V(GpuFields[9]),V(GpuFields[10]),p.State);
    }
    public static Row Network(string name,NetworkPoint p)
    {
        // Split integer counters: doubles alone would round values above 2^53.
        var values=new Dictionary<string,double>{{"received_high",p.ReceivedBytes>>32},{"received_low",(uint)p.ReceivedBytes},{"sent_high",p.SentBytes>>32},{"sent_low",(uint)p.SentBytes}};
        if(p.ReceiveBytesPerSecond is {} rx&&double.IsFinite(rx))values["receive_bytes_per_second"]=rx;
        if(p.SendBytesPerSecond is {} tx&&double.IsFinite(tx))values["send_bytes_per_second"]=tx;
        return new(name,p.At,values);
    }
    public static NetworkPoint Network(Row p)
    {
        double? V(string key)=>p.Values.TryGetValue(key,out var v)?v:null;
        long Counter(string prefix)=>((long)p.Values.GetValueOrDefault(prefix+"_high")<<32)|(uint)p.Values.GetValueOrDefault(prefix+"_low");
        return new(p.At,V("receive_bytes_per_second"),V("send_bytes_per_second"),Counter("received"),Counter("sent"));
    }
    public static Row Storage(string entity,StorageReading p)
    {
        var values=new Dictionary<string,double>();
        void Bytes(string name,long? bytes)
        {
            if(bytes is not {} n)return;
            values[name]=n;values[name+"_high"]=n>>32;values[name+"_low"]=(uint)n;
        }
        Bytes("capacity_bytes",p.CapacityBytes);Bytes("used_bytes",p.UsedBytes);Bytes("available_bytes",p.AvailableBytes);
        if(p.EnduranceRemainingPercent is {} percent&&double.IsFinite(percent))values["endurance_remaining_percent"]=percent;
        if(p.EnduranceRemainingBytes is {} remaining)
        {
            values["endurance_remaining_bytes"]=(double)remaining;
            // Human/graph-friendly totals plus exact components for lossless raw reads.
            // decimal can represent byte budgets beyond the integer/double ranges.
            var bits=decimal.GetBits(remaining);
            for(var i=0;i<bits.Length;i++)values["endurance_remaining_bytes_part"+i]=(uint)bits[i];
        }
        return new(entity,p.At,values);
    }
    public static StorageReading Storage(Row p)
    {
        long? Bytes(string name)=>p.Values.TryGetValue(name+"_high",out var high)&&p.Values.TryGetValue(name+"_low",out var low)?((long)high<<32)|(uint)low:null;
        var parts=Enumerable.Range(0,4).Select(i=>p.Values.TryGetValue("endurance_remaining_bytes_part"+i,out var value)?(int?)unchecked((int)(uint)value):null).ToArray();
        decimal? remaining=parts.All(p=>p.HasValue)?new decimal(parts.Select(p=>p!.Value).ToArray()):null;
        return new(p.At,Bytes("capacity_bytes"),Bytes("used_bytes"),Bytes("available_bytes"),p.Values.TryGetValue("endurance_remaining_percent",out var percent)?percent:null,remaining);
    }
    public static Row[] Storage(StorageUsageSnapshot snapshot,DateTimeOffset at)=>
        snapshot.Filesystems.Select(f=>Storage("filesystem:"+f.Source,new(at,f.Bytes,f.Used,f.Available)))
        .Concat(snapshot.Disks.Select(d=>Storage("disk:"+d.Path,new(at,d.Bytes,EnduranceRemainingPercent:d.Endurance?.RemainingPercent,EnduranceRemainingBytes:d.Endurance?.RemainingBytes)))).ToArray();
    public async Task Write(string kind,IEnumerable<Row> rows,string run="",CancellationToken stop=default)
    {
        // Bound individual requests during backfill; an interrupted request can safely be retried.
        foreach(var chunk in rows.Chunk(512))
        {
            var series=new Dictionary<(string Entity,string Field,string? State),List<(long At,double Value)>>();
            foreach(var row in chunk)
            {
                void Add(string field,double value,string? state=null)
                {
                    if(!double.IsFinite(value))return;
                    var key=(row.Entity,field,state);if(!series.TryGetValue(key,out var points))series[key]=points=[];
                    points.Add((row.At.ToUnixTimeMilliseconds(),value));
                }
                Add("present",1);Add("submillisecond_ticks",row.At.UtcTicks%TimeSpan.TicksPerMillisecond);
                foreach(var value in row.Values)Add(value.Key,value.Value);
                if(row.State!=null)Add("state",1,row.State);
            }
            var body=new StringBuilder();
            foreach(var (key,points) in series)
            {
                var labels=new Dictionary<string,string>{{"__name__","xur_"+kind+"_"+key.Field},{"entity",key.Entity},{"kind",kind},{"run",run}};
                if(key.State!=null)labels["state"]=key.State;
                body.AppendLine(JsonSerializer.Serialize(new Series(labels,points.Select(p=>p.Value).ToArray(),points.Select(p=>p.At).ToArray())));
            }
            using var content=new StringContent(body.ToString(),Encoding.UTF8,"application/json");
            using var response=await http.PostAsync("/api/v1/import",content,stop);response.EnsureSuccessStatusCode();
        }
    }
    public async Task<Row[]> Read(string kind,DateTimeOffset start,DateTimeOffset end,string run="",CancellationToken stop=default,string[]? entities=null)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(30));stop=deadline.Token;
        string Seconds(DateTimeOffset t)=>(t.ToUnixTimeMilliseconds()/1000m).ToString(CultureInfo.InvariantCulture);
        var selector="{kind="+JsonSerializer.Serialize(kind)+",run="+JsonSerializer.Serialize(run)+(entities==null?"":",entity=~"+JsonSerializer.Serialize("(?:"+string.Join("|",entities.Select(System.Text.RegularExpressions.Regex.Escape))+")"))+"}";
        var uri="/api/v1/export?match[]="+Uri.EscapeDataString(selector)+"&start="+Seconds(start)+"&end="+Seconds(end)+"&reduce_mem_usage=1";
        using var response=await http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,stop);response.EnsureSuccessStatusCode();
        await using var stream=await response.Content.ReadAsStreamAsync(stop);using var reader=new StreamReader(stream);
        var rows=new Dictionary<(string Entity,long At),Row>();long bytes=0;
        while(await reader.ReadLineAsync(stop) is {} line)
        {
            bytes+=line.Length;if(bytes>64*1024*1024)throw new IOException("Telemetry query exceeds the 64 MiB response limit.");
            var s=JsonSerializer.Deserialize<Series>(line)??throw new IOException("Invalid telemetry response.");
            if(s.timestamps.Length!=s.values.Length)throw new IOException("Invalid telemetry sample arrays.");
            var entity=s.metric["entity"];var prefix="xur_"+kind+"_";var name=s.metric["__name__"];
            if(!name.StartsWith(prefix,StringComparison.Ordinal))continue;
            var field=name[prefix.Length..];
            for(var i=0;i<s.timestamps.Length;i++)
            {
                var key=(entity,s.timestamps[i]);
                if(!rows.TryGetValue(key,out var row)){if(rows.Count>=200000)throw new IOException("Too many telemetry samples.");rows[key]=row=new(entity,DateTimeOffset.FromUnixTimeMilliseconds(key.Item2),[]);}
                if(field=="state")rows[key]=row with{State=s.metric.GetValueOrDefault("state")};
                else row.Values[field]=s.values[i];
            }
        }
        // A presence metric preserves all-null readings. Unknown fields are forward compatible.
        return rows.Values.Where(r=>r.Values.ContainsKey("present")).Select(r=>r with{At=r.At.AddTicks((long)r.Values.GetValueOrDefault("submillisecond_ticks"))}).Where(r=>r.At>=start&&r.At<=end).OrderBy(r=>r.At).ToArray();
    }
    public async Task<Row[]> ReadEntities(string kind,DateTimeOffset start,DateTimeOffset end,IEnumerable<string> entities,CancellationToken stop=default)
    {
        var result=new List<Row>();
        // Keep a 24-hour network query below per-request limits even on hosts with many interfaces.
        foreach(var group in entities.Distinct().Chunk(8))result.AddRange(await Read(kind,start,end,stop:stop,entities:group));
        return result.ToArray();
    }
    public async Task ImportGpu(IEnumerable<Row> source,string run="",CancellationToken stop=default)
    {
        var rows=source.Where(r=>r.At>=DateTimeOffset.UtcNow.AddMonths(-12)&&r.At<=DateTimeOffset.UtcNow).ToArray();
        if(rows.Length==0)return;
        await Write("gpu",rows,run,stop);
        // Import parsing/search visibility may lag HTTP acceptance. Check every original reading.
        var expected=rows.ToDictionary(r=>(r.Entity,r.At.ToUnixTimeMilliseconds()),r=>Gpu(r));
        for(var attempt=0;attempt<10;attempt++)
        {
            using var flush=await http.GetAsync("/internal/force_flush",stop);flush.EnsureSuccessStatusCode();
            var actual=(await Read("gpu",rows.Min(r=>r.At),rows.Max(r=>r.At),run,stop)).ToDictionary(r=>(r.Entity,r.At.ToUnixTimeMilliseconds()),r=>Gpu(r));
            if(expected.All(p=>actual.TryGetValue(p.Key,out var value)&&value==p.Value)){await Checkpoint(stop);return;}
            await Task.Delay(500,stop);
        }
        throw new IOException("Telemetry import verification failed; the original history is retained.");
    }
    // Migration only. force_flush makes data searchable but does NOT guarantee disk persistence.
    // A snapshot flushes the underlying parts; removing its hard links leaves those parts durable.
    public async Task Checkpoint(CancellationToken stop=default)
    {
        using var response=await http.GetAsync("/snapshot/create",stop);response.EnsureSuccessStatusCode();
        using var result=JsonDocument.Parse(await response.Content.ReadAsStringAsync(stop));
        if(result.RootElement.GetProperty("status").GetString()!="ok")throw new IOException("Telemetry checkpoint failed.");
        var name=result.RootElement.GetProperty("snapshot").GetString()!;
        using var deleted=await http.GetAsync("/snapshot/delete?snapshot="+Uri.EscapeDataString(name),stop);deleted.EnsureSuccessStatusCode();
    }
    public static bool Unavailable(Exception e)=>e is HttpRequestException or IOException or JsonException or TaskCanceledException;
}

// RAM-only retry window. No secondary disk spool or unbounded backlog during an outage.
public sealed class TelemetryBuffer(VictoriaMetrics store,string kind,int capacity=32768)
{
    readonly Queue<VictoriaMetrics.Row> pending=[];
    public string? Error {get;private set;}
    public long Dropped {get;private set;}
    public async Task Append(IEnumerable<VictoriaMetrics.Row> rows,CancellationToken stop)
    {
        foreach(var row in rows){pending.Enqueue(row);if(pending.Count>capacity){pending.Dequeue();Dropped++;}}
        try{await store.Write(kind,pending,stop:stop);pending.Clear();Error=Dropped>0?$"Telemetry storage recovered; {Dropped} samples were dropped while its memory buffer was full.":null;}
        catch(Exception e)when(VictoriaMetrics.Unavailable(e)){Error=$"Telemetry storage unavailable; {pending.Count} samples buffered in memory, {Dropped} dropped.";}
    }
}
