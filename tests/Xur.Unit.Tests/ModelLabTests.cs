using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xur.Control;
using Xur.Domain;
static class ModelLabTests
{
    public static async Task Run(Action<bool,string> check,VictoriaMetrics? real=null)
    {
        var recipe=new Recipe("test-model","Test model",EngineImages.Image("vllm"),["--max-num-seqs","1"],8080,"/health","NVIDIA",1,1,"",Engine:"vLLM",Hub:new("org/test",new string('b',40),"Apache-2.0"));
        var work=new Workload("1","Test LLM",recipe,["0000:01:00.0"],"test");
        var instance=new RuntimeInstance("1",work.Fingerprint,"instance",123,"boot","http://127.0.0.1:1234","running",work.Gpus);
        var target=new LabTarget(work,instance);string mode="normal";int simultaneous=0,peak=0,calls=0;
        var root=Path.Combine(Path.GetTempPath(),"xur-lab-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));
        await using var app=builder.Build();
        app.MapPost("/test/v1/chat/completions",async(HttpContext c)=>{
            Interlocked.Increment(ref calls);var count=Interlocked.Increment(ref simultaneous);int old;do{old=peak;}while(count>old&&Interlocked.CompareExchange(ref peak,count,old)!=old);
            try{
                using var body=await JsonDocument.ParseAsync(c.Request.Body);
                if(body.RootElement.GetProperty("model").GetString()!="model"||!body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean()||c.Request.Headers["X-Xur-Expected-Workload"]!="1"||c.Request.Headers["X-Xur-Expected-Endpoint"]!=instance.Endpoint){c.Response.StatusCode=400;return;}
                if(mode=="error"){c.Response.StatusCode=500;return;}
                c.Response.ContentType="text/event-stream";
                await c.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\r\n\r\n");await c.Response.Body.FlushAsync();
                await Task.Delay(mode=="slow"?30000:35,c.RequestAborted);
                await c.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Thinking\"}}]}\n\n");await c.Response.Body.FlushAsync();
                await Task.Delay(35,c.RequestAborted);
                await c.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"Hello <script>世界\"}}]}\n\n");await c.Response.Body.FlushAsync();
                if(mode=="incomplete")return;
                await c.Response.WriteAsync("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
                if(mode!="missing")await c.Response.WriteAsync("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":9}}\n\n");
                await c.Response.WriteAsync("data: [DONE]\n\n");
            }catch(OperationCanceledException){}finally{Interlocked.Decrement(ref simultaneous);}
        });
        await app.StartAsync();using var http=new HttpClient{BaseAddress=new Uri(app.Urls.Single()),Timeout=Timeout.InfiniteTimeSpan};var client=new ModelChatClient(http);
        try
        {
            var delta=new StringBuilder();var response=await client.Send(target,[new("user","hello")],128,0,1,false,(t,r)=>{delta.Append(t);return Task.CompletedTask;},default);
            check(response.Error==null&&response.Text=="Hello <script>世界"&&response.Reasoning=="Thinking"&&response.OutputTokens==9&&response.InputTokens==12&&delta.ToString()==response.Text,"Chat parser handles streamed UTF-8, reasoning and server usage without counting chunks as tokens");
            check(response.FirstTokenMs>=20&&response.FirstTokenMs<response.DurationMs,"First-token timing ignores role-only chunks and precedes completion");
            mode="missing";response=await client.Send(target,[new("user","hello")],128,0,1,false,null,default);
            check(response.OutputTokens==null&&response.Error==null,"Missing server token usage remains unknown");
            mode="incomplete";response=await client.Send(target,[new("user","hello")],128,0,1,false,null,default);
            check(response.Error!=null&&response.Text.Length>0,"Truncated streams retain partial text and report failure");
            mode="normal";calls=0;peak=0;
            var gpu=new GpuDevice(work.Gpus[0],"NVIDIA","RTX 3090","nvidia","GPU-fixture",24576,[],[]);
            var secret=new Workload("2","Other container",recipe with{Id="container",Kind="Container",Container=new([],new(){["PASSWORD"]="do-not-export"})},[],"other");
            var other=instance with{Id="2",Fingerprint=secret.Fingerprint,InstanceId="other",Gpus=[]};
            var state=new ProfileState([new("1","Mixed profile",1,[work,secret])],new("1","Mixed profile",1,[work,secret]),new("generation",[gpu],[instance,other]),null);
            var maintenance=new ApplicationMaintenance();var sampleCount=0;
            Task<GpuTelemetrySnapshot> Sample(CancellationToken ct)=>Task.FromResult(new GpuTelemetrySnapshot(DateTimeOffset.UtcNow,[new(gpu,"driver",new(DateTimeOffset.UtcNow,MemoryUsedMiB:1000+100*Interlocked.Increment(ref sampleCount)),[],[])]));
            Task<BackendRoute[]> Routes()=>Task.FromResult(new[]{new BackendRoute(work.Route,work.Id,instance.Endpoint)});
            var metricHandler=new TelemetryTests.Handler();using var metricHttp=new HttpClient(metricHandler){BaseAddress=new Uri("http://metrics")};
            ModelLab New(VictoriaMetrics? storage=null)=>new(root,()=>Task.FromResult(state),Routes,Sample,client,maintenance,metrics:storage??real??new(metricHttp));
            var lab=New();var run=await lab.Start(new("1","hello",4,2,128));
            var denied=false;try{await lab.Start(new("1","hello"));}catch(InvalidOperationException){denied=true;}
            check(denied,"Only one benchmark may run at a time");
            async Task<LabRun> Finish(string id)
            {
                for(int n=0;n<300;n++){var current=(await lab.Get(id))!;if(current.State is not ("Running" or "Cancelling")&&maintenance.Active==0)return current;await Task.Delay(20);}throw new Exception("Benchmark did not finish");
            }
            run=await Finish(run.Id);var summary=ModelLab.Summary(run);
            check(run.State=="Completed"&&calls==5&&peak==2&&run.Responses.Count(r=>r.Warmup)==1&&summary.Completed==4,"Benchmark runs requested concurrency and excludes warm-up from results");
            check(summary.OutputTokensPerSecond>0&&summary.PeakVramMiB[gpu.Pci]>=1200&&run.Samples.Length>=2&&run.Target.Workload.Recipe.Hub?.Revision==new string('b',40),"Saved benchmark retains throughput, sampled peak VRAM and pinned model revision");
            check(run.Context.Workloads.Length==2&&run.Samples.All(s=>s.Workloads?.Length==2)&&!File.ReadAllText(Path.Combine(root,run.Id+".json")).Contains("do-not-export"),"Run snapshots include competing workloads without exporting container secrets");
            if(real!=null)await real.Checkpoint();
            using var offlineHttp=new HttpClient(new TelemetryTests.Handler{Offline=true}){BaseAddress=new Uri("http://metrics")};var offline=new VictoriaMetrics(offlineHttp);
            var restored=(await New(offline).Get(run.Id))!;
            var archive=BenchmarkResults.PathFor(root,run.Id);var archiveBytes=File.ReadAllBytes(archive);
            using(var compressed=new System.IO.Compression.GZipStream(File.OpenRead(archive),System.IO.Compression.CompressionMode.Decompress))
            using(var artifact=await JsonDocument.ParseAsync(compressed))
            {
                check(artifact.RootElement.GetProperty("schema").GetInt32()==1&&artifact.RootElement.GetProperty("summary").GetProperty("completed").GetInt32()==4&&artifact.RootElement.GetProperty("run").GetProperty("responses").GetArrayLength()==5,"Permanent gzip result contains the versioned run, full responses and summary");
            }
            check(archiveBytes.Length<JsonSerializer.SerializeToUtf8Bytes(run).Length&&(File.GetUnixFileMode(archive)&(UnixFileMode)63)==0,"Permanent results are compressed and owner-only");
            check(restored.Samples.SelectMany(s=>s.Gpus).Select(g=>g.Reading).SequenceEqual(run.Samples.SelectMany(s=>s.Gpus).Select(g=>g.Reading)),"Permanent benchmark GPU traces survive recreation with telemetry storage offline");
            var document=JsonSerializer.Deserialize<LabRun>(File.ReadAllText(Path.Combine(root,run.Id+".json")),new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            check(document.TelemetryVersion==1&&!File.ReadAllText(Path.Combine(root,run.Id+".json")).Contains("memoryUsedMiB")&&document.Samples.All(s=>s.Gpus.All(g=>g.Reading.MemoryUsedMiB==null))&&document.PeakVramMiB![gpu.Pci]>=1200,"Benchmark documents keep summary/context but no numeric GPU history");
            var legacy=run with{Id=Guid.NewGuid().ToString("N"),TelemetryVersion=0};var legacyPath=Path.Combine(root,legacy.Id+".json");var legacyJson=JsonSerializer.Serialize(legacy,new JsonSerializerOptions(JsonSerializerDefaults.Web));File.WriteAllText(legacyPath,legacyJson);
            var migrated=(await New().Get(legacy.Id))!;var reloaded=(await New().Get(legacy.Id))!;
            check(migrated.TelemetryVersion==1&&File.ReadAllText(legacyPath+".legacy")==legacyJson&&reloaded.Samples.SelectMany(s=>s.Gpus).Select(g=>g.Reading).SequenceEqual(legacy.Samples.SelectMany(s=>s.Gpus).Select(g=>g.Reading)),"Legacy benchmark traces migrate durably, retain their original and reload without JSON measurements");
            File.Delete(BenchmarkResults.PathFor(root,legacy.Id));
            check((await New(offline).Get(legacy.Id))!.Samples[0].Gpus[0].Reading==legacy.Samples[0].Gpus[0].Reading&&File.Exists(BenchmarkResults.PathFor(root,legacy.Id)),"Previously migrated metadata can rebuild its permanent artifact from the retained legacy original without VM");
            if(real==null)
            {
                var pending=legacy with{Id=Guid.NewGuid().ToString("N")};var pendingPath=Path.Combine(root,pending.Id+".json");var pendingJson=JsonSerializer.Serialize(pending,new JsonSerializerOptions(JsonSerializerDefaults.Web));File.WriteAllText(pendingPath,pendingJson);metricHandler.Offline=true;
                var fallback=(await New().Get(pending.Id))!;
                check(fallback.TelemetryVersion==0&&fallback.Error!.Contains("pending")&&fallback.Samples.Length==pending.Samples.Length&&File.ReadAllText(pendingPath)==pendingJson&&!File.Exists(pendingPath+".legacy"),"Backend outage leaves legacy benchmark history readable and unmodified for retry");
                check(File.Exists(BenchmarkResults.PathFor(root,pending.Id))&&(await New(offline).Get(pending.Id))!.Samples.Length==pending.Samples.Length,"Legacy result is permanently archived even when its VM migration is pending");
                metricHandler.Offline=false;await New().Migrate(default);
                check(JsonSerializer.Deserialize<LabRun>(File.ReadAllText(pendingPath),new JsonSerializerOptions(JsonSerializerDefaults.Web))!.TelemetryVersion==1,"Background VM migration still completes when a permanent result was already archived during an outage");
                File.Delete(pendingPath);File.Delete(BenchmarkResults.PathFor(root,pending.Id));
            }
            check((await New().Get(run.Id))?.Responses.Length==5&&New().List().Length==2,"Benchmark results survive service recreation");
            File.Delete(Path.Combine(root,run.Id+".json"));
            check((await New(offline).Get(run.Id))!.Responses.Length==5&&New(offline).List().Any(s=>s.Id==run.Id),"An archive alone supports saved-run lookup and listing without its index or VM");
            check(File.ReadAllBytes(archive).SequenceEqual(archiveBytes),"Viewing permanent results does not rewrite their archive");
            var expired=legacy with{Id=Guid.NewGuid().ToString("N"),Started=legacy.Started.AddYears(-2),Samples=legacy.Samples.Select(s=>s with{At=s.At.AddYears(-2),Gpus=s.Gpus.Select(g=>g with{Reading=g.Reading with{At=g.Reading.At.AddYears(-2)}}).ToArray()}).ToArray()};
            File.WriteAllText(Path.Combine(root,expired.Id+".json"),JsonSerializer.Serialize(expired,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            await New().Get(expired.Id);
            check((await New(offline).Get(expired.Id))!.Samples[0].Gpus[0].Reading==expired.Samples[0].Gpus[0].Reading,"Legacy samples outside VM retention remain complete in permanent results");
            mode="missing";var missing=await Finish((await lab.Start(new("1","hello",1,1,128,Warmup:false))).Id);
            check(ModelLab.Summary(missing).OutputTokensPerSecond==null,"Benchmark does not fabricate throughput without token counts");
            mode="error";var failed=await Finish((await lab.Start(new("1","hello",2,1,128,Warmup:false))).Id);
            check(failed.State=="Completed with errors"&&failed.Responses.All(r=>r.Error!=null),"Per-request engine failures remain visible in saved results");
            mode="normal";var cancel=await lab.Start(new("1","hello",64,1,128,Warmup:false));
            for(int n=0;n<100&&(await lab.Get(cancel.Id))!.Responses.Length==0;n++)await Task.Delay(10);
            lab.Cancel(cancel.Id);var cancelled=await Finish(cancel.Id);
            check(File.Exists(BenchmarkResults.PathFor(root,failed.Id))&&File.Exists(BenchmarkResults.PathFor(root,cancelled.Id)),"Failed and cancelled benchmarks also save permanent compressed results");
            check(cancelled.State=="Cancelled"&&cancelled.Responses.Length>0&&cancelled.Responses.Length<64&&maintenance.Active==0,"Cancellation stops generation while retaining completed requests and releasing update guard");
            mode="slow";using(var stop=new CancellationTokenSource(100))
            {var cancelledChat=false;try{await lab.Chat(new("1",[new("user","hello")]),(_,_)=>Task.CompletedTask,stop.Token);}catch(OperationCanceledException){cancelledChat=true;}check(cancelledChat,"Chat cancellation reaches the inference HTTP request");}
            var orphan=run with{Id=Guid.NewGuid().ToString("N"),State="Running",Finished=null};File.WriteAllText(Path.Combine(root,orphan.Id+".json"),JsonSerializer.Serialize(orphan,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            await (real??new VictoriaMetrics(metricHttp)).ImportGpu(orphan.Samples.SelectMany(s=>s.Gpus.Select(g=>VictoriaMetrics.Gpu(g.Device.Pci,g.Reading))),orphan.Id);
            check((await New().Get(orphan.Id))?.State=="Interrupted"&&File.Exists(BenchmarkResults.PathFor(root,orphan.Id)),"Restart recovery archives the available partial trace of interrupted benchmarks");
            mode="normal";var outageLab=New(offline);var outage=await outageLab.Start(new("1","hello",1,1,Warmup:false));
            for(var n=0;n<300&&maintenance.Active!=0;n++)await Task.Delay(20);
            var outageResult=(await New(offline).Get(outage.Id))!;
            check(maintenance.Active==0&&outageResult.Samples.Any(s=>s.Gpus.Any(g=>g.Reading.MemoryUsedMiB!=null))&&outageResult.Samples.Any(s=>s.Error?.Contains("telemetry storage")==true),"A VM outage does not prevent permanent storage of newly measured benchmark readings");
            mode="slow";var blockedLab=New(offline);var blocked=await blockedLab.Start(new("1","hello",1,1,Warmup:false));var blockedPath=BenchmarkResults.PathFor(root,blocked.Id);Directory.CreateDirectory(blockedPath);
            blockedLab.Cancel(blocked.Id);
            for(var n=0;n<300&&maintenance.Active!=0;n++)await Task.Delay(20);
            check((await blockedLab.Get(blocked.Id))!.Error!.Contains("Permanent result could not be saved"),"A failed permanent save is visible instead of claiming the result is durable");
            denied=false;try{await blockedLab.Start(new("1","hello",1,1,Warmup:false));}catch(InvalidOperationException){denied=true;}
            check(denied,"A new run cannot discard the previous result while its permanent save is failing");
            Directory.Delete(blockedPath);mode="normal";await blockedLab.Start(new("1","hello",1,1,Warmup:false));
            for(var n=0;n<300&&maintenance.Active!=0;n++)await Task.Delay(20);
            check(File.Exists(blockedPath)&&!BenchmarkResults.Read(root,blocked.Id).Error!.Contains("Permanent result could not be saved"),"Retry saves the retained result and clears the save-failure warning before another run");
            foreach(var invalid in new[]{new LabRequest("1","hello",0),new LabRequest("1","hello",2,3),new LabRequest("http://evil","hello"),new LabRequest("1","hello",Temperature:double.NaN)})
            {denied=false;try{ModelLab.Validate(invalid);}catch(InvalidOperationException){denied=true;}check(denied,"Benchmark validation rejects unsafe or unbounded input");}
            check(await lab.Get("../../outside")==null,"Benchmark exports cannot escape the results directory");
        }
        finally{await app.StopAsync();Directory.Delete(root,true);}
    }
}
