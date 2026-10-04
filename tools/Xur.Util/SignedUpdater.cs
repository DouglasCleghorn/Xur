using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Xur.Util;

public sealed record UpdatePaths(string Root, string Config, string Key,
    string RuntimeDirectory = "/run/xur", string CommandLine = "/proc/cmdline")
{
    public static UpdatePaths Installed => new("/var/lib/xur/app", "/etc/xur/application-updates.json", "/etc/xur/application-update-key.pem");
}

/// <summary>Signed updates and recovery execute independently of the application services.</summary>
public class SignedUpdater(UpdatePaths paths, Runtime runtime, DurableFiles files, Downloads downloads)
{
    public static readonly string[] Services = ["xur-control", "xur-agent", "xur-gateway"];
    static readonly Regex HashPattern = new("\\A[a-f0-9]{64}\\z", RegexOptions.CultureInvariant);
    static readonly Regex NightlyPattern = new("\\Anightly-[0-9]+(?:[.][0-9]+)+\\z", RegexOptions.CultureInvariant);
    static readonly Regex StablePattern = new("\\Av[0-9]+(?:[.][0-9]+)+\\z", RegexOptions.CultureInvariant);
    public const string Descriptor = "xur-update.json";
    public const string Archive = "xur-update-x86_64.tar.gz";
    static readonly Regex KeyPattern = new("\\A-----BEGIN PUBLIC KEY-----\\s+[A-Za-z0-9+/=\\s]+-----END PUBLIC KEY-----\\s*\\z", RegexOptions.CultureInvariant);
    public UpdatePaths Paths => paths;
    public Runtime Operations => runtime;
    public DurableFiles Files => files;
    public string Operation { get; } = Guid.NewGuid().ToString("N");
    string State => Path.Combine(paths.Root, "update.json");
    string Transaction => Path.Combine(paths.Root, "transaction.json");
    string Maintenance => Path.Combine(paths.Root, "maintenance");
    string AtRoot(string name) => Path.Combine(paths.Root, name);
    public static bool IsHash(string? value) => value is not null && HashPattern.IsMatch(value);
    public JsonObject Config => DurableFiles.ReadObject(paths.Config);
    public string Channel => JsonValues.Boolean(Config["development"]) ? "local" : JsonValues.Text(Config["channel"]) ?? "stable";
    public string Source => Channel == "local" ? JsonValues.Text(Config["server"]) ?? "" : Channel == "nightly" ? Downloads.Nightly : Downloads.Stable;
    public string TrustKey => Channel == "local" && JsonValues.Text(Config["publicKey"]) is { } key ? key : File.ReadAllText(paths.Key);
    public string SequenceScope => Channel == "local" ? "local:" + DurableFiles.Hash(Encoding.UTF8.GetBytes(Source + "\n" + TrustKey)) : Channel;

    public static string NormalizeServer(string value)
    {
        if (value.Length > 2048 || string.IsNullOrWhiteSpace(value)) throw new UserError("Enter the update server IP or domain");
        value = value.Trim();
        if (!value.Contains("://", StringComparison.Ordinal)) value = "http://" + value;
        if (value.Contains('\\') || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new UserError("Enter an IP or domain, optionally with scheme and port");
        var authority = value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..].TrimEnd('/');
        var explicitPort = authority.StartsWith('[') ? authority.Contains("]:", StringComparison.Ordinal) : authority.Contains(':');
        var port = explicitPort && uri.Port != 0 ? uri.Port : 8088;
        var host = uri.IdnHost.Trim('[', ']');
        if (host.Contains(':')) host = "[" + host + "]";
        return uri.Scheme + "://" + host + ":" + port;
    }

    public async Task<string> PublicKey(string value, CancellationToken cancellationToken = default)
    {
        if (value.Length > 4096 || !KeyPattern.IsMatch(value.Trim()))
            throw new UserError("Enter an Ed25519 signing public key in PEM format, not a private key");
        byte[] encoded;
        try { encoded = await runtime.Run(["openssl", "pkey", "-pubin", "-outform", "DER"], 5, cancellationToken, Encoding.UTF8.GetBytes(value)); }
        catch (IOException) { throw new UserError("Invalid signing public key"); }
        if (encoded.Length != 44 || !encoded.AsSpan(0, 12).SequenceEqual(Convert.FromHexString("302a300506032b6570032100")))
            throw new UserError("The signing public key must use Ed25519");
        return "-----BEGIN PUBLIC KEY-----\n" + Convert.ToBase64String(encoded) + "\n-----END PUBLIC KEY-----\n";
    }

