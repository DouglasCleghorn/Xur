using System.Text;

namespace Xur.Util;

public sealed class LogCompression(Runtime runtime, DurableFiles files)
{
    public static readonly string[] Names =
    [
        "journald.conf.d/60-xur-log-compression.conf",
        "system/systemd-journald.service.d/60-xur-log-compression.conf"
    ];

    static byte[] Resource(string name)
    {
        using var input = typeof(LogCompression).Assembly.GetManifestResourceStream("Xur.Util." + name)!;
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    public static byte[][] Configuration => [Resource("JournalConfiguration"), Resource("JournalService")];

    public async Task<string> Ensure(string root = "/", bool? activate = null,
        CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(root)) throw new UserError("--root must be absolute");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var directory = Path.Combine(root, "etc/systemd");
        var paths = Names.Select(name => Path.Combine(directory, name)).ToArray();
        var configuration = Configuration;
        var changed = Enumerable.Range(0, paths.Length)
            .Where(i => !File.Exists(paths[i]) || !File.ReadAllBytes(paths[i]).SequenceEqual(configuration[i])).ToArray();
        var pending = Path.Combine(directory, "journald.conf.d/.xur-log-compression.pending");
        if (changed.Length == 0 && !File.Exists(pending)) return "current";

        // Publish the pending marker first: a partial write or activation retries on the next boot.
        if (!File.Exists(pending)) files.Write(pending, Encoding.UTF8.GetBytes("Journal compression activation pending\n"), 0x1a4);
        foreach (var index in changed) files.Write(paths[index], configuration[index], 0x1a4);
        if (!(activate ?? root == "/")) return "installed";

        await runtime.Run(["restorecon", .. paths], 10, cancellationToken);
        await runtime.Run(["systemctl", "daemon-reload"], 10, cancellationToken);
        await runtime.Run(["systemctl", "restart", "systemd-journald.service"], 10, cancellationToken);
        // Rotate once. Existing logs retain their codec and are never vacuumed or recompressed.
        await runtime.Run(["journalctl", "--rotate"], 10, cancellationToken);
        File.Delete(pending);
        files.SyncDirectory(Path.GetDirectoryName(pending)!);
        return "activated";
    }
}
