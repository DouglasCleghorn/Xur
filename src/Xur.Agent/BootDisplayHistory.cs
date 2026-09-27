using System.Text.Json.Nodes;
namespace Xur.Agent;

// Bounded, volatile samples help compare the initial HDMI state with a later replug.
public sealed class BootDisplayHistory
{
    readonly object gate=new();
    readonly Queue<JsonNode> samples=[];
    public JsonNode[] Read(){lock(gate)return samples.Select(s=>s.DeepClone()).ToArray();}
    public async Task Run(CancellationToken stopping)
    {
        int count=0;
        while(!stopping.IsCancellationRequested)
        {
            try
            {
                var observed=await DisplayDiagnostics.Collect(runCommands:false);
                var sample=new JsonObject();
                foreach(var key in new[]{"capturedAt","kernel","drm","framebuffer","pci","modules","errors"})sample[key]=observed[key]?.DeepClone();
                lock(gate){samples.Enqueue(sample);while(samples.Count>120)samples.Dequeue();}
                await Task.Delay(TimeSpan.FromSeconds(count++<24?5:15),stopping);
            }
            catch(OperationCanceledException) when(stopping.IsCancellationRequested){break;}
            catch{try{await Task.Delay(15000,stopping);}catch(OperationCanceledException){break;}}
        }
    }
}
