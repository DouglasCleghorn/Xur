using Xur.Domain;
namespace Xur.Control;

public sealed partial class ProfileManager
{
    static string StationLabel(string name)
    {
        name=name.Trim();
        if(name.Length is 0 or >80 || name.Any(char.IsControl))throw new InvalidOperationException("Use a workstation name of 1–80 characters without control characters.");
        return name;
    }
    public async Task<StationDefinition> CreateStation(string name,StationUser user)
    {
        await gate.WaitAsync();try {
            Idle();name=StationLabel(name);
            if(!user.Temporary && (!ProfilePolicy.UserName(user.Username)||user.Uid<1000||user.Uid>=65534||users!=null&&!(await users()).Any(a=>a.Username==user.Username&&a.Uid==user.Uid)))
                throw new InvalidOperationException("Select an existing workstation user.");
            var station=new StationDefinition(store.Allocate("workload"),name,user.Temporary?new("temporary",0,true):user);
            store.Put("station",station.Id,station);return station;
        }finally{gate.Release();}
    }
    public async Task RenameStation(string id,string name)
    {
        await gate.WaitAsync();try {
            Idle();var station=store.Get<StationDefinition>("station",id)??throw new InvalidOperationException("Workstation not found.");
            store.Put("station",id,station with{Name=StationLabel(name)});
        }finally{gate.Release();}
    }
    public async Task DeleteStation(string id)
    {
        await gate.WaitAsync();try {
            Idle();if(store.Get<StationDefinition>("station",id)==null)throw new InvalidOperationException("Workstation not found.");
            if(store.List<Profile>("profile").Concat(store.List<Profile>("active")).Any(p=>p.Workloads.Any(w=>w.Id==id)) || (await runtime.Observe()).Instances.Any(w=>w.Id==id))
                throw new InvalidOperationException("Unload this workstation and remove it from its profiles before deleting it.");
            // Keep homes and pairing state on disk. IDs are never reused.
            store.Remove("station",id);
        }finally{gate.Release();}
    }
}