    public async Task SelectChannel(string channel, string? server = null, string? key = null, CancellationToken cancellationToken = default)
    {
        if (channel is not ("nightly" or "stable" or "local")) throw new UserError("Choose nightly, stable or local build testing");
        var config = Config;
        if (channel == "local")
        {
            config["server"] = NormalizeServer(server ?? "");
            config["publicKey"] = await PublicKey(key ?? "", cancellationToken);
        }
        config["channel"] = channel == "local" ? JsonValues.Text(config["channel"]) ?? "stable" : channel;
        config["development"] = channel == "local";
        files.WriteJson(paths.Config, config);
        File.Delete(AtRoot("available.json"));
    }

    public void Configure(string server)
    {
        if (Channel != "local") throw new UserError("Enable local build testing in Settings first");
        var config = Config;
        config["server"] = NormalizeServer(server);
        files.WriteJson(paths.Config, config);
        File.Delete(AtRoot("available.json"));
    }

    public void Development(bool enabled)
    {
        var config = Config;
        config["development"] = enabled;
        files.WriteJson(paths.Config, config);
        File.Delete(AtRoot("available.json"));
    }

    public JsonObject Current()
    {
        var metadata = DurableFiles.ReadObject(AtRoot("current/bundle.json"));
        var identity = JsonValues.Text(metadata["id"]);
        var release = DurableFiles.ReadJson(AtRoot("release-" + (identity ?? "unknown") + ".json")) as JsonObject ?? metadata;
        return new JsonObject { ["id"] = identity, ["version"] = JsonValues.Text(release["version"]) ?? (identity ?? "Unknown")[..Math.Min(12, (identity ?? "Unknown").Length)] };
    }

    public async Task<JsonObject> Status(CancellationToken cancellationToken = default)
    {
        var busy = false;
        try
        {
            var state = Encoding.UTF8.GetString(await runtime.Run(["systemctl", "show", "-p", "ActiveState", "--value", "xur-app-update.service"], 5, cancellationToken)).Trim();
            busy = state is "active" or "activating" or "deactivating";
        }
        catch (IOException) { }
        return new JsonObject
        {
            ["server"] = Source, ["localServer"] = JsonValues.Text(Config["server"]) ?? "",
            ["publicKey"] = Channel == "local" ? TrustKey : JsonValues.Text(Config["publicKey"]) ?? "",
            ["development"] = Channel == "local", ["channel"] = Channel, ["current"] = Current(),
            ["previous"] = DurableFiles.ReadJson(AtRoot("previous.json")), ["available"] = DurableFiles.ReadJson(AtRoot("available.json")),
            ["operation"] = DurableFiles.ReadJson(State), ["busy"] = busy
        };
    }

    async Task VerifySignature(string stage, CancellationToken cancellationToken)
    {
        var key = Path.Combine(stage, "verification-key.pem");
        File.WriteAllText(key, TrustKey);
        await runtime.Run(["openssl", "pkeyutl", "-verify", "-pubin", "-inkey", key, "-rawin", "-in", Path.Combine(stage, "release.json"), "-sigfile", Path.Combine(stage, "release.sig")], 60, cancellationToken);
    }

