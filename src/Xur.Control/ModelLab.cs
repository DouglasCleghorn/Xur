using System.Text.Json;
using Xur.Domain;
namespace Xur.Control;
public sealed class ModelLab
{
    readonly string directory;
    readonly Func<Task<ProfileState>> observe;
    readonly Func<Task<BackendRoute[]>> routes;
    readonly Func<CancellationToken,Task<GpuTelemetrySnapshot>> sample;
    readonly ModelChatClient chat;
    readonly ApplicationMaintenance maintenance;
    readonly CancellationToken shutdown;
    readonly object gate=new();
    readonly SemaphoreSlim chatSlots=new(4);
    readonly JsonSerializerOptions json=new(JsonSerializerDefaults.Web);
    LabRun? active;CancellationTokenSource? cancellation;
    public ModelLab(string directory,Func<Task<ProfileState>> observe,Func<Task<BackendRoute[]>> routes,Func<CancellationToken,Task<GpuTelemetrySnapshot>> sample,ModelChatClient chat,ApplicationMaintenance maintenance,CancellationToken shutdown=default)
    {
        this.directory=directory;this.observe=observe;this.routes=routes;this.sample=sample;this.chat=chat;this.maintenance=maintenance;this.shutdown=shutdown;
        Directory.CreateDirectory(directory);File.SetUnixFileMode(directory,(UnixFileMode)448);
        foreach(var path in Directory.GetFiles(directory,"*.json"))
        {
            try{var old=JsonSerializer.Deserialize<LabRun>(File.ReadAllText(path),json);if(old?.State is "Running" or "Cancelling")Save(old with{State="Interrupted",Finished=DateTimeOffset.UtcNow,Error="Xur restarted before the benchmark completed. Partial results are retained."});}
            catch(JsonException){} // One damaged record must not hide other saved runs.
        }
    }
    public static void Validate(LabRequest r)
    {
        if(!ProfilePolicy.EntityIdentifier(r.WorkloadId)||string.IsNullOrWhiteSpace(r.Prompt)||r.Prompt.Length>16000||r.Requests is <1 or >64||r.Concurrency is <1 or >8||r.Concurrency>r.Requests||r.MaxTokens is <1 or >4096||!double.IsFinite(r.Temperature)||r.Temperature is <0 or >2)
            throw new InvalidOperationException("Choose a running text model, a prompt up to 16,000 characters, 1–64 requests, concurrency 1–8, and 1–4,096 output tokens.");
    }
    public static void Validate(LabChatRequest r)
    {
        if(!ProfilePolicy.EntityIdentifier(r.WorkloadId)||r.Messages is not {Length:>0 and <=64}||r.Messages.Any(m=>m==null||m.Role is not ("system" or "user" or "assistant")||string.IsNullOrWhiteSpace(m.Content))||r.Messages.Sum(m=>(long)m.Content.Length)>64000||r.MaxTokens is <1 or >4096||!double.IsFinite(r.Temperature)||r.Temperature is <0 or >2)
            throw new InvalidOperationException("Use 1–64 text messages (up to 64,000 characters), 1–4,096 output tokens, and temperature 0–2.");
    }
    static Workload[] Definitions(ProfileState state)=>(state.Active?.Workloads??[]).Concat(state.Profiles.SelectMany(p=>p.Workloads)).ToArray();
    static LabContext Context(ProfileState state)=>new(DateTimeOffset.UtcNow,state.Active?.Name,ApplicationIdentity.Id,state.Runtime.Instances.Select(i=>{
        var w=Definitions(state).FirstOrDefault(w=>w.Id==i.Id&&w.Fingerprint==i.Fingerprint);
        return new LabRunningWorkload(i.Id,w?.Name??"Unknown workload",w?.Recipe.Kind??"Unknown",w?.Recipe.Engine??"Unknown",w?.Recipe.Image??"Unknown",i.Gpus,i.State,i.InstanceId,i.Fingerprint);
    }).ToArray(),state.Runtime.Gpus);
    static bool TextModel(Workload w)=>w.Recipe.Kind=="Model"&&w.Recipe.Engine is "llama.cpp" or "vLLM";
    public async Task<LabTarget[]> Targets()
    {
        var state=await observe().WaitAsync(TimeSpan.FromSeconds(15));var published=await routes().WaitAsync(TimeSpan.FromSeconds(10));
        return Definitions(state).Where(TextModel).DistinctBy(w=>(w.Id,w.Fingerprint,w.Route)).Select(w=>{
            var i=state.Runtime.Instances.FirstOrDefault(i=>i.Id==w.Id&&i.Fingerprint==w.Fingerprint&&i.State=="running");
            return i!=null&&published.Any(r=>r.Name==w.Route&&r.WorkloadId==w.Id&&r.Endpoint==i.Endpoint)?new LabTarget(w,i):null;
        }).OfType<LabTarget>().DistinctBy(t=>t.Workload.Id).ToArray();
    }
    async Task<LabTarget> Target(string id)=>(await Targets()).FirstOrDefault(t=>t.Workload.Id==id)??throw new InvalidOperationException("Load a text-chat model and wait until its endpoint is ready.");
    public async Task<LabRun> Start(LabRequest request)
    {
        Validate(request);var target=await Target(request.WorkloadId);var context=Context(await observe().WaitAsync(TimeSpan.FromSeconds(15)));
        lock(gate)
        {
            if(cancellation!=null)throw new InvalidOperationException("A benchmark is already running. Cancel it or wait for completion.");
            if(!maintenance.Enter())throw new InvalidOperationException("An update is in progress. Try again after it completes.");
            var run=new LabRun(Guid.NewGuid().ToString("N"),"Running",DateTimeOffset.UtcNow,null,request,target,context,null,[],[]);
            try{Save(run);}catch{maintenance.Exit();throw;}
            active=run;cancellation=CancellationTokenSource.CreateLinkedTokenSource(shutdown);cancellation.CancelAfter(TimeSpan.FromMinutes(15));
            _=Task.Run(()=>Run(run.Id,cancellation));return run;
        }
    }
    public LabRun? Get(string id)
    {
        if(!Guid.TryParseExact(id,"N",out _))return null;
        lock(gate){if(active?.Id==id)return active;var path=Path.Combine(directory,id+".json");return File.Exists(path)?JsonSerializer.Deserialize<LabRun>(File.ReadAllText(path),json):null;}
    }
    public LabSummary[] List()
    {
        lock(gate)return Directory.GetFiles(directory,"*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(100).Select(p=>{try{return Get(Path.GetFileNameWithoutExtension(p));}catch(JsonException){return null;}}).OfType<LabRun>().OrderByDescending(r=>r.Started).Select(Summary).ToArray();
    }
    public void Cancel(string id)
    {
        lock(gate){if(active?.Id!=id||active.State is not ("Running" or "Cancelling")||cancellation==null)throw new InvalidOperationException("This benchmark is no longer running.");Update(r=>r with{State="Cancelling"});cancellation.Cancel();}
    }
    void Save(LabRun run)
    {
        var path=Path.Combine(directory,run.Id+".json");
        using(var stream=new FileStream(path+".tmp",FileMode.Create,FileAccess.Write,FileShare.None)){File.SetUnixFileMode(path+".tmp",(UnixFileMode)384);JsonSerializer.Serialize(stream,run,json);stream.Flush(true);}
        File.Move(path+".tmp",path,true);
    }
    void Update(Func<LabRun,LabRun> change){lock(gate){active=change(active!);Save(active);}}
    async Task Run(string id,CancellationTokenSource stop)
    {
        using var sampling=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);Task? sampler=null;
        try
        {
            var run=Get(id)!;
            async Task Sample()
            {
                LabSample point;
                try
                {
                    var telemetry=await sample(sampling.Token).WaitAsync(TimeSpan.FromSeconds(10),sampling.Token);
                    var context=Context(await observe().WaitAsync(TimeSpan.FromSeconds(10),sampling.Token));
                    point=new(DateTimeOffset.UtcNow,telemetry.Gpus.Select(g=>g with{History=[]}).ToArray(),telemetry.Error,context.Workloads);
                }
                catch(OperationCanceledException)when(sampling.IsCancellationRequested){return;}
                catch(Exception e)when(e is HttpRequestException or IOException or TimeoutException or JsonException or InvalidOperationException){point=new(DateTimeOffset.UtcNow,[],"Telemetry or workload snapshot unavailable.");}
                Update(r=>r with{Samples=[..r.Samples,point]});
            }
            await Sample();
            sampler=Task.Run(async()=>{while(!sampling.IsCancellationRequested){await Task.Delay(2000,sampling.Token);await Sample();}});
            async Task Request(int number,bool warmup)
            {
                var current=await Target(run.Target.Workload.Id).WaitAsync(stop.Token);
                if(current.Instance.InstanceId!=run.Target.Instance.InstanceId||current.Workload.Fingerprint!=run.Target.Workload.Fingerprint||current.Workload.Route!=run.Target.Workload.Route)
                    throw new InvalidOperationException("The selected workload changed during the benchmark. Start a new run.");
                var response=await chat.Send(run.Target,[new("user",run.Settings.Prompt)],run.Settings.MaxTokens,run.Settings.Temperature,number,warmup,null,stop.Token);
                Update(r=>r with{Responses=[..r.Responses,response]});
                if(warmup&&response.Error!=null)throw new InvalidOperationException("Warm-up failed: "+response.Error);
            }
            if(run.Settings.Warmup)await Request(0,true);
            await Parallel.ForEachAsync(Enumerable.Range(1,run.Settings.Requests),new ParallelOptions{MaxDegreeOfParallelism=run.Settings.Concurrency,CancellationToken=stop.Token},async(n,_)=>await Request(n,false));
            await Sample();
            var end=Context(await observe().WaitAsync(TimeSpan.FromSeconds(10),stop.Token));
            Update(r=>r with{State=r.Responses.Any(x=>x.Error!=null)?"Completed with errors":"Completed",Finished=DateTimeOffset.UtcNow,EndContext=end});
        }
        catch(OperationCanceledException){Update(r=>r with{State=shutdown.IsCancellationRequested?"Interrupted":"Cancelled",Finished=DateTimeOffset.UtcNow,Error=shutdown.IsCancellationRequested?"Xur stopped. Partial results are retained.":"Stopped by cancellation or the 15-minute run limit. Partial results are retained."});}
        catch(Exception e){Update(r=>r with{State="Failed",Finished=DateTimeOffset.UtcNow,Error=e is InvalidOperationException?e.Message:"The benchmark could not finish. Partial results are retained."});}
        finally
        {
            sampling.Cancel();if(sampler!=null)try{await sampler;}catch(Exception){}
            lock(gate){cancellation=null;stop.Dispose();}maintenance.Exit();
        }
    }
    public async Task<LabResponse> Chat(LabChatRequest request,Func<string,string,Task> delta,CancellationToken cancel)
    {
        Validate(request);if(!await chatSlots.WaitAsync(0,cancel))throw new InvalidOperationException("Four test chats are already active. Stop one before sending another.");
        try{return await chat.Send(await Target(request.WorkloadId),request.Messages,request.MaxTokens,request.Temperature,1,false,delta,cancel);}
        finally{chatSlots.Release();}
    }
    public static LabSummary Summary(LabRun run)
    {
        var completed=run.Responses.Where(r=>!r.Warmup).ToArray();var good=completed.Where(r=>r.Error==null).ToArray();
        var first=good.Where(r=>r.FirstTokenMs!=null).Select(r=>r.FirstTokenMs!.Value).ToArray();
        // Wall throughput includes prefill, streaming and concurrent overlap. Never count chunks as tokens.
        double? throughput=good.Length>0&&good.Length==completed.Length&&good.All(r=>r.OutputTokens!=null)?good.Sum(r=>(long)r.OutputTokens!.Value)/(good.Max(r=>r.Started.AddMilliseconds(r.DurationMs))-good.Min(r=>r.Started)).TotalSeconds:null;
        var peaks=run.Samples.SelectMany(s=>s.Gpus).GroupBy(g=>g.Device.Pci).ToDictionary(g=>g.Key,g=>g.Select(x=>x.Reading.MemoryUsedMiB).Max());
        return new(run.Id,run.State,run.Started,run.Target.Workload.Recipe.Hub?.Repository??run.Target.Workload.Recipe.Name,completed.Length,run.Settings.Requests,first.Length>0?first.Average():null,throughput,peaks,run.Error);
    }
}
