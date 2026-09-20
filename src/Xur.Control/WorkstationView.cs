using Xur.Domain;
namespace Xur.Control;
public record WorkstationProfile(string Id,string Name);
public record WorkstationView(Workload Workload,GpuDevice? Gpu,string State,StationStreamStatus? Stream,WorkstationProfile[] Profiles)
{
    public static WorkstationView[] Build(ProfileState state,StationStreamStatus[] streams)
    {
        var all=state.Profiles.Concat(state.Active==null?[]:[state.Active]).ToArray();
        return all.SelectMany(p=>p.Workloads).Where(w=>w.Recipe.Kind=="Workstation").GroupBy(w=>w.Id).Select(group=>{
            var w=group.FirstOrDefault(w=>state.Runtime.Instances.Any(i=>i.Id==w.Id&&i.Fingerprint==w.Fingerprint)) ?? group.First();var running=state.Runtime.Instances.FirstOrDefault(i=>i.Id==w.Id&&i.Fingerprint==w.Fingerprint);
            return new WorkstationView(w,state.Runtime.Gpus.FirstOrDefault(g=>w.Gpus.Contains(g.Pci)),running?.State??"Stopped",running==null?null:streams.FirstOrDefault(s=>s.Id==w.Id),all.Where(p=>p.Workloads.Any(x=>x.Id==w.Id)).DistinctBy(p=>p.Id).Select(p=>new WorkstationProfile(p.Id,p.Name)).ToArray());
        }).OrderByDescending(w=>w.State=="running").ThenBy(w=>w.Workload.Id).ToArray();
    }
}
