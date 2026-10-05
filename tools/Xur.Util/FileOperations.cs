using System.IO.Compression;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using Xur.IO;

namespace Xur.Util;

/// <summary>JSON header followed by an optional streamed file/ZIP body; no extraction to disk.</summary>
public sealed class FileOperations
{
    public bool Started { get; private set; }
    static string Name(string path) => DirectoryTree.Parts(path).LastOrDefault() ?? throw new ArgumentException("Select a regular file or folder.");
    static JsonObject Row(string name, string kind, long? bytes, double modified) => new() { ["name"] = name, ["kind"] = kind, ["bytes"] = bytes, ["modified"] = modified };
    static async Task Header(Stream output, JsonObject value, CancellationToken token)
    {
        await output.WriteAsync(System.Text.Encoding.UTF8.GetBytes(value.ToJsonString() + "\n"), token);
        await output.FlushAsync(token);
    }
    public async Task Run(string mode, string root, string path, string query, Stream output, CancellationToken token = default)
    {
        DirectoryTree.Parts(path);
        if (query.Length > 4096) throw new ArgumentException("Input is too long.");
        using var tree = new DirectoryTree(root);
        if (mode is "move" or "delete")
        {
            var (parent, name) = tree.Parent(path); using var owned = parent;
            var expected = DirectoryTree.Inspect(parent, name);
            if (!tree.Within(expected)) throw new IOException("Nested mounts are not followed.");
            if (expected.Kind == "folder") { using var check = DirectoryTree.Child(parent, name, true); DirectoryTree.Verify(expected, check); }
            if (mode == "delete")
            {
                if (query != "confirm") throw new ArgumentException("Delete confirmation is required.");
                Remove(tree, parent, name, 0, token);
            }
            else
            {
                var (destination, target) = tree.Parent(query); using var targetParent = destination;
                DirectoryTree.Move(parent, name, destination, target);
            }
            await Header(output, new() { ["ok"] = true }, token); return;
        }
        if (mode == "size")
        {
            var sizes = new JsonObject();
            foreach (var (key, value) in FolderScanner.Sizes(tree, path, cancellationToken: token)) sizes[key] = new JsonObject { ["bytes"] = value.Bytes, ["partial"] = value.Partial };
            await Header(output, new() { ["ok"] = true, ["sizes"] = sizes }, token); return;
        }
        using var item = tree.Open(path, mode == "list"); var info = DirectoryTree.Inspect(item);
        if (mode == "list")
        {
            // Retain only the first 501 matches while scanning arbitrarily large directories.
            var comparer = Comparer<JsonObject>.Create((a, b) =>
            {
                var folders = (JsonValues.Text(a["kind"]) != "folder").CompareTo(JsonValues.Text(b["kind"]) != "folder");
                if (folders != 0) return folders;
                var names = StringComparer.OrdinalIgnoreCase.Compare(JsonValues.Text(a["name"]), JsonValues.Text(b["name"]));
                return names != 0 ? names : StringComparer.Ordinal.Compare(JsonValues.Text(a["name"]), JsonValues.Text(b["name"]));
            });
            var rows = new SortedSet<JsonObject>(comparer);
            foreach (var name in DirectoryTree.Names(item))
            {
                token.ThrowIfCancellationRequested();
                if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                DirectoryTree.Metadata metadata;
                try { metadata = DirectoryTree.Inspect(item, name); } catch (IOException) { continue; }
                var kind = tree.Within(metadata) ? metadata.Kind : "mount";
                rows.Add(Row(name, kind, kind == "file" ? checked((long)metadata.Size) : null, metadata.Modified));
                if (rows.Count > 501) rows.Remove(rows.Max!);
            }
            var entries = new JsonArray(); foreach (var row in rows.Take(500)) entries.Add((JsonNode)row);
            await Header(output, new() { ["ok"] = true, ["path"] = path, ["entries"] = entries, ["truncated"] = rows.Count > 500 }, token); return;
        }
        if (mode is not ("download" or "stored" or "compressed")) throw new ArgumentException("Invalid operation.");
        var fileName = Name(path);
        if (info.Kind is not ("folder" or "file")) throw new ArgumentException("Select a regular file or folder.");
        if (info.Kind == "folder")
        {
            await SendHeader(fileName + ".zip", null, "application/zip");
            using var zip = new ZipArchive(new ZipSink(output), ZipArchiveMode.Create, leaveOpen: true);
            await Archive(tree, item, fileName, zip, mode == "stored" ? CompressionLevel.NoCompression : CompressionLevel.Optimal, 0, token);
        }
        else if (mode == "download")
        {
            await SendHeader(fileName, checked((long)info.Size), "application/octet-stream");
            await Copy(item, output, info, token);
        }
        else
        {
            using var source = new FileStream(DirectoryTree.Duplicate(item), FileAccess.Read);
            using var original = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            // The framework rejects encrypted entries when opening them. Reject
            // encrypted metadata before publishing a successful response header.
            foreach (var entry in original.Entries) if (entry.IsEncrypted) throw new InvalidDataException("Encrypted ZIP files can only be downloaded as originals.");
            await SendHeader(Path.GetFileNameWithoutExtension(fileName) + (mode == "stored" ? "-uncompressed.zip" : "-compressed.zip"), null, "application/zip");
            using var zip = new ZipArchive(new ZipSink(output), ZipArchiveMode.Create, leaveOpen: true);
            foreach (var entry in original.Entries)
            {
                token.ThrowIfCancellationRequested();
                var target = zip.CreateEntry(entry.FullName, mode == "stored" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                await using var destination = await target.OpenAsync(token); await using var input = await entry.OpenAsync(token);
                await StreamTransfer.Copy(input, destination, entry.Length, cancellationToken: token, expectedCrc32: entry.Crc32);
            }
            if (!info.Unchanged(DirectoryTree.Inspect(item))) throw new IOException("Archive changed during download.");
        }
        async Task SendHeader(string name, long? size, string contentType)
        { await Header(output, new() { ["ok"] = true, ["name"] = name, ["size"] = size, ["contentType"] = contentType }, token); Started = true; }
    }
    static async Task Copy(SafeFileHandle item, Stream output, DirectoryTree.Metadata expected, CancellationToken token)
    {
        using var source = new FileStream(DirectoryTree.Duplicate(item), FileAccess.Read);
        var receipt = await StreamTransfer.Copy(source, output, checked((long)expected.Size), cancellationToken: token);
        if (receipt.Bytes != (long)expected.Size || !expected.Unchanged(DirectoryTree.Inspect(item))) throw new IOException("File changed during download.");
    }
    static async Task Archive(DirectoryTree tree, SafeFileHandle folder, string prefix, ZipArchive zip, CompressionLevel compression, int depth, CancellationToken token)
    {
        if (depth > 128) throw new IOException("Folder nesting is too deep.");
        zip.CreateEntry(prefix + "/", compression);
        foreach (var name in DirectoryTree.Names(folder))
        {
            token.ThrowIfCancellationRequested();
            var expected = DirectoryTree.Inspect(folder, name);
            if (expected.Kind is not ("file" or "folder")) continue;
            using var child = DirectoryTree.Child(folder, name, expected.Kind == "folder"); DirectoryTree.Verify(expected, child);
            if (expected.Kind == "folder") await Archive(tree, child, prefix + "/" + name, zip, compression, depth + 1, token);
            else { var entry = zip.CreateEntry(prefix + "/" + name, compression); await using var target = await entry.OpenAsync(token); await Copy(child, target, expected, token); }
        }
    }
    // ZIP offsets start at zero after the JSON header, even in seekable fixtures.
    // A pipe also needs data descriptors so entries can stream without buffering.
    sealed class ZipSink(Stream output) : Stream
    {
        public override bool CanRead => false;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush();
        public override Task FlushAsync(CancellationToken token) => output.FlushAsync(token);
        public override void Write(byte[] bytes, int offset, int count) => output.Write(bytes, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default) => output.WriteAsync(bytes, token);
        public override Task WriteAsync(byte[] bytes, int offset, int count, CancellationToken token) => output.WriteAsync(bytes, offset, count, token);
        public override int Read(byte[] bytes, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
    static void Remove(DirectoryTree tree, SafeFileHandle parent, string name, int depth, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (depth > 128) throw new IOException("Folder nesting is too deep.");
        var expected = DirectoryTree.Inspect(parent, name);
        if (!tree.Within(expected)) throw new IOException("Nested mounts are not followed.");
        if (expected.Kind == "folder")
        {
            using var child = DirectoryTree.Child(parent, name, true); DirectoryTree.Verify(expected, child);
            foreach (var entry in DirectoryTree.Names(child)) Remove(tree, child, entry, depth + 1, token);
            if (!expected.SameFile(DirectoryTree.Inspect(parent, name))) throw new IOException("Folder changed during deletion.");
            DirectoryTree.Delete(parent, name, true);
        }
        else DirectoryTree.Delete(parent, name);
    }
}
