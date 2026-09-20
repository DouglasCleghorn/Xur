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
    public static async Task Run(Action<bool,string> check)
    {
        var recipe=new Recipe("test-model","Test model","registry/model@sha256:"+new string('a',64),["--max-num-seqs","1"],8080,"/health","NVIDIA",1,1,"",Engine:"vLLM",Hub:new("org/test",new string('b',40),"Apache-2.0"));
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
            ModelLab New()=>new(root,()=>Task.FromResult(state),Routes,Sample,client,maintenance);
            var lab=New();var run=await lab.Start(new("1","hello",4,2,128));
            var denied=false;try{await lab.Start(new("1","hello"));}catch(InvalidOperationException){denied=true;}
            check(denied,"Only one benchmark may run at a time");
            async Task<LabRun> Finish(string id)
            {
                for(int n=0;n<300;n++){var current=lab.Get(id)!;if(current.State is not ("Running" or "Cancelling")&&maintenance.Active==0)return current;await Task.Delay(20);}throw new Exception("Benchmark did not finish");
            }
            run=await Finish(run.Id);var summary=ModelLab.Summary(run);
            check(run.State=="Completed"&&calls==5&&peak==2&&run.Responses.Count(r=>r.Warmup)==1&&summary.Completed==4,"Benchmark runs requested concurrency and excludes warm-up from results");
            check(summary.OutputTokensPerSecond>0&&summary.PeakVramMiB[gpu.Pci]>=1200&&run.Samples.Length>=2&&run.Target.Workload.Recipe.Hub?.Revision==new string('b',40),"Saved benchmark retains throughput, sampled peak VRAM and pinned model revision");
            check(run.Context.Workloads.Length==2&&run.Samples.All(s=>s.Workloads?.Length==2)&&!File.ReadAllText(Path.Combine(root,run.Id+".json")).Contains("do-not-export"),"Run snapshots include competing workloads without exporting container secrets");
            check(New().Get(run.Id)?.Responses.Length==5&&New().List().Length==1,"Benchmark results survive service recreation");
            mode="missing";var missing=await Finish((await lab.Start(new("1","hello",1,1,128,Warmup:false))).Id);
            check(ModelLab.Summary(missing).OutputTokensPerSecond==null,"Benchmark does not fabricate throughput without token counts");
            mode="error";var failed=await Finish((await lab.Start(new("1","hello",2,1,128,Warmup:false))).Id);
            check(failed.State=="Completed with errors"&&failed.Responses.All(r=>r.Error!=null),"Per-request engine failures remain visible in saved results");
            mode="normal";var cancel=await lab.Start(new("1","hello",64,1,128,Warmup:false));
            for(int n=0;n<100&&lab.Get(cancel.Id)!.Responses.Length==0;n++)await Task.Delay(10);
            lab.Cancel(cancel.Id);var cancelled=await Finish(cancel.Id);
            check(cancelled.State=="Cancelled"&&cancelled.Responses.Length>0&&cancelled.Responses.Length<64&&maintenance.Active==0,"Cancellation stops generation while retaining completed requests and releasing update guard");
            mode="slow";using(var stop=new CancellationTokenSource(100))
            {var cancelledChat=false;try{await lab.Chat(new("1",[new("user","hello")]),(_,_)=>Task.CompletedTask,stop.Token);}catch(OperationCanceledException){cancelledChat=true;}check(cancelledChat,"Chat cancellation reaches the inference HTTP request");}
            var orphan=run with{Id=Guid.NewGuid().ToString("N"),State="Running",Finished=null};File.WriteAllText(Path.Combine(root,orphan.Id+".json"),JsonSerializer.Serialize(orphan,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            check(New().Get(orphan.Id)?.State=="Interrupted","Restart recovery marks interrupted benchmarks while preserving their context and results");
            foreach(var invalid in new[]{new LabRequest("1","hello",0),new LabRequest("1","hello",2,3),new LabRequest("http://evil","hello"),new LabRequest("1","hello",Temperature:double.NaN)})
            {denied=false;try{ModelLab.Validate(invalid);}catch(InvalidOperationException){denied=true;}check(denied,"Benchmark validation rejects unsafe or unbounded input");}
            check(lab.Get("../../outside")==null,"Benchmark exports cannot escape the results directory");
        }
        finally{await app.StopAsync();Directory.Delete(root,true);}
    }
}
