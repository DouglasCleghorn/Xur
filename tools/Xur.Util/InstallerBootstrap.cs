using System.Text.Json.Nodes;

namespace Xur.Util;

public sealed record InstallerPaths(string Root = "/run/xur/app", string Bundled = "/usr/share/xur/app-bundle",
    string Ready = "/run/xur/installer-app-ready", string Channel = "/usr/share/xur/installer-channel",
    string CommandLine = "/proc/cmdline", string Approved = "/run/xur/approved.ks",
    string Config = "/run/xur/application-updates.json", string Key = "/usr/share/xur/application-update-key.pem",
    string RuntimeDirectory = "/run/xur");

/// <summary>Default boot is entirely local; online checks cannot interrupt an approved installer.</summary>
public sealed class InstallerBootstrap(InstallerPaths paths, Runtime runtime, DurableFiles files, Downloads downloads,
    Func<SignedUpdater>? factory = null)
{
    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan RefreshTimeout { get; init; } = TimeSpan.FromSeconds(120);

    string Mode()
    {
        var arguments = File.ReadAllText(paths.CommandLine).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (arguments.Contains("xur.app-update=off")) return "off";
        return arguments.Contains("xur.app-update=on") ? "on" : "background";
    }

    void Status(string state)
    {
        Directory.CreateDirectory(paths.Root);
        files.WriteJson(Path.Combine(paths.Root, "check.json"), new JsonObject { ["state"] = state });
    }

    public void Select(string bundle)
    {
        Directory.CreateDirectory(paths.Root);
        Linux.PublishLink(Path.Combine(paths.Root, "current"), bundle);
    }

    async Task<SignedUpdater> Updater(CancellationToken cancellationToken)
    {
        var updater = factory?.Invoke() ?? new SignedUpdater(
            new UpdatePaths(paths.Root, paths.Config, paths.Key, paths.RuntimeDirectory, paths.CommandLine), runtime, files, downloads);
        var channel = File.Exists(paths.Channel) ? File.ReadAllText(paths.Channel).Trim() : "stable";
        if (channel is not ("stable" or "nightly")) throw new UserError("Invalid installer update channel");
        await updater.SelectChannel(channel, cancellationToken: cancellationToken);
        return updater;
    }

    void Initialize(SignedUpdater updater)
    {
        var bundled = DurableFiles.ReadObject(Path.Combine(paths.Bundled, "bundle.json"));
        var current = DurableFiles.ReadObject(Path.Combine(paths.Root, "current/bundle.json"));
        var floorPath = Path.Combine(paths.Root, "highest-sequence.json");
        var floor = Math.Max(JsonValues.Integer(bundled["buildSequence"]) ?? 0, JsonValues.Integer(current["buildSequence"]) ?? 0);
        floor = Math.Max(floor, JsonValues.Integer(DurableFiles.ReadJson(floorPath)) ?? 0);
        files.WriteJson(floorPath, JsonValue.Create(floor));
        var sequencePath = Path.Combine(paths.Root, "channel-sequences.json");
        var sequences = DurableFiles.ReadObject(sequencePath);
        sequences[updater.Channel] = Math.Max(JsonValues.Integer(sequences[updater.Channel]) ?? 0, floor);
        files.WriteJson(sequencePath, sequences);
    }

    public async Task Prepare(CancellationToken cancellationToken = default)
    {
        Select(paths.Bundled);
        File.Delete(paths.Ready);
        var mode = Mode();
        Status(mode == "off" ? "disabled" : "pending");
        // Health controls disk approval. Ordinary boot makes no network calls.
        if (mode == "on") await Refresh(cancellationToken);
    }

    public async Task Check(CancellationToken cancellationToken = default)
    {
        if (Mode() == "off") { Status("disabled"); return; }
        if (File.Exists(paths.Approved)) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(CheckTimeout);
        try
        {
            Status("checking");
            var updater = await Updater(deadline.Token);
            Initialize(updater);
            using var stage = new TemporaryDirectory(paths.Root, "check-");
            var entry = await updater.Check(stage.Path, deadline.Token);
            var identity = JsonValues.Text(DurableFiles.ReadObject(Path.Combine(paths.Root, "current/bundle.json"))["id"]);
            Status(JsonValues.Text(entry["id"]) == identity ? "current" : "available");
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Checking never changes the selection, readiness marker, or running services.
            Status("unavailable");
        }
    }

    public async Task Refresh(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RefreshTimeout);
        try
        {
            Status("refreshing");
            var updater = await Updater(deadline.Token);
            Initialize(updater);
            using var stage = new TemporaryDirectory(paths.Root, "download-");
            var entry = await updater.Check(stage.Path, deadline.Token);
            if (JsonValues.Text(entry["id"]) == JsonValues.Text(DurableFiles.ReadObject(Path.Combine(paths.Bundled, "bundle.json"))["id"]))
            {
                Status("current");
                return;
            }
            var bundle = await updater.DownloadRelease(entry, stage.Path, deadline.Token);
            var features = DurableFiles.ReadJson(Path.Combine(bundle, "host/application-features.json")) as JsonArray;
            if (features is null || !features.Any(value => JsonValues.Text(value) == "online-installer-v1"))
                throw new UserError("Release does not support this installer");
            var release = await updater.StageRelease(bundle, entry, 30, deadline.Token);
            files.WriteJson(Path.Combine(paths.Root, "release-" + JsonValues.RequiredText(entry["id"]) + ".json"), entry);
            Select(release);
            Status("current");
            Console.WriteLine("Using signed Xur release " + JsonValues.Text(entry["version"]));
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Select(paths.Bundled);
            Status("unavailable");
            Console.Error.WriteLine("Keeping bundled Xur: " + error.Message);
        }
    }

    public async Task Verify(CancellationToken cancellationToken = default)
    {
        var updater = await Updater(cancellationToken);
        var identity = JsonValues.RequiredText(DurableFiles.ReadObject(Path.Combine(paths.Root, "current/bundle.json"))["id"]);
        if (!await updater.Healthy(identity, 45, cancellationToken))
        {
            if (File.Exists(paths.Approved)) throw new UserError("Installation already approved; refusing app replacement");
            await runtime.Run(["systemctl", "stop", .. SignedUpdater.Services], 60, cancellationToken);
            Select(paths.Bundled);
            await runtime.Run(["systemctl", "start", "xur-gateway", "xur-agent", "xur-control"], 60, cancellationToken);
            var bundled = JsonValues.RequiredText(DurableFiles.ReadObject(Path.Combine(paths.Bundled, "bundle.json"))["id"]);
            if (!await updater.Healthy(bundled, 45, cancellationToken)) throw new UserError("Bundled installer app health check failed");
            Status("fallback");
        }
        files.Write(paths.Ready, [], 0x180);
    }
}
