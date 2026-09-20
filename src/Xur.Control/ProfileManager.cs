using Xur.Domain;
namespace Xur.Control;

public sealed partial class ProfileManager(ProfileStore store,IWorkloadRuntime runtime,IWorkloadGateway gateway,RecipeCatalog? catalog=null,Func<Task<StationAccount[]>>? users=null)
{
    readonly SemaphoreSlim gate=new(1,1);
    Task? worker;
    string Epoch=>store.Get<string>("epoch","current") ?? "empty";
    static Transition? View(Journal? j)
    {
        if(j==null)return null;
        TransitionStep Describe(TransitionStep s)=>s with {Description=(s.WorkloadId.Length==0?"":(j.Plan.Target.Workloads.FirstOrDefault(w=>w.Id==s.WorkloadId)?.Name??s.WorkloadId)+": ")+s.Description};
        var done=j.Done().ToHashSet();
        var current=j.RunningSteps is {Length:>0}?j.RunningSteps:Enumerable.Range(0,j.Plan.Steps.Length).Where(i=>!done.Contains(i)).Take(1).ToArray();
        return new(j.Plan.Id,j.Plan.Target.Name,j.Stage,done.Count,j.Plan.Steps.Length,j.Error,j.Updated,j.Plan.Unload,
            j.Plan.Steps.Where((s,i)=>done.Contains(i)).Select(Describe).ToArray(),
            current.Length>0?string.Join("; ",current.Select(i=>Describe(j.Plan.Steps[i]).Description)):null);
    }
    void Idle()
    {
        if(worker is {IsCompleted:false} || store.Get<Journal>("journal","current") is { Stage:not ("Complete" or "Cancelled") })
            throw new InvalidOperationException("Finish or resume the current profile change first.");
    }
    async Task ValidateUsers(Profile profile)
    {
        var selected=profile.Workloads.Where(w=>w.User is {Temporary:false}).Select(w=>w.User!).ToArray();
        if(users==null || selected.Length==0)return;
        var available=await users();
        if(selected.Any(u=>!available.Any(a=>a.Username==u.Username&&a.Uid==u.Uid)))throw new InvalidOperationException("A workstation user changed or is unavailable. Select the user again.");
    }
    Profile StationNames(Profile profile)=>profile with {Workloads=profile.Workloads.Select(w=>w.Recipe.Kind=="Workstation" && store.Get<StationDefinition>("station",w.Id) is {} station?w with{Name=station.Name}:w).ToArray()};
    public async Task<StationDefinition[]> Stations()
    {await gate.WaitAsync();try{return store.List<StationDefinition>("station");}finally{gate.Release();}}
    public bool UpdateBusy => worker is {IsCompleted:false};
    public async Task<Profile[]> ExportProfiles()
    {
        await gate.WaitAsync();try{return store.List<Profile>("profile");}finally{gate.Release();}
    }
    public async Task<ProfileState> State()
    {
        await gate.WaitAsync();try { return new(store.List<Profile>("profile").Select(StationNames).ToArray(),store.Get<Profile>("active","current") is {} active?StationNames(active):null,await runtime.Observe(),View(store.Get<Journal>("journal","current"))); }
        finally { gate.Release(); }
    }
    public async Task<Profile> Save(Profile profile)
    {
        await gate.WaitAsync();try
        {
            Idle();profile=RepairRecipes(profile);if(store.Get<Profile>("deleted-profile",profile.Id)!=null)throw new InvalidOperationException("This profile was deleted. Create a new profile.");foreach(var w in profile.Workloads)catalog?.Verify(w.Recipe);var current=store.Get<Profile>("profile",profile.Id);
            if(profile.Revision!=(current?.Revision ?? 0))throw new InvalidOperationException("This profile changed. Reload before saving.");
            ProfilePolicy.Validate(profile,await runtime.Observe());await ValidateUsers(profile);
            var next=profile with {Revision=profile.Revision+1};store.Save(next);return next;
        } finally {gate.Release();}
    }
    public async Task<Profile> Create(string? copy=null)
    {
        await gate.WaitAsync();try
        {
            Idle();
            var source=string.IsNullOrEmpty(copy)?null:store.Get<Profile>("profile",copy) ?? throw new InvalidOperationException("Profile not found.");
            var id=store.Allocate("profile");
            var profile=new Profile(id,"Profile "+id,1,source?.Workloads ?? []);
            store.Save(profile);return profile;
        }finally{gate.Release();}
    }
    public async Task<Profile> SaveSelection(string id,long revision,WorkloadSelection[] selections,string? name=null)
    {
        await gate.WaitAsync();try
        {
            Idle();
            var current=store.Get<Profile>("profile",id) ?? throw new InvalidOperationException("Profile not found.");
            if(current.Revision!=revision)throw new InvalidOperationException("This profile changed. Reload before saving.");
            if(selections.Length>32)throw new InvalidOperationException("Use at most 32 workloads.");
            var known=(store.Get<Profile>("active","current")?.Workloads ?? []).Concat(store.List<Profile>("profile").SelectMany(p=>p.Workloads)).ToArray();
            var definitions=new List<StationDefinition>();var workloads=new List<Workload>();var used=new HashSet<string>();
            foreach(var selection in selections)
            {
                if(string.IsNullOrEmpty(selection.Recipe))continue;
                var recipe=catalog?.Recipes.SingleOrDefault(r=>r.Id==selection.Recipe) ?? throw new InvalidOperationException("Select a recipe.");
                var old=current.Workloads.SingleOrDefault(w=>w.Id==selection.Id);
                if(!string.IsNullOrEmpty(selection.Id) && old==null)throw new InvalidOperationException("Reload the workload selection.");
                if(old!=null && old.Recipe.Kind!=recipe.Kind)old=null;
                StationDefinition? station=null;
                if(recipe.Kind=="Workstation" && !string.IsNullOrEmpty(selection.StationId))
                    station=store.Get<StationDefinition>("station",selection.StationId) ?? throw new InvalidOperationException("Workstation identity not found.");
                var user=station?.User ?? (recipe.Kind=="Workstation"?selection.User:null);
                if(station!=null)user=station.User;
                // An explicit new identity must not borrow another station's pairing.
                if(recipe.Kind=="Workstation" && selection.StationId!=null)old=null;
                if(recipe.Kind=="Workstation" && user==null && station==null && (old==null || old.Recipe.Kind!="Workstation" || old.User!=null))throw new InvalidOperationException("Select a workstation user.");
                var candidate=new Workload("",recipe.Name,recipe,selection.Gpus,"",user,recipe.Kind=="Workstation"?selection.Devices:null);
                // Selecting the same runtime in another profile keeps its exact
                // identity/allocation/route, rather than creating a second model.
                if(recipe.Kind!="Workstation" || selection.StationId==null)old??=known.FirstOrDefault(w=>w.Fingerprint==candidate.Fingerprint && !used.Contains(w.Id));
                var key=station?.Id ?? old?.Id ?? store.Allocate("workload");
                if(recipe.Kind=="Workstation") {
                    var label=station?.Name ?? selection.StationName?.Trim();
                    if(string.IsNullOrEmpty(label))label=station?.Name ?? old?.Name ?? "Workstation "+key;
                    if(label.Length>80 || label.Any(char.IsControl))throw new InvalidOperationException("Use a workstation name of at most 80 characters without control characters.");
                    definitions.Add(new(key,label,user));candidate=candidate with{Name=label};
                }
                if(!used.Add(key))throw new InvalidOperationException("Select each workload once.");
                workloads.Add(candidate with {Id=key,Route=old?.Route ?? "workload-"+key});
            }
            var next=current with {Name=name?.Trim()??current.Name,Revision=current.Revision+1,Workloads=workloads.ToArray()};
            ProfilePolicy.Validate(next,await runtime.Observe());await ValidateUsers(next);store.Save(next,definitions.ToArray());return next;
        }finally{gate.Release();}
    }
    bool PeripheralHandoff(Profile target,RuntimeObservation observed)
    {
        var known=store.List<Profile>("profile").Concat(store.List<Profile>("revision")).SelectMany(p=>p.Workloads).ToArray();
        var previous=observed.Instances.Select(i=>known.FirstOrDefault(w=>w.Id==i.Id&&(w.Fingerprint==i.Fingerprint||"legacy-seat:"+w.Fingerprint==i.Fingerprint))).Where(w=>w?.Recipe.Kind=="Workstation").Cast<Workload>().ToArray();
        static string Signature(IEnumerable<Workload> workloads)
        {
            var stations=workloads.Where(w=>w.Recipe.Kind=="Workstation").OrderBy(w=>w.Id).ToArray();
            var primary=stations.SingleOrDefault(w=>w.Devices?.Primary==true)?.Id??(stations.Length==1?stations[0].Id:null);
            return Canonical.Hash(new{Primary=primary,Claims=stations.Where(w=>w.Devices?.Usb is {Length:>0}).Select(w=>new{w.Id,Usb=w.Devices!.Usb!.Order().ToArray()}).ToArray()});
        }
        return previous.Length>0&&Signature(previous)!=Signature(target.Workloads);
    }
    public async Task<ProfilePlan> Preview(string id)
    {
        await gate.WaitAsync();try
        {
            Idle();var target=store.Get<Profile>("profile",id) ?? throw new InvalidOperationException("Profile not found.");
            target=StationNames(SaveRepairs(target));
            var observed=await runtime.Observe();ProfilePolicy.Validate(target,observed);await ValidateUsers(target);
            var plan=new ProfilePlan(Guid.NewGuid().ToString("N"),"",observed.Generation,Epoch,target,ProfilePolicy.Steps(target,observed,PeripheralHandoff(target,observed)),DateTimeOffset.UtcNow.AddMinutes(5));
            plan=plan with {Digest=Canonical.Hash(plan)};store.Put("plan",plan.Id,plan);return plan;
        } finally {gate.Release();}
    }
    public async Task Delete(string id,long revision)
    {
        await gate.WaitAsync();try
        {
            Idle();var profile=store.Get<Profile>("profile",id) ?? throw new InvalidOperationException("Profile not found.");
            if(profile.Revision!=revision)throw new InvalidOperationException("This profile changed. Reload before deleting.");
            if(store.Get<Profile>("active","current")?.Id==id)throw new InvalidOperationException("Unload this profile before deleting it.");
            // Saved definitions only. Runtime ownership and model/user data are untouched.
            store.DeleteProfile(profile);
        }finally{gate.Release();}
    }
    public async Task<ProfilePlan> PreviewUnload()
    {
        await gate.WaitAsync();try
        {
            Idle();var active=store.Get<Profile>("active","current");var observed=await runtime.Observe();
            if(active==null && observed.Instances.Length==0)throw new InvalidOperationException("No profile is loaded.");
            var target=(active ?? new Profile("unload","Running workloads",0,[])) with {Workloads=[]};
            var plan=new ProfilePlan(Guid.NewGuid().ToString("N"),"",observed.Generation,Epoch,target,ProfilePolicy.Steps(target,observed),DateTimeOffset.UtcNow.AddMinutes(5),Unload:true);
            plan=plan with {Digest=Canonical.Hash(plan)};store.Put("plan",plan.Id,plan);return plan;
        }finally{gate.Release();}
    }
    bool TargetMatches(ProfilePlan plan)
    {
        if(!plan.Unload)return store.Get<Profile>("profile",plan.Target.Id)?.Revision==plan.Target.Revision;
        var active=store.Get<Profile>("active","current");
        return active==null ? plan.Target.Id=="unload" && plan.Target.Revision==0 : active.Id==plan.Target.Id && active.Revision==plan.Target.Revision;
    }
    public async Task<ProfilePlan?> Plan(string id)
    {await gate.WaitAsync();try{return store.Get<ProfilePlan>("plan",id);}finally{gate.Release();}}
    public async Task<Transition> Apply(Approval approval)
    {
        await gate.WaitAsync();try
        {
            Idle();var plan=store.Get<ProfilePlan>("plan",approval.Id) ?? throw new InvalidOperationException("Review the profile again.");
            if(plan.Digest!=approval.Digest || plan.Expires<=DateTimeOffset.UtcNow || plan.SourceEpoch!=Epoch || !TargetMatches(plan))
                throw new InvalidOperationException("The preview changed or expired. Review the profile again.");
            var now=await runtime.Observe();
            if(now.Generation!=plan.ObservationGeneration)throw new InvalidOperationException("Workloads or GPU inventory changed. Review again.");
            ProfilePolicy.Validate(plan.Target,now);await ValidateUsers(plan.Target);
            var journal=new Journal(plan,now,0,"Applying",null,DateTimeOffset.UtcNow);store.Put("journal","current",journal);
            worker=Task.Run(Run);return View(journal)!;
        }finally{gate.Release();}
    }
    public async Task Resume(bool automatic=false)
    {
        await gate.WaitAsync();try
        {
            var j=store.Get<Journal>("journal","current");
            if(j==null || j.Stage is "Complete" or "Cancelled" || worker is {IsCompleted:false} || automatic && j.Stage=="Failed")return;
            if(j.Stage!="Cancelling" && !j.Plan.Unload)j=await RepairForResume(j);
            store.Put("journal","current",j with {Stage=j.Stage=="Cancelling"?"Cancelling":"Applying",Error=null,RunningSteps=[],Updated=DateTimeOffset.UtcNow});worker=Task.Run(Run);
        }finally{gate.Release();}
    }
    public async Task Cancel(string? operationId=null)
    {
        await gate.WaitAsync();try
        {
            var j=store.Get<Journal>("journal","current");
            if(j==null || operationId!=null && j.Plan.Id!=operationId)throw new InvalidOperationException("This profile change is no longer current. Refresh the page.");
            if(j.Stage is "Cancelled" or "Cancelling")return;
            if(j.Stage=="Complete")throw new InvalidOperationException("This profile change has already completed.");
            if(worker is {IsCompleted:false})
                store.Put("journal","current",j with {Stage="Cancelling",Updated=DateTimeOffset.UtcNow});
            else store.Cancel(j with {Stage="Cancelled",Updated=DateTimeOffset.UtcNow});
        }finally{gate.Release();}
    }
    public async Task Wait() {var task=worker;if(task!=null)await task;}
    async Task Run()
    {
        await gate.WaitAsync();bool unload;
        try{unload=store.Get<Journal>("journal","current")!.Plan.Unload;}finally{gate.Release();}
        if(unload){await RunUnload();return;}
        await RunLoad();
    }
}
public record WorkloadSelection(string? Id,string Recipe,string[] Gpus,StationUser? User=null,string? StationId=null,string? StationName=null,StationDevices? Devices=null);