    void ValidateMetadata(JsonObject entry, int schema)
    {
        if (JsonValues.Integer(entry["schema"]) != schema || JsonValues.Integer(entry["hostAbi"]) != 1 || JsonValues.Integer(entry["dataSchema"]) != 1 || !IsHash(JsonValues.Text(entry["id"])))
            throw new UserError("Release is incompatible with this installation");
        var name = JsonValues.Text(entry["file"]);
        if (name != JsonValues.Text(entry["id"]) + ".tar.gz" && (schema != 2 || name != Archive))
            throw new UserError("Invalid update archive name");
        if (JsonValues.Integer(entry["bytes"]) is null or <= 0 or > 2147483648 || !IsHash(JsonValues.Text(entry["sha256"])))
            throw new UserError("Invalid release metadata");
        var sequence = JsonValues.Integer(entry["sequence"]) ?? throw new UserError("Invalid release sequence");
        if (sequence < 0) throw new UserError("Invalid release sequence");
        if (Channel != "local" && JsonValues.Text(entry["channel"]) != Channel)
            throw new UserError("Signed release does not match the selected channel");
        var floor = JsonValues.Integer(DurableFiles.ReadObject(AtRoot("channel-sequences.json"))[SequenceScope]) ?? 0;
        if (sequence < floor) throw new UserError("Repository offered an older release in this channel; use Roll back for the previous installation");
    }

    public async Task<JsonObject> CheckLocalLegacy(string stage, CancellationToken cancellationToken = default)
    {
        if (Channel != "local") throw new UserError("Legacy update discovery is supported only for local build testing");
        var server = Source;
        if (server.Length == 0) throw new UserError("Set the update server address first");
        await downloads.Fetch(server + "/latest", Path.Combine(stage, "latest"), 128, cancellationToken);
        var identity = File.ReadAllText(Path.Combine(stage, "latest")).Trim();
        if (!IsHash(identity)) throw new UserError("Invalid release identity");
        await downloads.Fetch(server + "/" + identity + ".json", Path.Combine(stage, "release.json"), 65536, cancellationToken);
        await downloads.Fetch(server + "/" + identity + ".json.sig", Path.Combine(stage, "release.sig"), 128, cancellationToken);
        await VerifySignature(stage, cancellationToken);
        var entry = DurableFiles.ReadObject(Path.Combine(stage, "release.json"));
        ValidateMetadata(entry, 1);
        if (JsonValues.Text(entry["id"]) != identity) throw new UserError("Release identity mismatch");
        File.WriteAllText(Path.Combine(stage, "source"), server);
        files.WriteJson(AtRoot("available.json"), entry);
        return entry;
    }

