using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Xur.IO;

public sealed record AllocatedEntry(string Name, string Kind, long? Bytes, bool Partial);
public sealed record AllocatedScan(IReadOnlyList<AllocatedEntry> Entries, long Bytes, bool Partial, int Errors, int Visited, bool Truncated);
public sealed record FolderSize(long Bytes, bool Partial);

/// <summary>Bounded allocated-space accounting without reading file contents or following mounts.</summary>
[SupportedOSPlatform("linux")]
public static class FolderScanner
{
    sealed class Budget(TimeSpan duration, int limit, int depth, CancellationToken token, bool partialMounts = false)
    {
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly HashSet<(uint Major, uint Minor, ulong Inode)> seen = [];
        public int Visited { get; private set; }
        public int Errors { get; private set; }
        public bool Stopped { get; private set; }
        public bool Visit(int level)
        {
            token.ThrowIfCancellationRequested();
            if (Visited >= limit || clock.Elapsed >= duration || level >= depth) { Stopped = true; return false; }
            Visited++; return true;
        }
        public (long? Bytes, bool Partial, string Kind) Measure(DirectoryTree tree, SafeFileHandle parent, string name, int level, Action<int, long, bool>? measured = null)
        {
            if (!Visit(level)) return (null, true, "folder");
            try
            {
                var expected = DirectoryTree.Inspect(parent, name);
                if (!tree.Within(expected)) return (null, partialMounts, "mount");
                if (!seen.Add((expected.DeviceMajor, expected.DeviceMinor, expected.Inode))) return (0, false, expected.Kind);
                var amount = expected.Allocated; var partial = false;
                if (expected.Kind == "folder")
                {
                    using var child = DirectoryTree.Child(parent, name, true); DirectoryTree.Verify(expected, child);
                    foreach (var item in DirectoryTree.Names(child))
                    {
                        var result = Measure(tree, child, item, level + 1);
                        amount = checked(amount + (result.Bytes ?? 0)); partial |= result.Partial;
                        if (Stopped) break;
                    }
                    measured?.Invoke(level, amount, partial);
                }
                return (amount, partial, expected.Kind);
            }
            catch (IOException) { Errors++; return (null, true, "unavailable"); }
        }
    }

    public static AllocatedScan Scan(string root, string path, TimeSpan? duration = null, int limit = 250000, CancellationToken cancellationToken = default)
    {
        using var tree = new DirectoryTree(root); using var folder = tree.Open(path, true);
        var budget = new Budget(duration ?? TimeSpan.FromSeconds(15), limit, 256, cancellationToken);
        var rows = new List<AllocatedEntry>(); var truncated = false; var listingErrors = 0;
        foreach (var name in DirectoryTree.Names(folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rows.Count >= 2000) { truncated = true; break; }
            try { rows.Add(new(name, DirectoryTree.Inspect(folder, name).Kind, null, true)); }
            catch (IOException) { rows.Add(new(name, "unavailable", null, true)); listingErrors++; }
        }
        rows = rows.OrderBy(row => row.Kind == "folder").ThenBy(row => row.Name, StringComparer.Ordinal).ToList();
        for (var index = 0; index < rows.Count && !budget.Stopped; index++)
        {
            var result = budget.Measure(tree, folder, rows[index].Name, 0);
            rows[index] = rows[index] with { Bytes = result.Bytes, Partial = result.Partial, Kind = result.Kind };
        }
        var sorted = rows.OrderBy(row => row.Bytes is null).ThenByDescending(row => row.Bytes ?? 0).ThenBy(row => row.Name, StringComparer.Ordinal).ToArray();
        return new(sorted, sorted.Sum(row => row.Bytes ?? 0), budget.Stopped || truncated || budget.Errors + listingErrors > 0, budget.Errors + listingErrors, budget.Visited, truncated);
    }

    public static Dictionary<string, FolderSize> Sizes(DirectoryTree tree, string path, TimeSpan? duration = null, int limit = 250000, CancellationToken cancellationToken = default)
    {
        using var folder = tree.Open(path, true);
        var budget = new Budget(duration ?? TimeSpan.FromSeconds(12), limit, 128, cancellationToken, partialMounts: true);
        var sizes = new Dictionary<string, FolderSize>(); var total = DirectoryTree.Inspect(folder).Allocated; var partial = false; var cachedBytes = 0;
        foreach (var name in DirectoryTree.Names(folder))
        {
            var key = path.Length == 0 ? name : path + "/" + name;
            var result = budget.Measure(tree, folder, name, 0, (level, bytes, incomplete) =>
            {
                var cost = System.Text.Encoding.UTF8.GetByteCount(key) * 6 + 100;
                if (level == 0 && sizes.Count < 1999 && cachedBytes + cost < 512 * 1024)
                { sizes[key] = new(bytes, incomplete); cachedBytes += cost; }
            });
            total = checked(total + (result.Bytes ?? 0)); partial |= result.Partial;
            if (budget.Stopped) break;
        }
        sizes[path] = new(total, partial || budget.Stopped || budget.Errors > 0);
        return sizes;
    }
}
