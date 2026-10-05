using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Xur.IO;

namespace Xur.Util.Tests;

static class FileTests
{
    static async Task<(JsonObject Header, byte[] Body)> Run(Fixture fixture, string mode, string path = "", string query = "")
    {
        using var output = new MemoryStream();
        await new FileOperations().Run(mode, fixture.Root, path, query, output);
        var bytes = output.ToArray(); var newline = Array.IndexOf(bytes, (byte)10);
        return (JsonNode.Parse(bytes.AsSpan(0, newline))!.AsObject(), bytes[(newline + 1)..]);
    }
    public static async Task Run()
    {
        using var fixture = new Fixture();
        var payload = Enumerable.Range(0, 131328).Select(n => (byte)n).ToArray();
        fixture.Write(".hidden/steam-测试.log", ""); File.WriteAllBytes(fixture.PathOf(".hidden/steam-测试.log"), payload);
        Directory.CreateSymbolicLink(fixture.PathOf("link"), "/etc");
        File.CreateSymbolicLink(fixture.PathOf("filelink"), "/etc/passwd");
        await new Runtime().Run(["mkfifo", fixture.PathOf("fifo")]);
        var listing = await Run(fixture, "list");
        Verify.That(JsonValues.Text(listing.Header["entries"]![0]!["name"]) == ".hidden", "Hidden folders sort before files");
        Verify.That(listing.Body.Length == 0, "List protocol has only a JSON header");
        var download = await Run(fixture, "download", ".hidden/steam-测试.log");
        Verify.That(download.Body.SequenceEqual(payload) && JsonValues.Integer(download.Header["size"]) == payload.Length, "Unicode file downloads retain exact binary bytes and length");
        foreach (var path in new[] { "../etc/passwd", "/etc/passwd", "link/passwd", "filelink", "fifo", ".hidden/../filelink", "./filelink", "", ".hidden//steam-测试.log", ".hidden/" })
            await Verify.Reject(() => Run(fixture, "download", path), "Traversal, links, special files and root downloads are rejected");
        var alias = fixture.PathOf("alias"); Directory.CreateSymbolicLink(alias, fixture.Root);
        await Verify.Reject(() => Task.Run(() => { using var tree = new DirectoryTree(alias); }), "Root aliases are rejected");
        foreach (var path in new[] { "../outside", "x/../outside", "x\0y", "\ud800" })
            await Verify.Reject(() => Task.Run(() => DirectoryTree.Parts(path)), "Ambiguous paths and invalid Unicode cannot reach native marshaling");
        using (var tree = new DirectoryTree("/"))
            await Verify.Reject(() => Task.Run(() => { using var child = tree.Open("proc", true); }), "Kernel rejects nested procfs mount traversal");
        fixture.Write(new string('x', 255), "long filename");
        Verify.That((await Run(fixture, "list")).Header["entries"]!.AsArray().Any(e => (JsonValues.Text(e!["name"]) ?? "").Length == 255), "Padded Linux directory records support 255-byte names");
        for (var n = 0; n < 502; n++) fixture.Write($"log-{n:0000}", "x");
        listing = await Run(fixture, "list");
        Verify.That(JsonValues.Boolean(listing.Header["truncated"]) && listing.Header["entries"]!.AsArray().Count == 500, "Large listings retain only the first 500 matches");
        listing = await Run(fixture, "list", query: "LOG-0501");
        Verify.That(listing.Header["entries"]!.AsArray().Count == 1 && JsonValues.Text(listing.Header["entries"]![0]!["name"]) == "log-0501", "Search is case insensitive");
        fixture.Write("fresh", "new");
        Verify.That((await Run(fixture, "list", query: "fresh")).Header["entries"]!.AsArray().Count == 1, "Listings see new files without cached rows");

        await Scan();
        await Archives();
        await Mutations();
        var command = Xur.Util.Program.CreateCommand();
        Verify.That(command.Parse(["files", "list", "--", fixture.Root, "", "--help"]).Errors.Count == 0, "Argument delimiter preserves filenames and queries starting with dashes");
        Verify.That(command.Parse(["files", "list", fixture.Root]).Errors.Count == 0, "Omitted path selects the root");
        Verify.That(command.Parse(["files", "delete"]).Errors.Count > 0, "File commands require an explicit root");
    }
    static async Task Scan()
    {
        using var fixture = new Fixture(); fixture.Write("data", new string('x', 8192));
        await new Runtime().Run(["ln", fixture.PathOf("data"), fixture.PathOf("hardlink")]);
        Directory.CreateSymbolicLink(fixture.PathOf("outside"), "/etc");
        var scan = FolderScanner.Scan(fixture.Root, "");
        using var tree = new DirectoryTree(fixture.Root);
        Verify.That(scan.Entries.Single(e => e.Name == "outside").Kind == "link" && !scan.Partial, "Allocated scans report symlinks without following them");
        Verify.That(scan.Entries.Where(e => e.Name is "data" or "hardlink").Sum(e => e.Bytes) == DirectoryTree.Inspect(tree.Root, "data").Allocated, "Allocated scans count hard-linked data once");
        await Verify.Reject(() => Task.Run(() => FolderScanner.Scan(fixture.Root, "outside")), "Scanner refuses symlink drilldown");
        await Verify.Reject(() => Task.Run(() => FolderScanner.Scan(fixture.Root, "../etc")), "Scanner refuses parent drilldown");
        fixture.Write("models/weights", new string('x', 16384));
        scan = FolderScanner.Scan(fixture.Root, "models");
        Verify.That(scan.Entries.Single().Name == "weights" && scan.Bytes >= 16384, "Scanner drills into folders and reports allocated bytes");
        scan = FolderScanner.Scan(fixture.Root, "", limit: 2);
        Verify.That(scan.Partial && scan.Visited == 2 && scan.Entries.Count == 4 && scan.Entries.Any(e => e.Bytes is null), "Entry budgets retain direct children with unknown sizes");
        scan = FolderScanner.Scan(fixture.Root, "", duration: TimeSpan.Zero);
        Verify.That(scan.Partial && scan.Visited == 0, "Time budgets stop scanning without discarding rows");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Verify.Reject(() => Task.Run(() => FolderScanner.Scan(fixture.Root, "", cancellationToken: cancelled.Token)), "Scanner honors cancellation");
        using (var sparse = File.Create(fixture.PathOf("sparse"))) sparse.SetLength(128 * 1024 * 1024);
        scan = FolderScanner.Scan(fixture.Root, "");
        Verify.That(scan.Entries.Single(e => e.Name == "sparse").Bytes == DirectoryTree.Inspect(tree.Root, "sparse").Allocated, "Sparse files report allocated blocks rather than logical length");
        for (var i = 0; i < 2002; i++) fixture.Write("many/" + i, "x");
        scan = FolderScanner.Scan(fixture.Root, "many");
        Verify.That(scan.Truncated && scan.Partial && scan.Entries.Count == 2000, "Scanner caps direct children at 2000");
        fixture.Write("held/file", "inside");
        using var held = tree.Open("held", true);
        Directory.Move(fixture.PathOf("held"), fixture.PathOf("moved"));
        Directory.CreateSymbolicLink(fixture.PathOf("held"), "/etc");
        using var inside = DirectoryTree.Child(held, "file");
        Verify.That(DirectoryTree.Inspect(inside).Size == 6, "Held descriptors remain on the selected inode after a path replacement");
        await Verify.Reject(() => Task.Run(() => { using var escaped = tree.Open("held/passwd"); }), "New opens reject a replaced symlink component");
    }
    static async Task Archives()
    {
        using var fixture = new Fixture(); fixture.Write("folder/child/payload", "payload");
        Directory.CreateDirectory(fixture.PathOf("folder/empty"));
        Directory.CreateSymbolicLink(fixture.PathOf("folder/outside"), "/etc");
        await new Runtime().Run(["mkfifo", fixture.PathOf("folder/fifo")]);
        var result = await Run(fixture, "download", "folder");
        using (var zip = new ZipArchive(new MemoryStream(result.Body), ZipArchiveMode.Read))
        {
            Verify.That(zip.GetEntry("folder/empty/") is not null && zip.GetEntry("folder/child/payload") is not null, "Folder ZIP includes empty folders and regular files");
            Verify.That(zip.Entries.All(e => !e.FullName.Contains("outside") && !e.FullName.Contains("fifo")), "Archives skip symlinks and special files");
        }
        File.WriteAllBytes(fixture.PathOf("archive.zip"), result.Body);
        foreach (var format in new[] { "stored", "compressed" })
        {
            var converted = await Run(fixture, format, "archive.zip");
            using var zip = new ZipArchive(new MemoryStream(converted.Body), ZipArchiveMode.Read);
            using var reader = new StreamReader(zip.GetEntry("folder/child/payload")!.Open());
            Verify.That(await reader.ReadToEndAsync() == "payload", "ZIP recompression preserves file contents");
        }
        var sizes = (await Run(fixture, "size", "folder")).Header;
        Verify.That(sizes["sizes"]!.AsObject().Select(e => e.Key).Order().SequenceEqual(new[] { "folder", "folder/child", "folder/empty" }), "Size cache retains only root and immediate-child folder scalars");
        Verify.That(sizes["entries"] is null, "Size responses do not retain file listings");
        using (var tree = new DirectoryTree(fixture.Root))
            Verify.That(FolderScanner.Sizes(tree, "folder", limit: 0)["folder"].Partial, "Incomplete size scans mark the cached root partial");
        fixture.Write("invalid.zip", "invalid");
        await Verify.Reject(() => Run(fixture, "stored", "invalid.zip"), "Invalid ZIP is rejected before a download header");
        var corrupt = result.Body.ToArray();
        for (var index = 0; index < corrupt.Length - 20; index++)
            if (corrupt[index] == 0x50 && corrupt[index + 1] == 0x4b && corrupt[index + 2] == 1 && corrupt[index + 3] == 2) corrupt[index + 16] ^= 1;
        File.WriteAllBytes(fixture.PathOf("corrupt.zip"), corrupt);
        await Verify.Reject(() => Run(fixture, "stored", "corrupt.zip"), "Recompression verifies CRC-32 rather than legitimizing corrupt payloads");
        var encrypted = result.Body.ToArray();
        for (var index = 0; index < encrypted.Length - 10; index++)
            if (encrypted[index] == 0x50 && encrypted[index + 1] == 0x4b && encrypted[index + 2] == 1 && encrypted[index + 3] == 2) encrypted[index + 8] |= 1;
        File.WriteAllBytes(fixture.PathOf("encrypted.zip"), encrypted);
        await Verify.Reject(() => Run(fixture, "stored", "encrypted.zip"), "Encrypted ZIP metadata is rejected before success");
        var operation = new FileOperations(); using var output = new MemoryStream();
        await Verify.Reject(() => operation.Run("compressed", fixture.Root, "encrypted.zip", "", output), "Rejected archive does not publish body bytes");
        Verify.That(!operation.Started && output.Length == 0, "Failed archive validation leaves the protocol ready for an error header");
        fixture.Write("changing", "original");
        using var changing = new ChangingStream(() => File.AppendAllText(fixture.PathOf("changing"), "extra"));
        await Verify.Reject(() => new FileOperations().Run("download", fixture.Root, "changing", "", changing), "File changes interrupt a download instead of silently truncating");
    }
    static async Task Mutations()
    {
        using var fixture = new Fixture(); fixture.Write("folder/file", "payload"); fixture.Write("existing", "original");
        Directory.CreateSymbolicLink(fixture.PathOf("folder/link"), "/etc");
        await Verify.Reject(() => Run(fixture, "delete", "folder"), "Recursive delete requires explicit confirmation");
        await Verify.Reject(() => Run(fixture, "move", "folder", "../escape"), "Move destination cannot escape the root");
        await Verify.Reject(() => Run(fixture, "move", "folder", "existing"), "Atomic move refuses an existing destination");
        Verify.That(File.ReadAllText(fixture.PathOf("existing")) == "original" && Directory.Exists(fixture.PathOf("folder")), "Rejected move preserves both source and destination");
        await Run(fixture, "move", "existing", "folder/renamed");
        Verify.That(File.ReadAllText(fixture.PathOf("folder/renamed")) == "original", "Move resolves both parent folders within the selected root");
        await Verify.Reject(() => Run(fixture, "delete", "", "confirm"), "Root cannot be deleted");
        await Verify.Reject(() => Run(fixture, "move", "", "other"), "Root cannot be moved");
        await Verify.Reject(() => Run(fixture, "delete", "folder/link/passwd", "confirm"), "Delete cannot traverse a symlink");
        await Run(fixture, "delete", "folder", "confirm");
        Verify.That(!Directory.Exists(fixture.PathOf("folder")) && File.Exists("/etc/passwd"), "Recursive delete unlinks links without affecting their targets");
    }
    sealed class ChangingStream(Action change) : MemoryStream
    {
        bool changed;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        {
            if (!changed) { changed = true; change(); }
            return base.WriteAsync(bytes, token);
        }
    }
}
