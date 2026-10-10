using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xur.Domain;

public record Disk(string Path, string StablePath, string Serial, string Wwn, string Model,
    long Bytes, string Layout, string[] Mounts, string[] Blocked);
public record Inventory(string Generation, Disk[] Disks);
public record ScanMount(string Device, string Filesystem, string Options, bool BlockReadOnly, bool AnswerFound);
public record ScanResult(string State, string[] Answers, string[] Errors, string[] ReadOnlyDevices, ScanMount[] Mounts,string[]? Skipped=null);
public record InstallPlan(string Id, string Digest, string Generation, Disk Target, Disk[] Unaffected,
    string[] Actions, DateTimeOffset Expires);
public record Approval(string Id, string Digest);
public record InstallationProgress(int CompletedSteps,string CurrentStep,string Detail="");
public record Operation(string Id, string Stage, string Message, DateTimeOffset Updated,InstallationProgress? Progress=null);
public record ProcessResult(int ExitCode, string Output)
{
    // Output remains the combined diagnostic text for existing callers. Machine
    // readers must use stdout without treating command warnings as response data.
    [JsonIgnore] public string StandardOutput { get; init; } = Output;
    [JsonIgnore] public string StandardError { get; init; } = "";
}

public static class Canonical
{
    public static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(value)));
}

public static class Processes
{
    public static async Task<ProcessResult> Run(string executable, IEnumerable<string> args,
        int seconds = 30, CancellationToken cancellation = default)
    {
        var result = await Xur.IO.CommandRunner.Run(executable, args, seconds, cancellation);
        var stdout=Encoding.UTF8.GetString(result.Output);var stderr=Encoding.UTF8.GetString(result.Error);
        return new(result.ExitCode,stdout+stderr){StandardOutput=stdout,StandardError=stderr};
    }
}

public static class LocalClient
{
    public static HttpClient Create(string socket) => Xur.IO.HttpClients.Unix(socket, TimeSpan.FromMinutes(3));
}

public interface IStorageLayout
{
    string[] Actions { get; }
    string Kickstart(Disk disk);
}

public sealed class PlainRootLayout : IStorageLayout
{
    public string[] Actions => ["Erase selected disk partition table and all contents",
        "Create GPT", "Create 600 MiB FAT32 EFI system partition",
        "Create 1024 MiB ext4 /boot", "Create Btrfs root using remaining space", "Install sealed Xur bootc payload"];
    public string Kickstart(Disk disk)
    {
        // lsblk names are re-observed, but still reject any Kickstart metacharacter.
        var name = disk.Path.StartsWith("/dev/", StringComparison.Ordinal) ? disk.Path[5..] : "";
        if (name.Length == 0 || name.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new InvalidOperationException("Invalid whole-disk path");
        return $"ignoredisk --only-use={name}\nclearpart --all --initlabel --disklabel=gpt --drives={name}\n" +
            $"part /boot/efi --fstype=efi --size=600 --ondisk={name}\n" +
            $"part /boot --fstype=ext4 --size=1024 --ondisk={name}\n" +
            $"part btrfs.01 --fstype=btrfs --size=8192 --grow --ondisk={name}\n" +
            "btrfs / --label=xur-system btrfs.01\n" +
            $"bootloader --boot-drive={name}\n";
    }
}

public record SystemSnapshot(double CpuPercent,long MemoryUsedBytes,long MemoryTotalBytes,long DiskUsedBytes,long DiskTotalBytes,double UptimeSeconds,ServiceState[] Services);
public record ServiceState(string Name,string Description,string State,bool Managed);
public record ServiceAction(string Name,string Action);
