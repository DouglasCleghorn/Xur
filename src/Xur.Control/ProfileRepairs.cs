using Xur.Domain;
namespace Xur.Control;

public sealed partial class ProfileManager
{
    Profile RepairRecipes(Profile profile)=>profile with {Workloads=profile.Workloads.Select(w=>
    {
        var repaired=ModelLaunchSettings.RepairSaved(w.Recipe);
        if(repaired!=w.Recipe){catalog?.Verify(w.Recipe);catalog?.Verify(repaired);}
        return w with {Recipe=repaired};
    }).ToArray()};
    Profile SaveRepairs(Profile profile)
    {
        var repaired=RepairRecipes(profile);
        if(Canonical.Hash(repaired)==Canonical.Hash(profile))return profile;
        repaired=repaired with {Revision=profile.Revision+1};store.Save(repaired);return repaired;
    }
    async Task<Journal> RepairForResume(Journal journal)
    {
        var target=journal.Plan.Target;
        if(Canonical.Hash(RepairRecipes(target))==Canonical.Hash(target))return journal;
        if(store.Get<Profile>("profile",target.Id)?.Revision!=target.Revision)
            throw new InvalidOperationException("The profile changed. Cancel this operation and load its current revision.");
        var observed=await runtime.Observe();
        var repaired=RepairRecipes(target);ProfilePolicy.Validate(repaired,observed);await ValidateUsers(repaired);
        repaired=repaired with {Revision=target.Revision+1};
        // Replan from real instances: failed old containers must stop before a
        // corrected launch, while unrelated running workloads remain Keep.
        var plan=new ProfilePlan(Guid.NewGuid().ToString("N"),"",observed.Generation,Epoch,repaired,
            ProfilePolicy.Steps(repaired,observed,PeripheralHandoff(repaired,observed)),DateTimeOffset.UtcNow.AddMinutes(5));
        plan=plan with {Digest=Canonical.Hash(plan)};
        var next=new Journal(plan,observed,0,"Applying",null,DateTimeOffset.UtcNow);
        store.SaveRepairedTransition(repaired,next);return next;
    }
}
