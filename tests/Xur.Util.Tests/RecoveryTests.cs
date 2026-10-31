using System.Text;
using System.Text.Json.Nodes;
using Xur.Util;

namespace Xur.Util.Tests;

static class RecoveryTests
{
    static readonly string Previous = new('a', 64), Target = new('b', 64);
    static UpdatePaths Paths(string root) => new(Path.Combine(root, "app"), Path.Combine(root, "config"), Path.Combine(root, "key"));

    public static async Task Run()
    {
        await Publication();
        foreach (var boundary in new[] { "draining", "stopping", "starting", "healthy", "committed" })
        {
            using var fixture = new Fixture();
            foreach (var identity in new[] { Previous, Target })
                fixture.Write("app/releases/" + identity + "/bundle.json", new JsonObject { ["id"] = identity }.ToJsonString());
            Linux.PublishLink(fixture.PathOf("app/current"), "releases/" + Previous);
            var native = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
            var executable = native ? Environment.ProcessPath! : Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../../dotnet"));
            var arguments = native ? Array.Empty<string>() : new[] { Path.Combine(AppContext.BaseDirectory, "Xur.Util.Tests.dll") };
            var child = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false };
            foreach (var argument in arguments.Concat(["--update-crash-fixture", fixture.Root, boundary])) child.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(child)!;
            await process.WaitForExitAsync();
            Verify.That(process.ExitCode == 86, "Updater exits abruptly at " + boundary + " without running catch/finally recovery");
            var runtime = new FakeRuntime();
            var updater = new HealthyUpdater(Paths(fixture.Root), runtime, new DurableFiles(), new Downloads());
            await updater.Execute("prepare-recovery", []);
            await updater.Execute("prepare-recovery", []);
            Verify.That(JsonValues.Text(updater.Current()["id"]) == (boundary == "committed" ? Target : Previous), "Boot selects a known release before starting services after " + boundary);
            Verify.That(runtime.Commands.Count == 0, "Early recovery never starts or stops services synchronously");
            if (boundary is "stopping" or "starting" or "healthy")
            {
                // Losing the maintenance marker must not hide a surviving transaction.
                File.Delete(fixture.PathOf("app/maintenance"));
                updater.Health = _ => false;
                await Verify.Reject(async () => await updater.Execute("recover", []), "Unhealthy rollback keeps durable retry state", "retained for recovery");
                Verify.That(File.Exists(fixture.PathOf("app/transaction.json")), "Recovery can retry even without a maintenance marker");
                updater.Health = _ => true;
                runtime.Commands.Clear();
                await updater.Execute("recover", []);
                Verify.That(runtime.Commands.Select(command => string.Join(' ', command)).SequenceEqual([
                    "systemctl stop xur-control xur-agent xur-gateway",
                    "systemctl reset-failed xur-control xur-agent xur-gateway",
                    "systemctl start xur-gateway xur-agent xur-control"
                ]), "Recovery clears restart limits before starting the previous app");
            }
            Verify.That(!File.Exists(fixture.PathOf("app/transaction.json")) && !File.Exists(fixture.PathOf("app/maintenance")), "Successful boot recovery clears interruption state after " + boundary);
        }
    }

    public static async Task CrashFixture(string root, string boundary)
    {
        var paths = Paths(root);
        var runtime = new FakeRuntime();
        var files = new FaultyFiles();
        runtime.OnLocal = (_, _) =>
        {
            if (boundary == "draining") Environment.Exit(86);
            return Task.FromResult(new JsonObject { ["active"] = 0, ["profileBusy"] = false, ["busy"] = false });
        };
        runtime.OnRun = (command, _) =>
        {
            if (command[0] == "systemctl" && command[1] == (boundary == "stopping" ? "stop" : boundary == "starting" ? "start" : "")) Environment.Exit(86);
            return Task.FromResult(Array.Empty<byte>());
        };
        files.BeforeWrite = path =>
        {
            if (boundary == "healthy" && path == Path.Combine(paths.Root, "previous.json")) Environment.Exit(86);
        };
        files.BeforeSync = path =>
        {
            if (boundary == "committed" && path == paths.Root && !File.Exists(Path.Combine(paths.Root, "transaction.json")) &&
                !File.Exists(Path.Combine(paths.Root, "maintenance")) && File.Exists(Path.Combine(paths.Root, "previous.json"))) Environment.Exit(86);
        };
        var updater = new HealthyUpdater(paths, runtime, files, new Downloads());
        await updater.Activate(new JsonObject { ["id"] = Target });
        throw new Exception("Crash boundary was not reached");
    }

    static async Task Publication()
    {
        using var fixture = new Fixture();
        fixture.Write("app/releases/" + Previous + "/bundle.json", new JsonObject { ["id"] = Previous }.ToJsonString());
        Linux.PublishLink(fixture.PathOf("app/current"), "releases/" + Previous);
        var runtime = new FakeRuntime();
        var files = new FaultyFiles();
        var updater = new SignedUpdater(Paths(fixture.Root), runtime, files, new Downloads());
        var content = new JsonObject();
        foreach (var name in new[] { "control/Xur.Control", "agent/Xur.Agent", "gateway/Xur.Gateway", "host/app-update" })
        {
            fixture.Write("bundle/" + name, name);
            File.SetUnixFileMode(fixture.PathOf("bundle/" + name), (UnixFileMode)0x1c0);
            content[name] = DurableFiles.Hash(Encoding.UTF8.GetBytes(name));
        }
        var identity = DurableFiles.Hash(Encoding.UTF8.GetBytes(BundleArchive.CanonicalManifest(content)));
        var entry = new JsonObject { ["id"] = identity };
        fixture.Write("bundle/bundle.json", new JsonObject { ["id"] = identity, ["hostAbi"] = 1, ["files"] = content }.ToJsonString());
        var release = fixture.PathOf("app/releases/" + identity);
        var synced = new List<string>();
        files.BeforeTree = path =>
        {
            Verify.That(runtime.Commands.Any(command => command[0] == "restorecon"), "Release labels are applied before flushing metadata");
            if (path == release) throw new IOException("Interrupted release flush");
        };
        await Verify.Reject(async () => await updater.StageRelease(fixture.PathOf("bundle"), entry), "Release flush failure stops publication");
        Verify.That(JsonValues.Text(updater.Current()["id"]) == Previous && !File.Exists(fixture.PathOf("app/maintenance")) &&
            runtime.Commands.All(command => command[0] != "systemctl"), "Staging failures retain the previous selection without draining or stopping services");
        files.BeforeTree = null;
        files.BeforeSync = path => { if (path == fixture.PathOf("app/releases")) throw new IOException("Release directory flush failed"); };
        await Verify.Reject(async () => await updater.StageRelease(fixture.PathOf("bundle"), entry), "Publication directory flush failure prevents activation");
        Verify.That(JsonValues.Text(updater.Current()["id"]) == Previous, "Failed publication flush retains the previous selection");
        files.BeforeSync = path => synced.Add(path);
        await updater.StageRelease(fixture.PathOf("bundle"), entry);
        Verify.That(synced.Contains(Path.Combine(release, "control")) && synced.Contains(Path.Combine(release, "agent")) &&
            synced.TakeLast(3).SequenceEqual([release, fixture.PathOf("app/releases"), fixture.PathOf("app")]), "Retry flushes the complete release tree and publication ancestors in order");
        updater.Link(identity);
        Verify.That(JsonValues.Text(updater.Current()["id"]) == identity, "Durable staged release can be selected");
        File.AppendAllText(Path.Combine(release, "control/Xur.Control"), "corruption");
        await Verify.Reject(async () => await updater.StageRelease(fixture.PathOf("bundle"), entry), "Interrupted existing release is revalidated before reuse", "hash mismatch");
    }
}
