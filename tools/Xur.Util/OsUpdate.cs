using System.Text;
using System.Text.Json.Nodes;
using Xur.IO;

namespace Xur.Util;

/// <summary>Standalone bootc operations, with durable progress and a separate power guard.</summary>
public sealed class OsUpdate(string state, string configuration, string installed, Runtime runtime, DurableFiles files)
{
    public const string Channel = "ghcr.io/ublue-os/bazzite-nvidia-open:stable";
    string PathOf(string name) => Path.Combine(state, name);
    static JsonObject? Entry(JsonNode? value)
    {
        if (value?["image"] is null) return null;
        var image = value["image"] as JsonObject ?? throw new InvalidDataException("Invalid bootc image status.");
        var digest = JsonValues.Text(image["imageDigest"]);
        var reference = JsonValues.Text(image["image"]?["image"]);
        if (string.IsNullOrEmpty(digest) || string.IsNullOrEmpty(reference)) throw new InvalidDataException("Incomplete bootc image status.");
        return new() { ["version"] = JsonValues.Text(image["version"]) is { Length: >0 } version ? version : digest,
            ["digest"] = digest, ["image"] = reference,
            ["downloadOnly"] = JsonValues.Boolean(value["downloadOnly"]) };
    }
    public static JsonObject Snapshot(JsonObject raw)
    {
        var status = raw["status"] as JsonObject ?? throw new InvalidDataException("Invalid bootc status.");
        return new() { ["current"] = Entry(status["booted"]), ["pending"] = Entry(status["staged"]), ["previous"] = Entry(status["rollback"]),
            ["available"] = Entry(new JsonObject { ["image"] = status["booted"]?["cachedUpdate"]?.DeepClone() }),
            ["rollbackQueued"] = JsonValues.Text(raw["spec"]?["bootOrder"]) == "rollback" };
    }
    async Task<JsonObject> BootStatus(CancellationToken token) => Snapshot(JsonNode.Parse(await runtime.Run(["bootc", "status", "--json"], 30, token))!.AsObject());
    bool Automatic() => !File.Exists(PathOf("settings.json")) || JsonValues.RequiredBoolean(DurableFiles.ReadObject(PathOf("settings.json"))["automatic"]);
    public bool PowerBlocked() => FileLease.Busy(PathOf("power.lock"));
    public async Task<JsonObject> Observe(CancellationToken token = default)
    {
        var busy = FileLease.Busy(PathOf("lock"));
        var result = busy ? DurableFiles.ReadJson(PathOf("deployment.json")) as JsonObject : null;
        result ??= await BootStatus(token);
        result["automatic"] = Automatic(); result["operation"] = DurableFiles.ReadJson(PathOf("operation.json"));
        result["logs"] = Tail(PathOf("operation.log")); result["busy"] = busy;
        if (!busy && result["operation"] is JsonObject operation && JsonValues.Text(operation["stage"]) == "Running")
        { operation["stage"] = "Interrupted"; operation["message"] = "Operation interrupted. Current deployment status is shown above."; }
        return result;
    }
    static string Tail(string path)
    {
        if (!File.Exists(path)) return "";
        using var stream = File.OpenRead(path); stream.Position = Math.Max(0, stream.Length - 96000);
        using var reader = new StreamReader(stream, Encoding.UTF8); var text = reader.ReadToEnd();
        return text.Length > 24000 ? text[^24000..] : text;
    }
    public static void VerifyStage(JsonObject after)
    {
        if (after["pending"] is not null && JsonValues.Text(after["pending"]?["image"]) != Channel) throw new UserError("Unexpected staged image");
        if (after["pending"] is null && after["available"] is not null && JsonValues.Text(after["available"]?["digest"]) != JsonValues.Text(after["current"]?["digest"]))
            throw new UserError("An OS update is available but no deployment was staged. Review the update log and retry.");
    }
    public async Task<JsonObject?> Execute(string action, CancellationToken token = default, uint? user = null)
    {
        if ((user ?? Linux.EffectiveUser()) != 0) throw new UserError("Root is required");
        // Recovery power checks must not depend on configuration, bootc or old operation records.
        if (action == "power-status") return new() { ["blocked"] = PowerBlocked() };
        if (!File.Exists(installed)) throw new UserError("Updates are available after installation");
        if (JsonValues.Text(DurableFiles.ReadObject(configuration)["channel"]) != Channel) throw new UserError("Unsupported upstream update source");
        if (action == "status") return await Observe(token);
        if (action is not ("check" or "stage" or "rollback" or "auto" or "enable" or "disable")) throw new UserError("Unknown update action");
        Directory.CreateDirectory(state); File.SetUnixFileMode(state, (UnixFileMode)448);
        using var lease = FileLease.TryAcquire(PathOf("lock")) ?? throw new UserError("An OS update operation is already running");
        if (action is "enable" or "disable") { files.WriteJson(PathOf("settings.json"), new JsonObject { ["automatic"] = action == "enable" }); return null; }
        if (action == "auto" && !Automatic()) return null;
        var before = await BootStatus(token); files.WriteJson(PathOf("deployment.json"), before);
        if (JsonValues.Text(before["current"]?["image"]) != Channel) throw new UserError("The booted deployment does not track the configured upstream channel");
        if (before["pending"] is not null || JsonValues.Boolean(before["rollbackQueued"]))
        { if (action == "auto") return null; throw new UserError("A deployment is already queued. Reboot before changing it"); }
        if (action == "rollback" && before["previous"] is null) throw new UserError("No previous deployment is available");
        var operation = new JsonObject { ["id"] = Guid.NewGuid().ToString("N"), ["action"] = action, ["stage"] = "Running", ["updated"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d, ["message"] = "" };
        files.WriteJson(PathOf("operation.json"), operation);
        await using var log = new FileStream(PathOf("operation.log"), FileMode.Create, FileAccess.Write, FileShare.Read);
        async Task Run(string[] command, int seconds)
        {
            // Stream both pipes to disk: multi-hour pulls must not accumulate in memory.
            await runtime.RunLogged(command, log, seconds, token);
        }
        try
        {
            if (action is "check" or "auto") await Run(["bootc", "upgrade", "--check"], 600);
            if (action is "stage" or "auto")
            {
                using var guard = await FileLease.Acquire(PathOf("power.lock"), token);
                await Run(["bootc", "switch", "--enforce-container-sigpolicy", Channel], 7200);
                if ((await BootStatus(token))["pending"] is null) await Run(["bootc", "upgrade"], 7200);
            }
            if (action == "rollback")
            { using var guard = await FileLease.Acquire(PathOf("power.lock"), token); await Run(["bootc", "rollback"], 300); }
            var after = await BootStatus(token);
            if (action == "rollback" && !JsonValues.Boolean(after["rollbackQueued"])) throw new UserError("Boot order did not change");
            if (after["pending"] is not null && JsonValues.Text(after["pending"]?["image"]) != Channel) throw new UserError("Unexpected staged image");
            if (action is "stage" or "auto") VerifyStage(after);
            operation["stage"] = "Complete";
            operation["message"] = after["pending"] is not null || JsonValues.Boolean(after["rollbackQueued"]) ? "Reboot to finish" : action is "stage" or "auto" ? "Already up to date" : "Check complete";
        }
        catch (Exception error) { operation["stage"] = "Failed"; operation["message"] = error.Message; throw; }
        finally { operation["updated"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d; files.WriteJson(PathOf("operation.json"), operation); }
        return null;
    }
}