    public virtual async Task<JsonObject> Check(string stage, CancellationToken cancellationToken = default)
    {
        var server = Source;
        if (server.Length == 0) throw new UserError("Set the update server address first");
        var official = Channel != "local";
        try { await downloads.Fetch(server + "/current", Path.Combine(stage, "current"), 128, cancellationToken); }
        catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            if (!official) return await CheckLocalLegacy(stage, cancellationToken);
            File.Delete(AtRoot("available.json"));
            throw new UserError("No " + (Channel == "nightly" ? "Nightly" : "Stable") + " release available. Keep the current installation and check again later.");
        }
        var target = File.ReadAllText(Path.Combine(stage, "current")).Trim();
        string name;
        if (official)
        {
            if (!(Channel == "nightly" ? NightlyPattern : StablePattern).IsMatch(target)) throw new UserError("Invalid release pointer");
            server = Downloads.GitHub + "/download/" + target;
            name = Descriptor;
        }
        else
        {
            if (!IsHash(target)) throw new UserError("Invalid release identity");
            name = target + ".update.json";
        }
        var descriptor = Path.Combine(stage, "descriptor.json");
        await downloads.Fetch(server + "/" + name, descriptor, 65536, cancellationToken);
        var envelope = DurableFiles.ReadObject(descriptor);
        if (JsonValues.Integer(envelope["schema"]) != 2 || envelope["release"] is not JsonObject entry)
            throw new UserError("Invalid update descriptor");
        var encoded = JsonValues.Text(envelope["signature"]);
        // Convert.FromBase64String tolerates whitespace; the protocol does not.
        if (encoded is null || encoded.Any(char.IsWhiteSpace)) throw new UserError("Invalid update signature");
        byte[] signature;
        try { signature = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw new UserError("Invalid update signature"); }
        if (signature.Length != 64) throw new UserError("Invalid update signature");
        File.WriteAllText(Path.Combine(stage, "release.json"), BundleArchive.CanonicalDescriptor(entry));
        File.WriteAllBytes(Path.Combine(stage, "release.sig"), signature);
        await VerifySignature(stage, cancellationToken);
        ValidateMetadata(entry, 2);
        if (!official && JsonValues.Text(entry["id"]) != target) throw new UserError("Release identity mismatch");
        File.WriteAllText(Path.Combine(stage, "source"), server);
        files.WriteJson(AtRoot("available.json"), entry);
        return entry;
    }

    public virtual async Task<string> DownloadRelease(JsonObject entry, string stage, CancellationToken cancellationToken = default)
    {
        var archive = Path.Combine(stage, "bundle.tar.gz");
        var length = JsonValues.Integer(entry["bytes"]) ?? throw new UserError("Invalid release metadata");
        await downloads.Fetch(File.ReadAllText(Path.Combine(stage, "source")) + "/" + JsonValues.RequiredText(entry["file"]), archive, length, cancellationToken);
        if (new FileInfo(archive).Length != length || DurableFiles.HashFile(archive) != JsonValues.Text(entry["sha256"])) throw new UserError("Download hash mismatch");
        var bundle = Path.Combine(stage, "bundle");
        BundleArchive.Unpack(archive, bundle, entry);
        return bundle;
    }

    public async Task<string> StageRelease(string bundle, JsonObject entry, int seconds = 120, CancellationToken cancellationToken = default)
    {
        var identity = JsonValues.RequiredText(entry["id"]);
        if (!IsHash(identity)) throw new UserError("Invalid release identity");
        var release = AtRoot("releases/" + identity);
        Directory.CreateDirectory(Path.GetDirectoryName(release)!);
        if (!Directory.Exists(release)) Directory.Move(bundle, release);
        else BundleArchive.Validate(release, entry);
        await runtime.Run(["restorecon", "-RF", release], seconds, cancellationToken);
        return release;
    }

    public void Link(string identity)
    {
        if (!IsHash(identity) || !File.Exists(AtRoot("releases/" + identity + "/bundle.json"))) throw new UserError("Release not installed");
        Linux.PublishLink(AtRoot("current"), "releases/" + identity);
    }

    public void Progress(string stage, string message) => files.WriteJson(State, new JsonObject
    {
        ["id"] = Operation, ["stage"] = stage, ["message"] = message, ["updated"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    });

    public virtual async Task<bool> Healthy(string identity, int seconds = 60, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            while (true)
            {
                try
                {
                    var control = await runtime.Local("/local/application-health", Path.Combine(paths.RuntimeDirectory, "control.sock"), deadline.Token);
                    var agent = await runtime.Local("/application-health", Path.Combine(paths.RuntimeDirectory, "agent.sock"), deadline.Token);
                    var gateway = await runtime.Local("/health", Path.Combine(paths.RuntimeDirectory, "gateway-admin.sock"), deadline.Token);
                    if (JsonValues.Text(control["id"]) == identity && JsonValues.Text(agent["id"]) == identity && JsonValues.Text(gateway["id"]) == identity) return true;
                }
                catch (Exception error) when (error is IOException or HttpRequestException or System.Text.Json.JsonException) { }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                await runtime.Delay(deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    public async Task Recover(CancellationToken cancellationToken = default)
    {
        if (DurableFiles.ReadJson(Transaction) is not JsonObject transaction) return;
        var previous = JsonValues.RequiredText(transaction["previous"]);
        Progress("Recovering", "Restoring the previous application version");
        await runtime.Run(["systemctl", "stop", .. Services], 120, cancellationToken);
        Link(previous);
        await runtime.Run(["systemctl", "start", "xur-gateway", "xur-agent", "xur-control"], 120, cancellationToken);
        if (!await Healthy(previous, cancellationToken: cancellationToken)) throw new UserError("Previous application did not become healthy; transaction retained for recovery");
        File.Delete(Transaction);
        File.Delete(Maintenance);
        files.SyncDirectory(paths.Root);
        Progress("RolledBack", "Previous application restored");
    }

    public static bool UsesUsers(JsonNode? value) => value switch
    {
        JsonArray array => array.Any(UsesUsers),
        JsonObject obj => obj["User"] is not null && obj.ContainsKey("Recipe") || obj.Any(pair => UsesUsers(pair.Value)),
        _ => false
    };

    public async Task Compatible(JsonObject entry, CancellationToken cancellationToken = default)
    {
        var identity = JsonValues.RequiredText(entry["id"]);
        if (!IsHash(identity)) throw new UserError("Invalid release identity");
        var features = (DurableFiles.ReadJson(AtRoot("releases/" + identity + "/host/application-features.json")) as JsonArray ?? [])
            .Select(JsonValues.Text).ToHashSet(StringComparer.Ordinal);
        var stateDirectory = Path.GetDirectoryName(paths.Root)!;
        if (features.Contains("btrfs-root-v1") && !File.ReadAllText(paths.CommandLine).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("xur.installer=1"))
        {
            string filesystem;
            try { filesystem = Encoding.UTF8.GetString(await runtime.Run(["findmnt", "--noheadings", "--output", "FSTYPE", "--target", "/sysroot"], 10, cancellationToken)).Trim(); }
            catch (IOException) { filesystem = "unknown"; }
            if (filesystem != "btrfs") throw new UserError("This Xur version requires a Btrfs root filesystem. Ext4 installations are legacy and unsupported; back up your data and reinstall with current Xur media.");
        }
        if (File.Exists(Path.Combine(stateDirectory, "manager-account.json")) && !features.Contains("manager-account-v1"))
            throw new UserError("This version cannot manage your username and password. Select a version with manager account support.");
        var catalog = Path.Combine(stateDirectory, "catalog-selected");
        if (!features.Contains("container-workloads-v1") && Directory.Exists(catalog) && Directory.EnumerateFiles(catalog, "*.json")
            .Any(path => JsonValues.Text(DurableFiles.ReadObject(path)["kind"]) == "Container"))
            throw new UserError("This version cannot manage your containers. Select a version with container workload support.");
        if (!features.Contains("multiseat-v1") && File.Exists(Path.Combine(stateDirectory, "station-seats.json")))
            throw new UserError("This version cannot manage workstation seats and USB assignments. Select a version with multiseat support.");
        if (features.Contains("station-users-v1")) return;
        var database = Path.Combine(stateDirectory, "profiles.db");
        var required = false;
        if (File.Exists(database))
            foreach (var (kind, document) in ProfileDatabase.Documents(database))
            {
                var value = JsonNode.Parse(document);
                if (kind == "journal" && JsonValues.Text(value?["Stage"]) is "Complete" or "Cancelled") continue;
                required |= UsesUsers(value);
            }
        var workloads = Path.Combine(stateDirectory, "workloads");
        if (Directory.Exists(workloads))
            foreach (var receipt in Directory.EnumerateFiles(workloads, "*.json"))
                try { required |= UsesUsers(DurableFiles.ReadJson(receipt)); }
                catch (FileNotFoundException) { }
        if (required) throw new UserError("This version cannot manage the workstation users used by your profiles. Select a version with workstation user support.");
    }

    public void RememberSequence(JsonObject entry, string? scope)
    {
        if (scope is null) return; // Explicit rollback must not attribute an older release to a new source.
        var sequences = DurableFiles.ReadObject(AtRoot("channel-sequences.json"));
        var sequence = JsonValues.Integer(entry["sequence"]) ?? 0;
        sequences[scope] = Math.Max(JsonValues.Integer(sequences[scope]) ?? 0, sequence);
        files.WriteJson(AtRoot("channel-sequences.json"), sequences);
        if (!scope.StartsWith("local:", StringComparison.Ordinal)) files.WriteJson(AtRoot("highest-sequence.json"),
            JsonValue.Create(Math.Max(JsonValues.Integer(DurableFiles.ReadJson(AtRoot("highest-sequence.json"))) ?? 0, sequence)));
    }

    public async Task Activate(JsonObject entry, string? scope = null, CancellationToken cancellationToken = default)
    {
        var previous = Current();
        var identity = JsonValues.RequiredText(entry["id"]);
        if (JsonValues.Text(previous["id"]) == identity)
        {
            RememberSequence(entry, scope);
            files.WriteJson(AtRoot("release-" + identity + ".json"), entry);
            Progress("Complete", "This version is already installed");
            return;
        }
        await Compatible(entry, cancellationToken);
        Progress("Draining", "Waiting for active requests and profile changes to finish");
        files.WriteJson(Maintenance, new JsonObject { ["operation"] = Operation });
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            while (true)
            {
                var control = await runtime.Local("/local/application-health", Path.Combine(paths.RuntimeDirectory, "control.sock"), deadline.Token);
                var agent = await runtime.Local("/application-health", Path.Combine(paths.RuntimeDirectory, "agent.sock"), deadline.Token);
                if (JsonValues.Integer(control["active"]) == 0 && !JsonValues.RequiredBoolean(control["profileBusy"]) && !JsonValues.Boolean(agent["busy"])) break;
                await runtime.Delay(deadline.Token);
            }
            await Compatible(entry, cancellationToken);
            files.WriteJson(Transaction, new JsonObject { ["previous"] = JsonValues.RequiredText(previous["id"]), ["target"] = identity, ["operation"] = Operation });
            Progress("Activating", "Starting the new application version");
            await runtime.Run(["systemctl", "stop", .. Services], 120, cancellationToken);
            Link(identity);
            await runtime.Run(["systemctl", "start", "xur-gateway", "xur-agent", "xur-control"], 120, cancellationToken);
            if (!await Healthy(identity, cancellationToken: cancellationToken)) throw new UserError("New application health checks failed");
            files.WriteJson(AtRoot("release-" + identity + ".json"), entry);
            files.WriteJson(AtRoot("previous.json"), previous);
            RememberSequence(entry, scope);
            File.Delete(Transaction);
            File.Delete(Maintenance);
            files.SyncDirectory(paths.Root);
            Progress("Complete", "Application updated");
        }
        catch
        {
            if (File.Exists(Transaction)) await Recover();
            else { File.Delete(Maintenance); files.SyncDirectory(paths.Root); }
            throw;
        }
    }

    public async Task<JsonObject?> Execute(string action, string[] arguments, CancellationToken cancellationToken = default)
    {
        if (action == "compatibility")
        {
            var identity = arguments[0];
            if (!IsHash(identity) || !File.Exists(AtRoot("releases/" + identity + "/bundle.json"))) throw new UserError("Release not installed");
            await Compatible(new JsonObject { ["id"] = identity }, cancellationToken);
            return null;
        }
        Directory.CreateDirectory(paths.Root);
        if (action == "status") return await Status(cancellationToken);
        using var locked = Linux.Lock(AtRoot("update.lock"));
        try
        {
            switch (action)
            {
                case "configure": Configure(arguments[0]); break;
                case "channel": await SelectChannel(arguments[0], arguments.ElementAtOrDefault(1), arguments.ElementAtOrDefault(2), cancellationToken); break;
                case "development": Development(bool.Parse(arguments[0])); break;
                case "recover": await Recover(cancellationToken); File.Delete(Maintenance); files.SyncDirectory(paths.Root); break;
                case "rollback": await Activate(DurableFiles.ReadJson(AtRoot("previous.json")) as JsonObject ?? throw new UserError("No previous application version"), cancellationToken: cancellationToken); break;
                case "check":
                case "update":
                    if (File.Exists(Transaction)) throw new UserError("Recover the previous interrupted transaction first");
                    Progress("Checking", "Checking the update repository");
                    using (var temporary = new TemporaryDirectory(paths.Root, "download-"))
                    {
                        var stage = temporary.Path;
                        var entry = await Check(stage, cancellationToken);
                        if (action == "check") Progress("Complete", "Update check finished");
                        else if (JsonValues.Text(Current()["id"]) == JsonValues.Text(entry["id"]))
                            await Activate(entry, SequenceScope, cancellationToken);
                        else
                        {
                            Progress("Downloading", "Downloading and verifying application files");
                            var bundle = await DownloadRelease(entry, stage, cancellationToken);
                            await StageRelease(bundle, entry, cancellationToken: cancellationToken);
                            await Activate(entry, SequenceScope, cancellationToken);
                        }
                    }
                    break;
                default: throw new UserError("Unknown update action");
            }
            return null;
        }
        catch (Exception error)
        {
            if (JsonValues.Text(DurableFiles.ReadObject(State)["stage"]) != "RolledBack")
                Progress("Failed", error is UserError ? error.Message : "Update failed; inspect the system journal");
            throw;
        }
    }
}
