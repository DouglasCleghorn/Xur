using Xur.Domain;

namespace Xur.Control;

public static class ProfileDisplay
{
    public static IEnumerable<Profile> Ordered(ProfileState state) =>
        state.Profiles.OrderByDescending(p => p.Id == state.Active?.Id).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase);

    public static string RuntimeLabel(ProfileState state)
    {
        if (state.Active is not {} active)
            return state.Runtime.Instances.Length == 0 ? "Idle" : "Partially running";
        var instances = state.Runtime.Instances;
        return instances.Length == active.Workloads.Length && active.Workloads.All(w =>
            instances.Any(i => i.Id == w.Id && i.Fingerprint == w.Fingerprint && i.State == "running"))
            ? "Loaded" : "Partially running";
    }

    public static string Devices(Workload workload, RuntimeObservation runtime) =>
        T3CodeWorkload.Is(workload.Recipe) ? "All GPUs (shared)" : workload.Gpus.Length == 0 ? "CPU" :
        string.Join(", ", workload.Gpus.Select(id => runtime.Gpus.FirstOrDefault(g => g.Pci == id)?.DisplayName ?? id));

    public static string Name(Workload workload) => string.IsNullOrWhiteSpace(workload.Name) ? workload.Recipe.Name : workload.Name;
}
