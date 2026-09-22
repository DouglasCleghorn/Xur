using System.Text.Json;
using Xur.Domain;
namespace Xur.Control;
public static class ModelLabEndpoints
{
    public static void MapModelLab(this WebApplication app,Appliance device,ProfileManager profiles,HttpClient gateway,HttpClient admin,ApplicationMaintenance maintenance)
    {
        var lab=new ModelLab(Path.Combine(device.StateDirectory,"benchmarks"),profiles.State,
            async()=>await admin.GetFromJsonAsync<BackendRoute[]>("/routes")??[],
            async ct=>await device.Agent.GetFromJsonAsync<GpuTelemetrySnapshot>("/gpu-telemetry/sample",ct)??new(null,[],"GPU telemetry unavailable."),new(gateway),maintenance,app.Lifetime.ApplicationStopping);
        app.MapModelLab(lab,device.Installer);
    }
    public static void MapModelLab(this WebApplication app,ModelLab lab,bool installer)
    {
        async Task<IResult> Safe(Func<Task<IResult>> action)
        {
            if(installer)return Results.Conflict(new{error="Model tools are available after installation."});
            try{return await action();}
            catch(Exception e)when(e is InvalidOperationException or HttpRequestException or IOException or JsonException or TimeoutException)
            {return Results.Conflict(new{error=e is InvalidOperationException?e.Message:"Model tools are temporarily unavailable."});}
        }
        app.MapGet("/api/model-lab/targets",()=>Safe(async()=>Results.Json(await lab.Targets())));
        app.MapGet("/api/benchmarks",()=>Safe(()=>Task.FromResult<IResult>(Results.Json(lab.List()))));
        app.MapGet("/api/benchmarks/{id}",(string id)=>Safe(()=>Task.FromResult<IResult>(lab.Get(id) is {} run?Results.Json(new{run,summary=ModelLab.Summary(run)}):Results.NotFound())));
        app.MapGet("/api/benchmarks/{id}/export",(string id)=>Safe(()=>Task.FromResult<IResult>(lab.Get(id) is {} run?Results.File(JsonSerializer.SerializeToUtf8Bytes(new{schema=1,run,summary=ModelLab.Summary(run)},new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true}),"application/json","benchmark-"+run.Id+".json"):Results.NotFound())));
        app.MapPost("/api/benchmarks",(LabRequest request)=>Safe(async()=>Results.Json(await lab.Start(request),statusCode:202)));
        app.MapPost("/api/benchmarks/{id}/cancel",(string id)=>Safe(()=>{lab.Cancel(id);return Task.FromResult<IResult>(Results.Accepted());}));
        app.MapPost("/api/model-lab/chat",async(HttpContext c,LabChatRequest request)=>
        {
            if(installer){c.Response.StatusCode=409;return;}
            async Task Send(object value)
            {await c.Response.WriteAsync(JsonSerializer.Serialize(value,new JsonSerializerOptions(JsonSerializerDefaults.Web))+"\n",c.RequestAborted);await c.Response.Body.FlushAsync(c.RequestAborted);}
            try
            {
                ModelLab.Validate(request);c.Response.ContentType="application/x-ndjson";
                var result=await lab.Chat(request,(text,reasoning)=>Send(new{type="delta",text,reasoning}),c.RequestAborted);
                await Send(new{type="result",result});
            }
            catch(OperationCanceledException)when(c.RequestAborted.IsCancellationRequested){}
            catch(Exception e)when(e is InvalidOperationException or HttpRequestException or IOException or JsonException or TimeoutException)
            {
                if(!c.Response.HasStarted)c.Response.ContentType="application/x-ndjson";
                if(!c.RequestAborted.IsCancellationRequested)await Send(new{type="error",error=e is InvalidOperationException?e.Message:"The model request failed."});
            }
        });
    }
}
