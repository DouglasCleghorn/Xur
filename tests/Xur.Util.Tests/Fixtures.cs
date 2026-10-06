using System.Text;
using System.Text.Json.Nodes;
using Xur.Util;

namespace Xur.Util.Tests;

static class Verify
{
    public static int Count { get; private set; }
    public static void That(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
        Count++;
    }
    public static async Task Reject(Func<Task> action, string description, string? message = null)
    {
        try { await action(); }
        catch (Exception error)
        {
            if (error is not (UserError or IOException or InvalidDataException or OperationCanceledException or ArgumentException or System.Text.Json.JsonException)) throw;
            That(message is null || error.Message.Contains(message, StringComparison.Ordinal), description + ": " + error.Message);
            return;
        }
        throw new Exception(description);
    }
}

sealed class Fixture : IDisposable
{
    public string Root { get; }
    public Fixture()
    {
        Directory.CreateDirectory(".build/evidence/xurutil");
        Root = Path.GetFullPath(Path.Combine(".build/evidence/xurutil", "fixture-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Root);
    }
    public string PathOf(string relative) => Path.Combine(Root, relative);
    public void Write(string relative, string text)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
    public void Dispose() => Directory.Delete(Root, recursive: true);
}

class FakeRuntime : Runtime
{
    public List<string[]> Commands { get; } = [];
    public Func<string[], int, Task<byte[]>>? OnRun { get; set; }
    public Func<string, string, Task<JsonObject>>? OnLocal { get; set; }
    public override Task<byte[]> Run(string[] command, int seconds = 60, CancellationToken cancellationToken = default, byte[]? input = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Commands.Add(command);
        return OnRun?.Invoke(command, seconds) ?? Task.FromResult(Array.Empty<byte>());
    }
    public override Task<JsonObject> Local(string endpoint, string socket, CancellationToken cancellationToken = default) =>
        OnLocal?.Invoke(endpoint, socket) ?? Task.FromResult(new JsonObject { ["active"] = 0, ["profileBusy"] = false, ["busy"] = false });
    public override Task Delay(CancellationToken cancellationToken) => Task.Delay(1, cancellationToken);
}

sealed class FaultyFiles : DurableFiles
{
    public Action<string>? BeforeWrite { get; set; }
    public Action<string>? BeforeSync { get; set; }
    public override void Write(string path, byte[] content, uint mode = 0x180, uint? uid = null, uint? gid = null, Action<Linux.Metadata>? published = null)
    {
        BeforeWrite?.Invoke(path);
        base.Write(path, content, mode, uid, gid, published);
    }
    public override void SyncDirectory(string path)
    {
        BeforeSync?.Invoke(path);
        base.SyncDirectory(path);
    }
}

sealed class FixtureDownloads : Downloads
{
    public List<string> Requests { get; } = [];
    public required Func<string, string, long, string?> Supply { get; set; }
    public override async Task<DownloadReceipt> FetchReceipt(string url, string path, long limit, CancellationToken cancellationToken = default)
    {
        var pinned = await Fetch(url, path, limit, cancellationToken);
        return new(pinned, new Xur.IO.TransferReceipt(new FileInfo(path).Length, DurableFiles.HashFile(path)));
    }
    public override Task<string?> Fetch(string url, string path, long limit, CancellationToken cancellationToken = default)
    {
        Requests.Add(url);
        return Task.FromResult(Supply(url, path, limit));
    }
}

sealed class HealthyUpdater(UpdatePaths paths, Runtime runtime, DurableFiles files, Downloads downloads) : SignedUpdater(paths, runtime, files, downloads)
{
    public Func<string, bool> Health { get; set; } = _ => true;
    public override Task<bool> Healthy(string identity, int seconds = 60, CancellationToken cancellationToken = default) => Task.FromResult(Health(identity));
}
