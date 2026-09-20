using Xur.Domain;
namespace Xur.Control;
public record GpuAssignedWorkload(string Id,string Name,string Engine,string State,int Pid);
public record GpuCardView(GpuTelemetry Telemetry,GpuAssignedWorkload[] Workloads);
public record GpuStatusView(DateTimeOffset? CapturedAt,GpuCardView[] Cards,string? Error,GpuTopology? Topology=null)
{
    public static async Task<GpuStatusView> Observe(Appliance appliance,ProfileManager profiles,int minutes=15)
    {
        var status=await appliance.Agent.GetFromJsonAsync<GpuTelemetrySnapshot>("/gpu-telemetry?minutes="+(minutes is 15 or 60 or 1440?minutes:15)) ?? throw new IOException();
        var state=await profiles.State();var plan=state.Operation==null?null:await profiles.Plan(state.Operation.Id);
        var definitions=(plan?.Target.Workloads??[]).Concat(state.Active?.Workloads??[]).Concat(state.Profiles.SelectMany(p=>p.Workloads)).ToArray();
        return new(status.CapturedAt,status.Gpus.Select(g=>new GpuCardView(g,state.Runtime.Instances.Where(i=>i.Gpus.Contains(g.Device.Pci)).Select(i=>{
            var w=definitions.FirstOrDefault(w=>w.Id==i.Id&&w.Fingerprint==i.Fingerprint);return new GpuAssignedWorkload(i.Id,w?.Recipe.Name??"Workload "+i.Id,w?.Recipe.Engine??"",i.State,i.Pid);
        }).ToArray())).ToArray(),status.Error,status.Topology);
    }
}
