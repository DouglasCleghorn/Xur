using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Xur.Util;

namespace Xur.Util.Tests;

static class UpdaterTests
{
    public static async Task Run()
    {
        await Archives();
        await Trust();
        await Compatibility();
        await Transactions();
        await Boundaries();
        await HttpDownloads();
    }

    static async Task Archives()
    {
        using var fixture = new Fixture();
        var runtime = new Runtime();
        var manifest = new JsonObject { ["z"] = "1", ["<é>\n"] = "2", ["𝒳"] = "3", ["\uffff"] = "4" };
        var python = await runtime.Run(["python3", "-c", "import json,sys;sys.stdout.write(json.dumps(json.load(sys.stdin),sort_keys=True))"], input: Encoding.UTF8.GetBytes(manifest.ToJsonString()));
        Verify.That(BundleArchive.CanonicalManifest(manifest) == Encoding.UTF8.GetString(python), "Manifest hashes preserve Python escaping, spacing and Unicode ordering");
        var archive = fixture.PathOf("hostile.tgz");
        foreach (var (name, type) in new[]
        {
            ("../escape", TarEntryType.RegularFile), ("/escape", TarEntryType.RegularFile),
            ("symlink", TarEntryType.SymbolicLink), ("hardlink", TarEntryType.HardLink),
            ("device", TarEntryType.CharacterDevice), ("pipe", TarEntryType.Fifo)
        })
        {
            WriteTar(archive, [(name, type, Array.Empty<byte>(), "../../../etc")]);
            await Verify.Reject(() => { BundleArchive.Unpack(archive, fixture.PathOf("extracted"), new JsonObject { ["id"] = new string('a', 64) }); return Task.CompletedTask; }, "Unsafe archive entry rejected", "Unsafe archive");
            Verify.That(!Directory.Exists(fixture.PathOf("extracted")), "Hostile archive is rejected before any files publish");
        }
        WriteTar(archive, [("file", TarEntryType.RegularFile, new byte[] { 1 }, ""), ("./file", TarEntryType.RegularFile, new byte[] { 2 }, "")]);
        await Verify.Reject(() => { BundleArchive.Unpack(archive, fixture.PathOf("extracted"), new JsonObject()); return Task.CompletedTask; }, "Normalized duplicate names are rejected", "Unsafe archive");
        var content = new Dictionary<string, byte[]>
        {
            ["control/Xur.Control"] = Encoding.UTF8.GetBytes("control"), ["agent/Xur.Agent"] = Encoding.UTF8.GetBytes("agent"),
            ["gateway/Xur.Gateway"] = Encoding.UTF8.GetBytes("gateway"), ["host/app-update"] = Encoding.UTF8.GetBytes("#!/bin/sh\n"),
            ["host/xurutil"] = Encoding.UTF8.GetBytes("native"), ["assets/é.txt"] = Encoding.UTF8.GetBytes("unicode")
        };
        var hashes = new JsonObject();
        foreach (var (name, bytes) in content) hashes[name] = DurableFiles.Hash(bytes);
        var identity = DurableFiles.Hash(Encoding.UTF8.GetBytes(BundleArchive.CanonicalManifest(hashes)));
        content["bundle.json"] = Encoding.UTF8.GetBytes(new JsonObject { ["id"] = identity, ["hostAbi"] = 1, ["files"] = hashes }.ToJsonString());
        WriteTar(archive, content.Select(pair => (pair.Key, TarEntryType.RegularFile, pair.Value, "")).ToArray());
        var target = fixture.PathOf("valid");
        var entry = new JsonObject { ["id"] = identity };
        BundleArchive.Unpack(archive, target, entry);
        Verify.That(File.ReadAllText(Path.Combine(target, "assets/é.txt")) == "unicode", "Valid archives retain Unicode paths");
        Verify.That(Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).All(path => (File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0), "Bundle files are never writable by other users");
        Verify.That(Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).All(path => (File.GetUnixFileMode(path) & (UnixFileMode.SetUser | UnixFileMode.SetGroup | UnixFileMode.StickyBit)) == 0), "Archive extraction strips setuid, setgid and sticky bits");
        File.WriteAllText(Path.Combine(target, "host/xurutil"), "tampered");
        await Verify.Reject(() => { BundleArchive.Validate(target, entry); return Task.CompletedTask; }, "Existing release tampering rejected", "hash mismatch");
    }

    static void WriteTar(string path, (string Name, TarEntryType Type, byte[] Bytes, string Link)[] entries)
    {
        using var output = File.Create(path);
        using var gzip = new GZipStream(output, CompressionLevel.Fastest);
        using var writer = new TarWriter(gzip);
        foreach (var (name, type, bytes, link) in entries)
        {
            var entry = new PaxTarEntry(type, name) { Mode = (UnixFileMode)0xfff };
            if (type == TarEntryType.RegularFile) entry.DataStream = new MemoryStream(bytes);
            if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink) entry.LinkName = link;
            writer.WriteEntry(entry);
            entry.DataStream?.Dispose();
        }
    }

    static async Task Trust()
    {
        using var fixture = new Fixture();
        var runtime = new Runtime();
        var files = new DurableFiles();
        var official = fixture.PathOf("official.private.key");
        var contributor = fixture.PathOf("contributor.private.key");
        foreach (var key in new[] { official, contributor }) await runtime.Run(["openssl", "genpkey", "-algorithm", "ED25519", "-out", key]);
        var officialPublic = Encoding.UTF8.GetString(await runtime.Run(["openssl", "pkey", "-in", official, "-pubout"]));
        var contributorPublic = Encoding.UTF8.GetString(await runtime.Run(["openssl", "pkey", "-in", contributor, "-pubout"]));
        fixture.Write("official.pem", officialPublic);
        var identity = new string('a', 64);
        var entry = new JsonObject
        {
            ["schema"] = 2, ["hostAbi"] = 1, ["dataSchema"] = 1, ["id"] = identity, ["file"] = SignedUpdater.Archive,
            ["channel"] = "stable", ["version"] = "1.2 <é>𝒳",
            ["installer"] = new JsonObject { ["iso"] = new JsonObject { ["bytes"] = 3, ["sha256"] = new string('c', 64) } }, ["sequence"] = 10, ["bytes"] = 20, ["sha256"] = new string('b', 64)
        };
        var descriptor = fixture.PathOf("signed.json");
        var signature = fixture.PathOf("signed.sig");
        var legacy = false;
        async Task Sign(string key)
        {
            var raw = await runtime.Run(["python3", "-c", "import json,sys;sys.stdout.write(json.dumps(json.load(sys.stdin),sort_keys=True,separators=(',',':')))"], input: Encoding.UTF8.GetBytes(entry.ToJsonString()));
            File.WriteAllBytes(descriptor, raw);
            await runtime.Run(["openssl", "pkeyutl", "-sign", "-inkey", key, "-rawin", "-in", descriptor, "-out", signature]);
            if (!legacy) File.WriteAllText(descriptor, new JsonObject { ["schema"] = 2, ["release"] = entry.DeepClone(), ["signature"] = Convert.ToBase64String(File.ReadAllBytes(signature)) }.ToJsonString());
        }
        var downloads = new FixtureDownloads
        {
            Supply = (url, path, _) =>
            {
                if (url.EndsWith("/current", StringComparison.Ordinal))
                {
                    if (legacy) throw new HttpRequestException("Not found", null, System.Net.HttpStatusCode.NotFound);
                    File.WriteAllText(path, url == Downloads.Stable + "/current" ? "v1.2" : url == Downloads.Nightly + "/current" ? "nightly-1.3" : identity);
                    return null;
                }
                if (url.EndsWith("/latest", StringComparison.Ordinal)) { File.WriteAllText(path, identity); return null; }
                if (url.EndsWith(".sig", StringComparison.Ordinal)) File.Copy(signature, path, true);
                else File.Copy(descriptor, path, true);
                return null;
            }
        };
        var root = fixture.PathOf("app");
        Directory.CreateDirectory(root);
        fixture.Write("bundled/bundle.json", new JsonObject { ["id"] = new string('c', 64), ["version"] = new string('c', 12) }.ToJsonString());
        Linux.PublishLink(Path.Combine(root, "current"), "../bundled");
        var installedRelease = Path.Combine(root, "release-" + identity + ".json");
        var updater = new SignedUpdater(new UpdatePaths(root, fixture.PathOf("config.json"), fixture.PathOf("official.pem")), runtime, files, downloads);
        using var stage = new TemporaryDirectory(root, "metadata-");
        Verify.That(updater.Source == Downloads.Stable, "Stable remains the default source");
        await Verify.Reject(() => { updater.Configure("127.0.0.1:8088"); return Task.CompletedTask; }, "Local sources require explicit opt-in");
        updater.Development(true);
        updater.Configure("127.0.0.1:8088");
        Verify.That(updater.Source == "http://127.0.0.1:8088", "Legacy development opt-in remains compatible");
        updater.Development(false);
        await Sign(official);
        Verify.That(JsonNode.DeepEquals(await updater.Check(stage.Path), entry), "Real Ed25519 signatures verify");
        Verify.That(File.ReadAllText(Path.Combine(stage.Path, "source")) == Downloads.GitHub + "/download/v1.2", "Stable metadata is pinned to an immutable release");
        Verify.That(downloads.Requests.Skip(1).All(url => url.StartsWith(Downloads.GitHub + "/download/v1.2/", StringComparison.Ordinal)), "Compact descriptor stays on the resolved release");
        Verify.That(!File.Exists(installedRelease) && JsonValues.Text(updater.Current()["version"]) == new string('c', 12), "An available release with a different bundle ID cannot rename the installed application");
        fixture.Write("bundled/bundle.json", new JsonObject { ["id"] = identity, ["version"] = identity[..12] }.ToJsonString());
        var bundledMetadata = File.ReadAllBytes(fixture.PathOf("bundled/bundle.json"));
        Verify.That(JsonValues.Text(updater.Current()["version"]) == identity[..12], "Bundled installs initially display their build hash");
        downloads.Requests.Clear();
        await updater.Execute("check", []);
        Verify.That(JsonValues.Text(updater.Current()["version"]) == JsonValues.Text(entry["version"]) && JsonNode.DeepEquals(DurableFiles.ReadJson(installedRelease), entry), "A signed check repairs the installed version when the bundle already matches");
        Verify.That(downloads.Requests.SequenceEqual([Downloads.Stable + "/current", Downloads.GitHub + "/download/v1.2/" + SignedUpdater.Descriptor]), "Repairing an installed version fetches only the pointer and signed metadata");
        Verify.That(Directory.ResolveLinkTarget(Path.Combine(root, "current"), true)!.FullName == fixture.PathOf("bundled") && File.ReadAllBytes(fixture.PathOf("bundled/bundle.json")).SequenceEqual(bundledMetadata), "Version repair preserves the selected bundle and its original manifest");
        Verify.That(new[] { "previous.json", "transaction.json", "maintenance", "channel-sequences.json" }.All(name => !File.Exists(Path.Combine(root, name))), "Version checks do not activate an update or advance replay floors");

        var installerPaths = new InstallerPaths(root, fixture.PathOf("bundled"), fixture.PathOf("ready"), fixture.PathOf("channel"),
            fixture.PathOf("cmdline"), fixture.PathOf("approved.ks"), fixture.PathOf("config.json"), fixture.PathOf("official.pem"), fixture.PathOf("run"));
        fixture.Write("cmdline", "xur.installer=1");
        var bootstrap = new InstallerBootstrap(installerPaths, runtime, files, downloads, () => updater);
        File.Delete(installedRelease);
        await bootstrap.Check();
        Verify.That(JsonValues.Text(DurableFiles.ReadObject(Path.Combine(root, "check.json"))["state"]) == "current" && JsonNode.DeepEquals(DurableFiles.ReadJson(installedRelease), entry), "Installer background checks retain matching signed metadata for install-manager to copy");
        File.Delete(installedRelease);
        await bootstrap.Refresh();
        Verify.That(JsonNode.DeepEquals(DurableFiles.ReadJson(installedRelease), entry) && downloads.Requests.All(url => !url.EndsWith(".tar.gz", StringComparison.Ordinal)), "Explicit installer refresh retains the release version even when its bundled app is already current");

        var installedMetadata = File.ReadAllBytes(installedRelease);
        var tampered = DurableFiles.ReadObject(descriptor);
        tampered["release"]!["sequence"] = 100;
        tampered["release"]!["version"] = "Untrusted version";
        File.WriteAllText(descriptor, tampered.ToJsonString());
        await Verify.Reject(async () => await updater.Check(stage.Path), "Tampered signed metadata is rejected");
        Verify.That(File.ReadAllBytes(installedRelease).SequenceEqual(installedMetadata), "An invalid signature cannot replace the installed version metadata");
        await Sign(official);
        files.WriteJson(Path.Combine(root, "highest-sequence.json"), JsonValue.Create(11));
        files.WriteJson(Path.Combine(root, "channel-sequences.json"), new JsonObject { ["stable"] = 11 });
        await Verify.Reject(async () => await updater.Check(stage.Path), "Stable replay floor is retained", "older release");
        Verify.That(File.ReadAllBytes(installedRelease).SequenceEqual(installedMetadata), "A rejected replay cannot replace the installed version metadata");
        await Verify.Reject(async () => await updater.CheckLocalLegacy(stage.Path), "Public channels cannot fall back to legacy discovery", "only for local");
        await updater.SelectChannel("nightly");
        entry["channel"] = "nightly";
        entry["sequence"] = 20;
        await Sign(official);
        Verify.That(JsonNode.DeepEquals(await updater.Check(stage.Path), entry), "Nightly pointer resolves signed nightly metadata");
        Verify.That(File.ReadAllText(Path.Combine(stage.Path, "source")) == Downloads.GitHub + "/download/nightly-1.3", "Nightly pointer becomes immutable source");
        files.WriteJson(Path.Combine(root, "channel-sequences.json"), new JsonObject { ["nightly"] = 20, ["stable"] = 5 });
        await updater.SelectChannel("stable");
        entry["channel"] = "stable";
        entry["sequence"] = 6;
        await Sign(official);
        Verify.That(JsonNode.DeepEquals(await updater.Check(stage.Path), entry), "Stable can be older than nightly without replaying stable");
        entry["sequence"] = 4;
        await Sign(official);
        await Verify.Reject(async () => await updater.Check(stage.Path), "Same-channel replay rejected", "older release");
        entry["channel"] = "nightly";
        entry["sequence"] = 21;
        await Sign(official);
        await Verify.Reject(async () => await updater.Check(stage.Path), "Wrong signed channel rejected", "selected channel");

        await updater.SelectChannel("local", "192.0.2.10:8088", contributorPublic);
        Verify.That(updater.TrustKey == contributorPublic && File.ReadAllText(fixture.PathOf("official.pem")) == officialPublic, "Contributor trust is confined to local sources");
        var config = File.ReadAllBytes(fixture.PathOf("config.json"));
        foreach (var bad in new[] { File.ReadAllText(contributor), "", contributorPublic + "junk", contributorPublic + contributorPublic, "-----BEGIN PUBLIC KEY-----\ninvalid\n-----END PUBLIC KEY-----" })
        {
            await Verify.Reject(async () => await updater.SelectChannel("local", "192.0.2.10:8088", bad), "Invalid keys rejected");
            Verify.That(File.ReadAllBytes(fixture.PathOf("config.json")).SequenceEqual(config), "Invalid settings cannot change active trust");
        }
        var rsa = fixture.PathOf("rsa.private.key");
        await runtime.Run(["openssl", "genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:2048", "-out", rsa]);
        await Verify.Reject(async () => await updater.PublicKey(Encoding.UTF8.GetString(await runtime.Run(["openssl", "pkey", "-in", rsa, "-pubout"]))), "RSA keys rejected", "Ed25519");
        entry["channel"] = "development";
        entry["sequence"] = 1;
        await Sign(contributor);
        Verify.That(JsonNode.DeepEquals(await updater.Check(stage.Path), entry), "Explicit local key verifies contributor metadata");
        var scope = updater.SequenceScope;
        var globalFloor = File.ReadAllText(Path.Combine(root, "highest-sequence.json"));
        updater.RememberSequence(entry, scope);
        Verify.That(File.ReadAllText(Path.Combine(root, "highest-sequence.json")) == globalFloor, "Local replay protection does not alter official floor");
        entry["sequence"] = 0;
        await Sign(contributor);
        await Verify.Reject(async () => await updater.Check(stage.Path), "Local replay rejected", "older release");
        await updater.SelectChannel("local", "192.0.2.11:8088", contributorPublic);
        Verify.That(updater.SequenceScope != scope && JsonNode.DeepEquals(await updater.Check(stage.Path), entry), "Local sources have independent replay floors");
        await updater.SelectChannel("stable");
        entry["channel"] = "stable";
        entry["sequence"] = 6;
        await Sign(contributor);
        await Verify.Reject(async () => await updater.Check(stage.Path), "Contributor key cannot verify official releases");
        await Sign(official);
        Verify.That(JsonNode.DeepEquals(await updater.Check(stage.Path), entry), "Returning to stable restores official trust");
        entry["bytes"] = "20";
        await Sign(official);
        await Verify.Reject(async () => await updater.Check(stage.Path), "Signed malformed metadata is still rejected", "metadata");
        entry["bytes"] = 20;
        entry["sequence"] = -1;
        await Sign(official);
        await Verify.Reject(async () => await updater.Check(stage.Path), "Negative signed sequences rejected");
        entry["sequence"] = 20;
        entry["channel"] = null;
        await Sign(official);
        await Verify.Reject(async () => await updater.Check(stage.Path), "Null signed channels cannot bypass replay floors", "selected channel");

        var supply = downloads.Supply;
        foreach (var channel in new[] { "stable", "nightly" })
        {
            await updater.SelectChannel(channel);
            foreach (var status in new[] { System.Net.HttpStatusCode.NotFound, System.Net.HttpStatusCode.Forbidden, System.Net.HttpStatusCode.InternalServerError })
            {
                files.WriteJson(Path.Combine(root, "available.json"), new JsonObject { ["id"] = "stale" });
                downloads.Requests.Clear();
                downloads.Supply = (_, _, _) => throw new HttpRequestException("Fixture failure", null, status);
                try { await updater.Check(stage.Path); throw new Exception("Missing public pointer accepted"); }
                catch (UserError error)
                {
                    Verify.That(status == System.Net.HttpStatusCode.NotFound && error.Message.StartsWith("No " + (channel == "stable" ? "Stable" : "Nightly") + " release available.") && !File.Exists(Path.Combine(root, "available.json")), "Missing public pointers explain availability and clear stale metadata");
                }
                catch (HttpRequestException error) { Verify.That(status != System.Net.HttpStatusCode.NotFound && error.StatusCode == status, "Authorization and repository failures propagate"); }
                Verify.That(downloads.Requests.SequenceEqual([updater.Source + "/current"]), "Public failures never try legacy discovery");
            }
        }
        await updater.SelectChannel("stable");
        downloads.Supply = (url, path, limit) => url.EndsWith("/current") ? supply(url, path, limit) : throw new HttpRequestException("Missing descriptor", null, System.Net.HttpStatusCode.NotFound);
        try { await updater.Check(stage.Path); throw new Exception("Broken release accepted"); }
        catch (HttpRequestException error) { Verify.That(error.StatusCode == System.Net.HttpStatusCode.NotFound, "Missing descriptor remains a broken release, not an unpublished channel"); }
        downloads.Supply = supply;
        foreach (var pointer in new[] { "v1", "../v1.2", "nightly-1.2", "v1.2\nInjected" })
        {
            downloads.Supply = (url, path, limit) => { File.WriteAllText(path, pointer); return null; };
            await Verify.Reject(async () => await updater.Check(stage.Path), "Malformed official pointers reject before metadata download", "pointer");
        }
        downloads.Supply = supply;
        entry["channel"] = "development";
        entry["sequence"] = 30;
        await updater.SelectChannel("local", "192.0.2.12:8088", contributorPublic);
        await Sign(contributor);
        Verify.That(JsonNode.DeepEquals(await updater.Check(stage.Path), entry), "Local compact descriptors verify contributor trust");
        foreach (var badSignature in new[] { "!", Convert.ToBase64String(new byte[63]), Convert.ToBase64String(new byte[64]) + "\n" })
        {
            var envelope = DurableFiles.ReadObject(descriptor);
            envelope["signature"] = badSignature;
            File.WriteAllText(descriptor, envelope.ToJsonString());
            await Verify.Reject(async () => await updater.Check(stage.Path), "Malformed embedded signatures fail without legacy fallback", "signature");
        }
        legacy = true;
        entry["schema"] = 1;
        entry["file"] = identity + ".tar.gz";
        entry["version"] = "1.3";
        await Sign(contributor);
        File.Delete(installedRelease);
        downloads.Requests.Clear();
        Verify.That(JsonNode.DeepEquals(await updater.Check(stage.Path), entry), "Local 404 falls back to real detached schema-one signatures");
        Verify.That(JsonValues.Text(updater.Current()["version"]) == "1.3" && JsonNode.DeepEquals(DurableFiles.ReadJson(installedRelease), entry), "Verified local legacy checks also repair the installed release version");
        Verify.That(downloads.Requests.SequenceEqual([updater.Source + "/current", updater.Source + "/latest", updater.Source + "/" + identity + ".json", updater.Source + "/" + identity + ".json.sig"]), "Legacy fallback is restricted to missing local current pointers");
        updater.RememberSequence(entry, updater.SequenceScope);
        entry["sequence"] = 29;
        await Sign(contributor);
        await Verify.Reject(async () => await updater.Check(stage.Path), "Local legacy replay floor is enforced", "older release");
    }

    public static async Task PublishedRepository(string root)
    {
        var requests = new List<string>();
        var downloads = new FixtureDownloads
        {
            Supply = (url, path, limit) =>
            {
                var relative = url[(Downloads.GitHub + "/download/").Length..];
                var bytes = File.ReadAllBytes(Path.Combine(root, "repository", relative));
                Verify.That(bytes.Length <= limit, "Published assets respect metadata and archive download bounds");
                File.WriteAllBytes(path, bytes);
                requests.Add(relative);
                return null;
            }
        };
        var state = Path.Combine(root, "native-state");
        Directory.CreateDirectory(state);
        var updater = new SignedUpdater(new UpdatePaths(state, Path.Combine(root, "native-config"), Path.Combine(root, "os/bootc/application-update-key.pem")), new Runtime(), new DurableFiles(), downloads);
        using var stage = new TemporaryDirectory(state, "stage-");
        foreach (var channel in new[] { "stable", "nightly" })
        {
            requests.Clear();
            await updater.SelectChannel(channel);
            var entry = await updater.Check(stage.Path);
            Verify.That(JsonValues.Text(entry["version"]) == "1.2" && requests.Count == 2, "Actual CI publisher metadata is accepted with two bounded requests");
            var tag = File.ReadAllText(Path.Combine(root, "repository", channel, "current")).Trim();
            File.WriteAllText(Path.Combine(root, "repository", channel, "current"), "invalid-new-pointer");
            var bundle = await updater.DownloadRelease(entry, stage.Path);
            Verify.That(requests[^1] == tag + "/" + SignedUpdater.Archive && File.Exists(Path.Combine(bundle, "fixture")), "Download stays on the signed immutable release after pointer changes");
            Directory.Delete(bundle, true);
        }
    }

    static async Task Compatibility()
    {
        using var fixture = new Fixture();
        var identity = new string('a', 64);
        var runtime = new FakeRuntime();
        var updater = new SignedUpdater(new UpdatePaths(fixture.PathOf("app"), fixture.PathOf("config"), fixture.PathOf("key"), CommandLine: fixture.PathOf("cmdline")), runtime, new DurableFiles(), new Downloads());
        fixture.Write("app/releases/" + identity + "/host/application-features.json", "[]");
        fixture.Write("cmdline", "quiet");
        var entry = new JsonObject { ["id"] = identity };
        var native = new Runtime();
        async Task Database(string document, string kind = "profile") => await native.Run(["python3", "-c",
            "import sqlite3,sys; db=sqlite3.connect(sys.argv[1]);db.execute('CREATE TABLE IF NOT EXISTS documents(kind TEXT,json TEXT)');db.execute('DELETE FROM documents');db.execute('INSERT INTO documents VALUES (?,?)',(sys.argv[2],sys.argv[3]));db.commit()",
            fixture.PathOf("profiles.db"), kind, document]);
        await Database("{\"Workloads\":[{\"Recipe\":{},\"User\":null}]}");
        await updater.Compatible(entry);
        fixture.Write("app/releases/" + identity + "/bundle.json", "{}");
        await updater.Execute("compatibility", [identity]);
        Verify.That(!File.Exists(fixture.PathOf("app/update.lock")) && !File.Exists(fixture.PathOf("app/update.json")), "Compatibility CLI has no lock, progress or activation side effects");
        foreach (var user in new[] { "{\"Username\":\"alex\",\"Uid\":1001,\"Temporary\":false}", "{\"Username\":\"\",\"Uid\":0,\"Temporary\":true}" })
        {
            await Database("{\"Workloads\":[{\"Recipe\":{},\"User\":" + user + "}]}");
            await Verify.Reject(async () => await updater.Compatible(entry), "User-dependent profiles reject older versions", "workstation users");
        }
        await Database("{\"Stage\":\"Failed\",\"Plan\":{\"Recipe\":{},\"User\":{}}}", "journal");
        await Verify.Reject(async () => await updater.Compatible(entry), "Incomplete journals retain user compatibility guard");
        await Database("{\"Stage\":\"Complete\",\"Plan\":{\"Recipe\":{},\"User\":{}}}", "journal");
        await updater.Compatible(entry);
        Verify.That(File.Exists(fixture.PathOf("profiles.db")), "Read-only SQLite compatibility never migrates the database");
        fixture.Write("workloads/station.json", "{\"Recipe\":{},\"User\":{}}");
        await Verify.Reject(async () => await updater.Activate(entry), "Incompatible activation rejects before mutation", "workstation users");
        Verify.That(!File.Exists(fixture.PathOf("app/maintenance")) && runtime.Commands.Count == 0, "Incompatible activation cannot stop services or create maintenance state");
        fixture.Write("app/releases/" + identity + "/host/application-features.json", "[\"station-users-v1\"]");
        await updater.Compatible(entry);
        fixture.Write("manager-account.json", "{}");
        await Verify.Reject(async () => await updater.Compatible(entry), "Manager account rollback guarded", "username and password");
        fixture.Write("app/releases/" + identity + "/host/application-features.json", "[\"station-users-v1\",\"manager-account-v1\"]");
        fixture.Write("catalog-selected/custom.json", "{\"kind\":\"Container\"}");
        await Verify.Reject(async () => await updater.Compatible(entry), "Container rollback guarded", "containers");
        fixture.Write("app/releases/" + identity + "/host/application-features.json", "[\"station-users-v1\",\"manager-account-v1\",\"container-workloads-v1\"]");
        fixture.Write("station-seats.json", "[]");
        await Verify.Reject(async () => await updater.Compatible(entry), "Seat rollback guarded", "multiseat");
        fixture.Write("app/releases/" + identity + "/host/application-features.json", "[\"station-users-v1\",\"manager-account-v1\",\"container-workloads-v1\",\"multiseat-v1\",\"btrfs-root-v1\"]");
        runtime.OnRun = (_, _) => Task.FromResult(Encoding.UTF8.GetBytes("ext4\n"));
        await Verify.Reject(async () => await updater.Activate(entry), "Unsupported root rejects before maintenance", "Btrfs");
        runtime.OnRun = (args, seconds) =>
        {
            Verify.That(args.SequenceEqual(["findmnt", "--noheadings", "--output", "FSTYPE", "--target", "/sysroot"]) && seconds == 10, "Physical sysroot is probed with a deadline");
            return Task.FromResult(Encoding.UTF8.GetBytes("btrfs\n"));
        };
        await updater.Compatible(entry);
        runtime.OnRun = (_, _) => throw new IOException("Probe failed");
        await Verify.Reject(async () => await updater.Compatible(entry), "Failed root probe rejects", "reinstall");
        fixture.Write("cmdline", "quiet xur.installer=1");
        runtime.Commands.Clear();
        await updater.Compatible(entry);
        Verify.That(runtime.Commands.Count == 0, "Installer never probes installed root compatibility");
    }

    static async Task Transactions()
    {
        using var fixture = new Fixture();
        var previous = new string('a', 64);
        var target = new string('b', 64);
        foreach (var identity in new[] { previous, target }) fixture.Write("app/releases/" + identity + "/bundle.json", new JsonObject { ["id"] = identity }.ToJsonString());
        Linux.PublishLink(fixture.PathOf("app/current"), "releases/" + previous);
        var runtime = new FakeRuntime();
        var files = new DurableFiles();
        var updater = new HealthyUpdater(new UpdatePaths(fixture.PathOf("app"), fixture.PathOf("config"), fixture.PathOf("key")), runtime, files, new Downloads());
        var entry = new JsonObject { ["id"] = target, ["sequence"] = 20 };
        await updater.Activate(entry, "stable");
        Verify.That(JsonValues.Text(updater.Current()["id"]) == target && JsonValues.Text(DurableFiles.ReadObject(fixture.PathOf("app/update.json"))["stage"]) == "Complete", "Healthy activation completes transaction");
        Verify.That(JsonValues.Text(DurableFiles.ReadObject(fixture.PathOf("app/previous.json"))["id"]) == previous, "Previous release is recorded");
        Verify.That(!File.Exists(fixture.PathOf("app/transaction.json")) && !File.Exists(fixture.PathOf("app/maintenance")), "Completed transaction removes durable recovery state");
        Verify.That(runtime.Commands.Select(command => string.Join(' ', command)).SequenceEqual(["systemctl stop xur-control xur-agent xur-gateway", "systemctl start xur-gateway xur-agent xur-control"]), "Service stop and start ordering is preserved");
        var beforeFloor = File.ReadAllText(fixture.PathOf("app/highest-sequence.json"));
        await updater.Activate(new JsonObject { ["id"] = previous });
        Verify.That(File.ReadAllText(fixture.PathOf("app/highest-sequence.json")) == beforeFloor, "Explicit rollback never lowers replay floors");
        updater.Health = identity => identity == previous;
        await Verify.Reject(async () => await updater.Activate(entry, "stable"), "Unhealthy new app triggers recovery", "health checks");
        Verify.That(JsonValues.Text(updater.Current()["id"]) == previous && JsonValues.Text(DurableFiles.ReadObject(fixture.PathOf("app/update.json"))["stage"]) == "RolledBack", "Health failure restores previous release");
        files.WriteJson(fixture.PathOf("app/transaction.json"), new JsonObject { ["previous"] = previous, ["target"] = target });
        files.WriteJson(fixture.PathOf("app/maintenance"), new JsonObject());
        updater.Health = _ => false;
        await Verify.Reject(async () => await updater.Recover(), "Failed recovery retains transaction", "retained for recovery");
        Verify.That(File.Exists(fixture.PathOf("app/transaction.json")) && File.Exists(fixture.PathOf("app/maintenance")), "Failed recovery can retry on the next boot");
        updater.Health = _ => true;
        await updater.Recover();
        Verify.That(!File.Exists(fixture.PathOf("app/transaction.json")), "Successful recovery clears transaction");
        using var locked = Linux.Lock(fixture.PathOf("app/update.lock"));
        await Verify.Reject(async () => await updater.Execute("check", []), "Concurrent updater rejected", "already running");
        var probes = 0;
        runtime.OnLocal = (_, _) =>
        {
            if (++probes == 1) throw new OperationCanceledException("Individual HTTP timeout");
            return Task.FromResult(new JsonObject { ["id"] = previous });
        };
        var health = new SignedUpdater(updater.Paths, runtime, files, new Downloads());
        Verify.That(await health.Healthy(previous, 1) && probes >= 4, "Individual health probe timeouts retry within overall deadline");
        runtime.OnLocal = (_, _) => Task.FromResult(new JsonObject { ["active"] = 0 });
        await Verify.Reject(async () => await updater.Activate(entry), "Malformed control health cannot release maintenance", "health response");
        Verify.That(!File.Exists(fixture.PathOf("app/maintenance")), "Malformed drain health leaves the current app active");
    }

    static async Task Boundaries()
    {
        Verify.That(SignedUpdater.NormalizeServer("192.168.0.134") == "http://192.168.0.134:8088", "Bare addresses get default local port");
        Verify.That(SignedUpdater.NormalizeServer("test.local:9090") == "http://test.local:9090", "Explicit ports are retained");
        Verify.That(SignedUpdater.NormalizeServer("http://[fd00::1]:8088") == "http://[fd00::1]:8088", "IPv6 addresses remain bracketed");
        Verify.That(SignedUpdater.NormalizeServer("http://host:80") == "http://host:80", "Explicit default HTTP port is retained");
        foreach (var bad in new[] { "file:///etc/passwd", "http://user:pass@host", "http://host/path", "http://host?key=value", "http://host#fragment" })
            await Verify.Reject(() => { SignedUpdater.NormalizeServer(bad); return Task.CompletedTask; }, "Untrusted local address rejected");
        foreach (var bad in new[] { "http://github.com/DouglasCleghorn/Xur/releases/download/v1/latest", "https://evil.example/file", "https://github.com/other/repo/releases/download/v1/file", "https://user:pass@release-assets.githubusercontent.com/file", "https://release-assets.githubusercontent.com:444/file" })
            Verify.That(!Downloads.GitHubAsset(bad), "Untrusted redirect rejected");
        Verify.That(Downloads.GitHubAsset("https://github.com/DouglasCleghorn/Xur/releases/download/v1/latest") && Downloads.GitHubAsset("https://release-assets.githubusercontent.com/release-asset?signature=fixture"), "Only approved HTTPS release redirect destinations are allowed");
        using var fixture = new Fixture();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await Verify.Reject(async () => await new Runtime().Run(["sleep", "30"], 1), "Hung system command is cancelled");
        Verify.That(timer.Elapsed < TimeSpan.FromSeconds(5), "Command deadlines kill the child process");
    }

    static async Task HttpDownloads()
    {
        using var fixture = new Fixture();
        foreach (var response in new[]
        {
            "HTTP/1.1 302 Found\r\nLocation: https://github.com/DouglasCleghorn/Xur/releases/download/v1/latest\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nTOO-LARGE",
            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"
        })
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                var buffer = new byte[4096];
                var request = new StringBuilder();
                while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var count = await stream.ReadAsync(buffer);
                    if (count == 0 || request.Length > 8192) throw new IOException("Incomplete fixture HTTP request");
                    request.Append(Encoding.ASCII.GetString(buffer, 0, count));
                }
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            });
            try
            {
                var action = new Downloads().Fetch("http://127.0.0.1:" + port + "/latest", fixture.PathOf("download"), 2);
                if (response.Contains("302")) await Verify.Reject(async () => await action, "Local sources cannot redirect even to approved GitHub hosts", "Untrusted repository redirect");
                else if (response.Contains("TOO-LARGE")) await Verify.Reject(async () => await action, "Streamed download limit enforced without Content-Length", "allowed size");
                else
                {
                    Verify.That(await action is null && File.ReadAllText(fixture.PathOf("download")) == "OK", "Real HTTP download writes exact bounded response");
                }
                await server;
            }
            finally { listener.Stop(); }
        }
    }
}
