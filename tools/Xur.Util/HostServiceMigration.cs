using System.Text;

namespace Xur.Util;

public sealed class HostServiceMigration(Runtime runtime, DurableFiles files)
{
    public const string Unit = "/etc/systemd/system/xur-agent.service";
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[]? Migration(byte[] content)
    {
        string text;
        try { text = StrictUtf8.GetString(content); }
        catch (DecoderFallbackException) { return null; }
        if (text.Contains('\r') || text.Contains("\\\n", StringComparison.Ordinal)) return null;
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? section = null;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line;
                if (!sections.TryAdd(section, [])) return null;
            }
            else if (section is not null && !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#') && !line.TrimStart().StartsWith(';'))
                sections[section].Add(line);
        }
        var service = sections.GetValueOrDefault("[Service]", []);
        var unit = sections.GetValueOrDefault("[Unit]", []);
        static string[] Directives(List<string> lines, string name) =>
            lines.Where(line => line.Split('=', 2)[0].Trim() == name).ToArray();
        if (!Directives(unit, "Description").SequenceEqual(["Description=Xur privileged typed operations (Unix socket only)"])) return null;
        if (!Directives(service, "ExecStart").SequenceEqual(["ExecStart=/var/lib/xur/app/current/agent/Xur.Agent"])) return null;
        if (!Directives(service, "WorkingDirectory").SequenceEqual(["WorkingDirectory=/var/lib/xur/app/current/agent"])) return null;
        var after = Directives(unit, "After");
        var requires = Directives(unit, "Requires");
        if (after.SequenceEqual(["After=local-fs.target NetworkManager.service"]) && requires.Length == 0)
            return unit.Any(line => line.StartsWith("Wants=", StringComparison.Ordinal) && line[6..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("xur-network.service")) ? content : null;
        if (!after.SequenceEqual(["After=local-fs.target xur-network.service"]) || !requires.SequenceEqual(["Requires=xur-network.service"])) return null;

        // Edit exact recognized Unit directives only, retaining comments and unrelated settings.
        var result = new StringBuilder();
        section = null;
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.StartsWith('[')) section = line.Trim();
            result.Append(section == "[Unit]" ? line switch
            {
                "After=local-fs.target xur-network.service" => "After=local-fs.target NetworkManager.service",
                "Requires=xur-network.service" => "Wants=xur-network.service",
                _ => line
            } : line);
            if (index < lines.Length - 1) result.Append('\n');
        }
        return Encoding.UTF8.GetBytes(result.ToString());
    }

    internal static byte[] ReadBounded(string path, out Linux.Metadata metadata, int limit)
    {
        using var stream = new FileStream(Linux.OpenRead(path, 0x800), FileAccess.Read); // O_NONBLOCK also rejects raced FIFOs.
        metadata = Linux.Inspect(stream.SafeFileHandle);
        if (!metadata.Regular || metadata.Size > (ulong)limit) throw new IOException("Unsafe agent migration file");
        var buffer = new byte[checked((int)metadata.Size + 1)];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = stream.Read(buffer, count, buffer.Length - count);
            if (read == 0) break;
            count += read;
        }
        if ((ulong)count != metadata.Size) throw new IOException("Agent migration file changed during inspection");
        return buffer[..count];
    }

    static bool Completed(string marker, byte[] digest, uint owner)
    {
        if (Linux.Inspect(marker) is null) return false;
        var bytes = ReadBounded(marker, out var metadata, 65);
        if (!metadata.Regular || metadata.Uid != owner || (metadata.Mode & 0xfff) != 0x180 || metadata.Size > 65)
            throw new IOException("Unsafe agent migration completion marker");
        return bytes.SequenceEqual(digest);
    }

    public async Task<string> Migrate(string path = Unit, uint owner = 0, CancellationToken cancellationToken = default)
    {
        var inspected = Linux.Inspect(path);
        if (inspected is null) return "absent";
        var metadata = inspected.Value;
        if (!metadata.Regular || metadata.Uid != owner || (metadata.Mode & 0x12) != 0 || metadata.Size > 65536) return "custom";
        var original = ReadBounded(path, out var opened, 65536);
        if (!metadata.SameFile(opened)) throw new IOException("Agent unit changed during migration inspection");
        var updated = Migration(original);
        if (updated is null) return "custom";
        var changed = !original.SequenceEqual(updated);
        var marker = Path.Combine(Path.GetDirectoryName(path)!, ".xur-agent-network-v1.completed");
        var digest = Encoding.ASCII.GetBytes(DurableFiles.Hash(updated) + "\n");
        var completed = Completed(marker, digest, owner);
        if (!changed && completed) return "current";
        if (changed && !(Linux.Inspect(path) is { } observed && metadata.Unchanged(observed)))
            throw new IOException("Agent unit changed during migration; refusing to overwrite it");

        Linux.Metadata? publication = null;
        try
        {
            // Include the post-rename directory fsync in the rollback scope.
            if (changed) files.Write(path, updated, (uint)(metadata.Mode & 0xfff), metadata.Uid, metadata.Gid, value => publication = value);
            await runtime.Run(["restorecon", path], 10, cancellationToken);
            await runtime.Run(["systemctl", "daemon-reload"], 10, cancellationToken);
            files.Write(marker, digest, 0x180, owner, metadata.Gid);
        }
        catch
        {
            var latest = Linux.Inspect(path);
            if (changed && publication is { } ours && latest is { } current && ours.SamePublication(current) &&
                ReadBounded(path, out _, 65536).SequenceEqual(updated))
            {
                files.Write(path, original, (uint)(metadata.Mode & 0xfff), metadata.Uid, metadata.Gid);
                // Cleanup must complete even when the original command was cancelled.
                await runtime.Run(["restorecon", path], 10);
                await runtime.Run(["systemctl", "daemon-reload"], 10);
            }
            throw;
        }
        return changed ? "migrated" : "current";
    }
}