public sealed class AgentWorkloadRuntime(HttpClient client):IWorkloadRuntime
{
    public async Task Prepare(Workload[] workloads)=>await Ensure(await client.PostAsJsonAsync("/workloads/prepare",workloads));
    public async Task<RuntimeObservation> Observe()=>await client.GetFromJsonAsync<RuntimeObservation>("/workloads") ?? throw new IOException();
    public async Task<RuntimeInstance> Start(Workload w)
    {var r=await client.PostAsJsonAsync("/workloads/start",new RuntimeStart(w));await Ensure(r);return (await r.Content.ReadFromJsonAsync<RuntimeInstance>())!;}
    public async Task Stop(RuntimeStop request)=>await Ensure(await client.PostAsJsonAsync("/workloads/stop",request));
    static async Task Ensure(HttpResponseMessage r)
    {if(!r.IsSuccessStatusCode)
        {
            var text=await r.Content.ReadAsStringAsync();
            try {using var doc=System.Text.Json.JsonDocument.Parse(text);text=doc.RootElement.GetProperty("error").GetString() ?? text;}catch(System.Text.Json.JsonException){}catch(KeyNotFoundException){}
            throw new InvalidOperationException(text);
        }}
}
public sealed class LocalWorkloadGateway(HttpClient client):IWorkloadGateway
{
    public async Task Drain(string id)
    {var r=await client.PostAsJsonAsync("/drain",new {id});if(!r.IsSuccessStatusCode)throw new InvalidOperationException("Active requests have not finished draining. Resume after they finish.");}
    public async Task Publish(BackendRoute[] routes)
    {var r=await client.PostAsJsonAsync("/routes",routes);r.EnsureSuccessStatusCode();}
}
