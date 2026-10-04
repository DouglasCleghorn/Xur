using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace Xur.Util;

public static class BundleArchive
{
    public static string SafeName(string name)
    {
        if (name.StartsWith('/') || name.Contains('\0') || name.Split('/').Contains(".."))
            throw new UserError("Unsafe archive entry");
        return string.Join('/', name.Split('/').Where(part => part is not "" and not "."));
    }

    public static void Unpack(string archive, string target, JsonObject entry)
    {
        // Validate the complete archive before writing any entries.
        var names = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        var count = 0;
        using (var input = File.OpenRead(archive))
        using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        using (var reader = new TarReader(gzip))
        {
            TarEntry? member;
            while ((member = reader.GetNextEntry()) is not null)
            {
                var name = SafeName(member.Name);
                if (member.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory) ||
                    name.Length == 0 && member.EntryType != TarEntryType.Directory || !names.Add(name) || member.Length < 0)
                    throw new UserError("Unsafe archive entry");
                total = checked(total + member.Length);
                if (total > 4L * 1024 * 1024 * 1024 || ++count > 20000) throw new UserError("Expanded archive exceeds limits");
            }
        }
        if (Directory.Exists(target)) throw new UserError("Extraction directory already exists");
        Directory.CreateDirectory(target);
        var modes = new List<(string Path, UnixFileMode Mode)>();
        using (var input = File.OpenRead(archive))
        using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        using (var reader = new TarReader(gzip))
        {
            TarEntry? member;
            while ((member = reader.GetNextEntry()) is not null)
            {
                var name = SafeName(member.Name);
                if (name.Length == 0) continue;
                var path = Path.Combine(target, name);
                if (member.EntryType == TarEntryType.Directory) Directory.CreateDirectory(path);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    member.DataStream?.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }
                if (member.EntryType != TarEntryType.Directory)
                {
                    // Match tarfile's data filter: strip special and writable
                    // bits, retain executable files, and ensure owner read/write.
                    var mode = member.Mode & (UnixFileMode)0x1ed;
                    if ((mode & UnixFileMode.UserExecute) == 0) mode &= ~(UnixFileMode)0x49;
                    modes.Add((path, mode | UnixFileMode.UserRead | UnixFileMode.UserWrite));
                }
            }
        }
        // Retain execute bits before checking the manifest's executable contract.
        foreach (var (path, mode) in modes.Where(item => File.Exists(item.Path))) File.SetUnixFileMode(path, mode);
        Validate(target, entry);
        foreach (var (path, mode) in modes.OrderByDescending(item => item.Path.Length)) File.SetUnixFileMode(path, mode);
    }

    public static void Validate(string target, JsonObject entry)
    {
        var directory = Linux.Inspect(target);
        if (directory is null || (directory.Value.Mode & 0xf000) != 0x4000 || directory.Value.Uid != Linux.EffectiveUser() || (directory.Value.Mode & 0x12) != 0)
            throw new UserError("Unsafe bundle directory");
        var metadata = DurableFiles.ReadObject(Path.Combine(target, "bundle.json"));
        if (JsonValues.Text(metadata["id"]) != JsonValues.Text(entry["id"]) || JsonValues.Integer(metadata["hostAbi"]) != 1)
            throw new UserError("Bundle identity mismatch");
        var files = metadata["files"] as JsonObject ?? throw new UserError("Bundle file manifest mismatch");
        if (DurableFiles.Hash(Encoding.UTF8.GetBytes(CanonicalManifest(files))) != JsonValues.Text(metadata["id"]))
            throw new UserError("Bundle file manifest mismatch");
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFileSystemEntries(target, "*", SearchOption.AllDirectories))
        {
            var observed = Linux.Inspect(path) ?? throw new UserError("Bundle entry disappeared");
            if (observed.Uid != Linux.EffectiveUser() || (observed.Mode & 0x12) != 0) throw new UserError("Unsafe bundle ownership or permissions");
            if ((observed.Mode & 0xf000) == 0x4000) continue;
            if (!observed.Regular) throw new UserError("Unsafe bundle entry");
            var name = Path.GetRelativePath(target, path);
            if (name != "bundle.json") actual.Add(name);
        }
        if (!actual.SetEquals(files.Select(pair => pair.Key))) throw new UserError("Unexpected or missing bundle files");
        foreach (var (name, digest) in files)
        {
            if (SafeName(name) != name || !SignedUpdater.IsHash(JsonValues.Text(digest)) ||
                DurableFiles.HashFile(Path.Combine(target, name)) != JsonValues.Text(digest)) throw new UserError("Bundle file hash mismatch");
        }
        foreach (var name in new[] { "control/Xur.Control", "agent/Xur.Agent", "gateway/Xur.Gateway", "host/app-update" })
            if (!File.Exists(Path.Combine(target, name)) || (File.GetUnixFileMode(Path.Combine(target, name)) & UnixFileMode.UserExecute) == 0)
                throw new UserError("Missing application executable");
        if (files.ContainsKey("host/xurutil") && (File.GetUnixFileMode(Path.Combine(target, "host/xurutil")) & UnixFileMode.UserExecute) == 0)
            throw new UserError("Missing utility executable");
    }

    // Existing signed bundles hash Python json.dumps(files, sort_keys=True), including
    // ASCII escapes, lowercase hex, separators, and Unicode code-point key ordering.
    public static string CanonicalManifest(JsonObject files)
    {
        var pairs = files.OrderBy(pair => pair.Key, Comparer<string>.Create(CompareCodePoints));
        return "{" + string.Join(", ", pairs.Select(pair => Quote(pair.Key) + ": " + Quote(JsonValues.RequiredText(pair.Value)))) + "}";
    }

    // Compact release descriptors use the same Python ASCII escaping and sorted
    // keys, with no separator whitespace. Release numbers are integers.
    public static string CanonicalDescriptor(JsonNode? value) => value switch
    {
        null => "null",
        JsonObject obj => "{" + string.Join(',', obj.OrderBy(pair => pair.Key, Comparer<string>.Create(CompareCodePoints))
            .Select(pair => Quote(pair.Key) + ":" + CanonicalDescriptor(pair.Value))) + "}",
        JsonArray array => "[" + string.Join(',', array.Select(CanonicalDescriptor)) + "]",
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => Quote(text),
        JsonValue scalar when scalar.TryGetValue<bool>(out var boolean) => boolean ? "true" : "false",
        JsonValue scalar when JsonValues.Integer(scalar) is { } number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new UserError("Invalid update descriptor number")
    };

    static int CompareCodePoints(string left, string right)
    {
        var a = left.EnumerateRunes().GetEnumerator();
        var b = right.EnumerateRunes().GetEnumerator();
        while (true)
        {
            var hasA = a.MoveNext();
            var hasB = b.MoveNext();
            if (!hasA || !hasB) return hasA.CompareTo(hasB);
            var comparison = a.Current.Value.CompareTo(b.Current.Value);
            if (comparison != 0) return comparison;
        }
    }

    static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        foreach (var character in value)
            result.Append(character switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\f' => "\\f", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                < ' ' or > '~' => "\\u" + ((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture),
                _ => character.ToString()
            });
        return result.Append('"').ToString();
    }
}
