using Xur.Domain;
namespace Xur.Control;

public sealed partial class ProfileManager
{
    public async Task<ConfigurationSnapshot> BackupConfiguration(string destination)
    {
        await gate.WaitAsync();
        try
        {
            if(worker is {IsCompleted:false})throw new InvalidOperationException("Wait for the profile change to finish before taking a backup.");
            store.Backup(destination);
            return new(store.List<Profile>("profile"),store.List<StationDefinition>("station"));
        }
        finally { gate.Release(); }
    }
}
public record ConfigurationSnapshot(Profile[] Profiles,StationDefinition[] Workstations);
