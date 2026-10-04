using System.Globalization;
using System.Text;

namespace Xur.Util;

public sealed class InstallerPreflight(Runtime runtime)
{
    public static readonly string[] Programs = ["wpa_supplicant", "chronyd", "chronyc", "hwclock", "modprobe", "sysctl", "ldconfig", "sshd", "auditctl", "tailscaled", "mkfs.btrfs"];
    public static void CheckRuntime(string root = "/")
    {
        if (!Path.IsPathFullyQualified(root)) throw new UserError("--root must be absolute");
        var usr = Path.Combine(root, "usr");
        var link = new DirectoryInfo(Path.Combine(usr, "sbin")).LinkTarget;
        if (link is not ("bin" or "/usr/bin")) throw new UserError("/usr/sbin must remain the Fedora symlink to /usr/bin");
        foreach (var program in Programs)
        {
            var path = Path.Combine(usr, "bin", program);
            if (!File.Exists(path) || (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                throw new UserError("Missing executable: /usr/bin/" + program);
        }
    }
    public async Task SyncClock(bool? hasRtc = null, Func<DateTimeOffset>? now = null, CancellationToken token = default)
    {
        async Task<string> Command(string[] arguments, int seconds, string message)
        {
            try { return Encoding.UTF8.GetString(await runtime.Run(["/usr/bin/env", "LC_ALL=C", "TZ=UTC", .. arguments], seconds, token)).Trim(); }
            catch (Exception error) when (error is IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
            { if (token.IsCancellationRequested) throw; throw new UserError(message); }
        }
        await Command(["systemctl", "start", "chronyd.service"], 15, "Time synchronization service could not start. Check chronyd and installer executable paths.");
        await Command(["chronyc", "online"], 5, "Could not bring the configured time sources online.");
        await Command(["chronyc", "makestep", "0.1", "3"], 5, "Could not enable correction of the system clock.");
        await Command(["chronyc", "burst", "4/4"], 5, "Could not request measurements from the time servers.");
        await Command(["chronyc", "waitsync", "15", "1", "0", "2"], 35, "Time synchronization did not complete within 30 seconds. Check Ethernet/Wi-Fi, DNS and access to NTP servers (UDP 123).");
        hasRtc ??= File.Exists("/dev/rtc") || Directory.EnumerateFileSystemEntries("/dev", "rtc*").Any(p => Path.GetFileName(p)[3..].All(char.IsAsciiDigit) && Path.GetFileName(p).Length > 3);
        if (hasRtc.Value)
        {
            await Command(["hwclock", "--systohc", "--utc"], 10, "Could not save synchronized UTC to the hardware clock. Anaconda would restore stale time.");
            var value = await Command(["hwclock", "--show", "--utc"], 10, "Could not verify the hardware clock.");
            var fields = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !DateTimeOffset.TryParseExact(fields[0] + " " + fields[1],
                ["yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd HH:mm:sszzz"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var rtc) ||
                Math.Abs(((now ?? (() => DateTimeOffset.UtcNow))() - rtc).TotalSeconds) > 5)
                throw new UserError("Hardware clock verification failed. Correct the RTC before starting installation.");
        }
    }
}
