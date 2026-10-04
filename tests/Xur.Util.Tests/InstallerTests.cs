using System.Text.Json.Nodes;
using Xur.Util;

namespace Xur.Util.Tests;

sealed class InstallerUpdater(UpdatePaths paths, Runtime runtime, DurableFiles files) : SignedUpdater(paths, runtime, files, new Downloads())
{
    public Func<CancellationToken, Task<JsonObject>> Online { get; set; } = _ => throw new IOException("Network unavailable");
    public Func<string, bool> Health { get; set; } = identity => identity == "bundled";
    public string Features { get; set; } = "[\"online-installer-v1\"]";
    public override Task<JsonObject> Check(string stage, CancellationToken cancellationToken = default) => Online(cancellationToken);
    public override Task<bool> Healthy(string identity, int seconds = 60, CancellationToken cancellationToken = default) => Task.FromResult(Health(identity));
    public override Task<string> DownloadRelease(JsonObject entry, string stage, CancellationToken cancellationToken = default)
    {
        var bundle = Path.Combine(stage, "bundle");
        Directory.CreateDirectory(Path.Combine(bundle, "host"));
        File.WriteAllText(Path.Combine(bundle, "bundle.json"), new JsonObject { ["id"] = JsonValues.Text(entry["id"]) }.ToJsonString());
        File.WriteAllText(Path.Combine(bundle, "host/application-features.json"), Features);
        return Task.FromResult(bundle);
    }
}

static class InstallerTests
{
    public static async Task Run()
    {
        using var fixture = new Fixture();
        var paths = new InstallerPaths(fixture.PathOf("app"), fixture.PathOf("bundled"), fixture.PathOf("ready"), fixture.PathOf("channel"),
            fixture.PathOf("cmdline"), fixture.PathOf("approved.ks"), fixture.PathOf("config"), fixture.PathOf("key"), fixture.PathOf("run"));
        fixture.Write("bundled/bundle.json", "{\"id\":\"bundled\",\"buildSequence\":5}");
        fixture.Write("cmdline", "xur.installer=1");
        var runtime = new FakeRuntime();
        var files = new DurableFiles();
        var updater = new InstallerUpdater(new UpdatePaths(paths.Root, paths.Config, paths.Key, paths.RuntimeDirectory, paths.CommandLine), runtime, files);
        var forbidden = true;
        var bootstrap = new InstallerBootstrap(paths, runtime, files, new Downloads(), () =>
        {
            if (forbidden) throw new Exception("Default boot initialized online updater");
            return updater;
        }) { CheckTimeout = TimeSpan.FromMilliseconds(100) };
        string State() => JsonValues.Text(DurableFiles.ReadObject(fixture.PathOf("app/check.json"))["state"])!;
        string Selected() => Directory.ResolveLinkTarget(fixture.PathOf("app/current"), true)!.FullName;
        await bootstrap.Prepare();
        Verify.That(Selected() == paths.Bundled && !File.Exists(paths.Ready) && runtime.Commands.Count == 0, "Default installer boot uses bundled executables without network or service commands");
        forbidden = false;
        await bootstrap.Verify();
        Verify.That(File.Exists(paths.Ready), "Healthy bundled application grants readiness");
        await bootstrap.Check();
        Verify.That(State() == "unavailable" && File.Exists(paths.Ready) && Selected() == paths.Bundled && runtime.Commands.Count == 0, "Offline check cannot interrupt ready installer");
        updater.Online = _ => Task.FromResult(new JsonObject { ["id"] = new string('b', 64), ["version"] = "2" });
        await bootstrap.Check();
        Verify.That(State() == "available" && Selected() == paths.Bundled && runtime.Commands.Count == 0, "Late connectivity reports availability without activation");
        Verify.That(JsonValues.Integer(DurableFiles.ReadJson(fixture.PathOf("app/highest-sequence.json"))) == 5, "Installer retains bundled replay floor");
        updater.Online = _ => Task.FromResult(new JsonObject { ["id"] = "bundled" });
        await bootstrap.Check();
        Verify.That(State() == "current", "Current release check reports current");
        updater.Online = async token => { await Task.Delay(5000, token); throw new IOException("Not cancelled"); };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await bootstrap.Check();
        Verify.That(clock.Elapsed < TimeSpan.FromSeconds(2) && State() == "unavailable" && File.Exists(paths.Ready), "Hung background check has independent deadline and cannot revoke readiness");
        forbidden = true;
        fixture.Write("approved.ks", "approved");
        await bootstrap.Check();
        File.Delete(paths.Approved);
        fixture.Write("cmdline", "xur.installer=1 xur.app-update=off");
        await bootstrap.Prepare();
        await bootstrap.Check();
        Verify.That(State() == "disabled" && !File.Exists(paths.Ready), "Explicit off makes no online calls");
        forbidden = false;
        fixture.Write("cmdline", "xur.installer=1 xur.app-update=on");
        updater.Online = _ => throw new IOException("Network unavailable");
        await bootstrap.Prepare();
        Verify.That(State() == "unavailable" && Selected() == paths.Bundled, "Explicit online refresh falls back when offline");
        await bootstrap.Verify();
        fixture.Write("broken/bundle.json", "{\"id\":\"broken\"}");
        bootstrap.Select(fixture.PathOf("broken"));
        File.Delete(paths.Ready);
        await bootstrap.Verify();
        Verify.That(Selected() == paths.Bundled && File.Exists(paths.Ready) && State() == "fallback", "Signed but unhealthy live release restores bundled app before readiness");
        Verify.That(runtime.Commands.TakeLast(2).Select(command => string.Join(' ', command)).SequenceEqual(["systemctl stop xur-control xur-agent xur-gateway", "systemctl start xur-gateway xur-agent xur-control"]), "Live fallback preserves service restart ordering");
        bootstrap.Select(fixture.PathOf("broken"));
        fixture.Write("approved.ks", "approved");
        runtime.Commands.Clear();
        await Verify.Reject(async () => await bootstrap.Verify(), "Approved installer cannot replace app", "already approved");
        Verify.That(Selected() == fixture.PathOf("broken") && runtime.Commands.Count == 0, "Approval protects selection and running services");
        File.Delete(paths.Approved);
        updater.Health = _ => false;
        File.Delete(paths.Ready);
        await Verify.Reject(async () => await bootstrap.Verify(), "Unhealthy bundled fallback cannot grant readiness", "health check failed");
        Verify.That(!File.Exists(paths.Ready), "Failed bundled health retains installer lock");
        updater.Health = identity => identity == "bundled";
        updater.Online = _ => Task.FromResult(new JsonObject { ["id"] = new string('b', 64), ["version"] = "2" });
        updater.Features = "[]";
        await bootstrap.Prepare();
        Verify.That(Selected() == paths.Bundled && State() == "unavailable", "Signed release without installer feature contract is rejected");
        updater.Features = "[\"online-installer-v1\"]";
        await bootstrap.Prepare();
        Verify.That(Selected() == fixture.PathOf("app/releases/" + new string('b', 64)) && !File.Exists(paths.Ready), "Explicit pre-start refresh selects compatible signed release before health approval");
        await bootstrap.Verify();
        Verify.That(Selected() == paths.Bundled && File.Exists(paths.Ready), "Unhealthy refreshed release falls back before disk approval");
    }
}
