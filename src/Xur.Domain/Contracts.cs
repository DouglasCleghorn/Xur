using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Xur.Domain;

public record Disk(string Path, string StablePath, string Serial, string Wwn, string Model,
    long Bytes, string Layout, string[] Mounts, string[] Blocked);
public record Inventory(string Generation, Disk[] Disks);
public record ScanMount(string Device, string Filesystem, string Options, bool BlockReadOnly, bool AnswerFound);
public record ScanResult(string State, string[] Answers, string[] Errors, string[] ReadOnlyDevices, ScanMount[] Mounts,string[]? Skipped=null);
public record InstallPlan(string Id, string Digest, string Generation, Disk Target, Disk[] Unaffected,
    string[] Actions, DateTimeOffset Expires);
public record Approval(string Id, string Digest);
public record Operation(string Id, string Stage, string Message, DateTimeOffset Updated);
public record ProcessResult(int ExitCode, string Output);

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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        var info = new ProcessStartInfo(executable) { RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Process did not start");
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return new(process.ExitCode, (await output) + (await error));
        }
        catch { if (!process.HasExited) process.Kill(true); throw; }
    }
}

public static class LocalClient
{
    public static HttpClient Create(string socket)
    {
        var handler = new SocketsHttpHandler { UseProxy=false,AllowAutoRedirect=false,UseCookies=false, ConnectCallback = async (_, ct) => {
            var connection = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try { await connection.ConnectAsync(new UnixDomainSocketEndPoint(socket), ct);
                return new NetworkStream(connection, ownsSocket: true); }
            catch { connection.Dispose(); throw; }
        }};
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost"), Timeout = TimeSpan.FromMinutes(3) };
    }
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
        "Create 1024 MiB ext4 /boot", "Create ext4 root using remaining space", "Install sealed Xur bootc payload"];
    public string Kickstart(Disk disk)
    {
        // lsblk names are re-observed, but still reject any Kickstart metacharacter.
        var name = disk.Path.StartsWith("/dev/", StringComparison.Ordinal) ? disk.Path[5..] : "";
        if (name.Length == 0 || name.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new InvalidOperationException("Invalid whole-disk path");
        return $"ignoredisk --only-use={name}\nclearpart --all --initlabel --disklabel=gpt --drives={name}\n" +
            $"part /boot/efi --fstype=efi --size=600 --ondisk={name}\n" +
            $"part /boot --fstype=ext4 --size=1024 --ondisk={name}\n" +
            $"part / --fstype=ext4 --size=8192 --grow --ondisk={name}\n" +
            $"bootloader --boot-drive={name}\n";
    }
}

public record SystemSnapshot(double CpuPercent,long MemoryUsedBytes,long MemoryTotalBytes,long DiskUsedBytes,long DiskTotalBytes,double UptimeSeconds,ServiceState[] Services);
public record ServiceState(string Name,string Description,string State,bool Managed);
public record ServiceAction(string Name,string Action);
