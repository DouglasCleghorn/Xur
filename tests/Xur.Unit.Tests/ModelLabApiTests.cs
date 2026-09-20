using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xur.Control;
using Xur.Domain;
static class ModelLabApiTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var directory=Path.Combine(Path.GetTempPath(),"xur-lab-api-"+Guid.NewGuid().ToString("N"));
        var recipe=new Recipe("api-model","API test","image",[],8080,"/health","CPU",0,0,"",Engine:"llama.cpp");
        var workload=new Workload("1","API model",recipe,[],"api");var instance=new RuntimeInstance("1",workload.Fingerprint,"first",1,"boot","http://127.0.0.1:19000","running",[]);
        var profile=new Profile("1","Test",1,[workload]);var state=new ProfileState([profile],profile,new("1",[],[instance]),null);
        using var engine=new HttpClient(new Handler()){BaseAddress=new Uri("http://localhost")};
        var lab=new ModelLab(directory,()=>Task.FromResult(state),()=>Task.FromResult(new[]{new BackendRoute("api","1",instance.Endpoint)}),_=>Task.FromResult(new GpuTelemetrySnapshot(DateTimeOffset.UtcNow,[])),new(engine),new());
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));
        await using var app=builder.Build();app.MapModelLab(lab,false);await app.StartAsync();using var http=new HttpClient{BaseAddress=new Uri(app.Urls.Single())};
        try
        {
            var targets=await http.GetFromJsonAsync<LabTarget[]>("/api/model-lab/targets");check(targets is {Length:1},"Model lab API lists only ready published text model targets");
            var invalid=await http.PostAsJsonAsync("/api/benchmarks",new LabRequest("1","hello",Concurrency:9));check(invalid.StatusCode==HttpStatusCode.Conflict,"Benchmark API returns validation failures without starting a job");
            var response=await http.PostAsJsonAsync("/api/benchmarks",new LabRequest("1","hello",1,1,Warmup:false));var run=(await response.Content.ReadFromJsonAsync<LabRun>())!;
            check(response.StatusCode==HttpStatusCode.Accepted,"Benchmark API acknowledges durable background work");
            for(var i=0;i<100&&lab.Get(run.Id)?.State=="Running";i++)await Task.Delay(10);
            var list=await http.GetFromJsonAsync<LabSummary[]>("/api/benchmarks");check(list?.Single().State=="Completed","Benchmark API exposes completed saved history");
            using var export=await http.GetAsync("/api/benchmarks/"+run.Id+"/export");using var content=JsonDocument.Parse(await export.Content.ReadAsStringAsync());
            check(export.Content.Headers.ContentDisposition?.DispositionType=="attachment"&&content.RootElement.GetProperty("run").GetProperty("settings").GetProperty("prompt").GetString()=="hello","JSON export is a downloadable artifact with original benchmark settings");
            var reply=await http.PostAsJsonAsync("/api/model-lab/chat",new LabChatRequest("1",[new("user","hello")]));var events=(await reply.Content.ReadAsStringAsync()).Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(x=>JsonDocument.Parse(x)).ToArray();
            try{check(reply.Content.Headers.ContentType?.MediaType=="application/x-ndjson"&&events[0].RootElement.GetProperty("type").GetString()=="delta"&&events[^1].RootElement.GetProperty("type").GetString()=="result","Chat API streams text updates followed by measured results");}finally{foreach(var e in events)e.Dispose();}
            check((await http.GetAsync("/api/benchmarks/not-an-id/export")).StatusCode==HttpStatusCode.NotFound,"Unknown benchmark exports return 404");
        }
        finally{await app.StopAsync();Directory.Delete(directory,true);}
    }
    sealed class Handler:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}\n\ndata: [DONE]\n\n",System.Text.Encoding.UTF8,"text/event-stream")});
    }
}
