using System.Text;

namespace Xur.Util;

/// <summary>First-upgrade bridge: boot recovery must not depend on the current app link.</summary>
public sealed class RecoveryMigration(Runtime runtime, DurableFiles files)
{
    public const string Unit = "/etc/systemd/system/xur-app-recovery.service";
    const string Legacy = "ExecStart=/usr/bin/python3 /var/lib/xur/updater/app-update recover";
    const string Native = "ExecStart=/var/lib/xur/updater/xurutil app-update recover";

    public async Task<string> Migrate(string executable, string directory = "/var/lib/xur/updater",
        string unit = Unit, uint owner = 0, CancellationToken cancellationToken = default)
    {
        var metadata = Linux.Inspect(unit);
        if (metadata is null) return "absent";
        if (!metadata.Value.Regular || metadata.Value.Uid != owner || (metadata.Value.Mode & 0x12) != 0 || metadata.Value.Size > 65536) return "custom";
        var original = HostServiceMigration.ReadBounded(unit, out var observed, 65536);
        if (!metadata.Value.SameFile(observed)) throw new IOException("Recovery unit changed during inspection");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(original); }
        catch (DecoderFallbackException) { return "custom"; }
        if (text.Contains('\r') || text.Contains("\\\n", StringComparison.Ordinal)) return "custom";
        var lines = text.Split('\n');
        if (lines.Count(line => line == "[Service]") != 1 || lines.Count(line => line == "[Unit]") != 1 ||
            !lines.Contains("Description=Recover interrupted Xur application update")) return "custom";
        string? section = null;
        var starts = new List<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith('[')) section = line.Trim();
            else if (section == "[Service]" && line.Split('=', 2)[0].Trim() == "ExecStart") starts.Add(line);
        }
        if (starts.Count != 1 || starts[0] is not (Legacy or Native)) return "custom";
        var updated = Encoding.UTF8.GetBytes(string.Join('\n', lines.Select(line => line == Legacy ? Native : line)));
        var content = HostServiceMigration.ReadBounded(executable, out var source, 64 * 1024 * 1024);
        if (source.Uid != owner || (source.Mode & 0x12) != 0 || content.Length < 4 || !content.AsSpan(0, 4).SequenceEqual(new byte[] { 0x7f, 69, 76, 70 }))
            throw new IOException("Recovery requires an owned, protected native utility executable");
        var stable = Path.Combine(directory, "xurutil");
        var notices = new Dictionary<string, byte[]>();
        foreach (var name in new[] { "System.CommandLine-LICENSE.txt", "dotnet-LICENSE.txt", "dotnet-THIRD-PARTY-NOTICES.TXT", "TeeForge-LICENSE.txt", "TeeForge-THIRD-PARTY-NOTICES.txt" })
        {
            var bytes = HostServiceMigration.ReadBounded(Path.Combine(Path.GetDirectoryName(executable)!, "licenses", name), out var notice, 4 * 1024 * 1024);
            if (notice.Uid != owner || (notice.Mode & 0x12) != 0) throw new IOException("Unsafe utility license notice");
            var target = Path.Combine(directory, "licenses", name);
            if (Linux.Inspect(target) is null || !HostServiceMigration.ReadBounded(target, out _, 4 * 1024 * 1024).SequenceEqual(bytes)) notices.Add(target, bytes);
        }
        var installed = Linux.Inspect(stable);
        if (installed is { } unsafeFile && (!unsafeFile.Regular || unsafeFile.Uid != owner || (unsafeFile.Mode & 0x12) != 0))
            throw new IOException("Unsafe independent recovery executable");
        var changedBinary = installed is null || !HostServiceMigration.ReadBounded(stable, out _, 64 * 1024 * 1024).SequenceEqual(content) || (installed.Value.Mode & 0xfff) != 0x1c0;
        var changedUnit = !original.SequenceEqual(updated);
        var pending = Path.Combine(directory, ".xurutil-recovery.pending");
        if (!changedBinary && !changedUnit && notices.Count == 0 && !File.Exists(pending)) return "current";
        files.Write(pending, Encoding.UTF8.GetBytes("Recovery activation pending\n"), 0x180, owner, metadata.Value.Gid);
        if (changedBinary) files.Write(stable, content, 0x1c0, owner, metadata.Value.Gid);
        foreach (var (target, bytes) in notices) files.Write(target, bytes, 0x1a4, owner, metadata.Value.Gid);
        Linux.Metadata? publication = null;
        try
        {
            if (changedUnit)
            {
                if (!(Linux.Inspect(unit) is { } latest && metadata.Value.Unchanged(latest))) throw new IOException("Recovery unit changed during migration");
                files.Write(unit, updated, (uint)(metadata.Value.Mode & 0xfff), metadata.Value.Uid, metadata.Value.Gid, value => publication = value);
            }
            // Older installations invoked a Python script from this directory;
            // an ELF executable also needs its own persistent executable label.
            var context = Encoding.UTF8.GetString(await runtime.Run(["matchpathcon", "-n", stable], 10, cancellationToken)).Trim().Split(':');
            if (context.Length < 3 || context[2] != "bin_t")
                await runtime.Run(["semanage", "fcontext", "-a", "-t", "bin_t", stable], 10, cancellationToken);
            await runtime.Run(["restorecon", stable, unit], 10, cancellationToken);
            await runtime.Run(["systemctl", "daemon-reload"], 10, cancellationToken);
            File.Delete(pending);
            files.SyncDirectory(directory);
        }
        catch
        {
            if (publication is { } ours && Linux.Inspect(unit) is { } current && ours.SamePublication(current) &&
                HostServiceMigration.ReadBounded(unit, out _, 65536).SequenceEqual(updated))
            {
                files.Write(unit, original, (uint)(metadata.Value.Mode & 0xfff), metadata.Value.Uid, metadata.Value.Gid);
                await runtime.Run(["restorecon", unit], 10);
                await runtime.Run(["systemctl", "daemon-reload"], 10);
            }
            throw;
        }
        return "migrated";
    }
}
