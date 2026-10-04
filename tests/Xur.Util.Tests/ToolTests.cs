using System.Text;
using System.Text.Json.Nodes;
using Xur.Util;

namespace Xur.Util.Tests;

static class ToolTests
{
    public static async Task Preflight()
    {
        var now = new DateTimeOffset(2026, 9, 28, 1, 2, 3, TimeSpan.Zero);
        foreach (var failure in new[] { "", "service", "online", "makestep", "burst", "waitsync", "write", "read", "stale", "malformed", "timeout", "missing" })
        {
            var runtime = new FakeRuntime();
            runtime.OnRun = (command, seconds) =>
            {
                Verify.That(command[..3].SequenceEqual(new[] { "/usr/bin/env", "LC_ALL=C", "TZ=UTC" }) && seconds is > 0 and <= 35, "Clock commands use UTC, invariant output and bounded waits");
                var args = command[3..];
                var stage = args[0] == "systemctl" ? "service" : args[0] == "chronyc" ? args[1] : args[1] == "--systohc" ? "write" : "read";
                if (failure == stage || failure == "missing" && stage == "service") throw new IOException("command failed");
                if (failure == "timeout" && stage == "waitsync") throw new OperationCanceledException();
                var output = stage != "read" ? "" : failure == "malformed" ? "bad time" : failure == "stale" ? "2024-01-01 00:00:00+00:00" : "2026-09-28 01:02:03.000000+00:00\n";
                return Task.FromResult(Encoding.UTF8.GetBytes(output));
            };
            var preflight = new InstallerPreflight(runtime);
            if (failure.Length == 0)
            {
                await preflight.SyncClock(true, () => now);
                Verify.That(runtime.Commands[^2][4] == "--systohc" && runtime.Commands[^1][4] == "--show", "Synchronized UTC is written to and read back from the RTC");
                Verify.That(runtime.Commands.Any(c => c[3..].SequenceEqual(new[] { "chronyc", "waitsync", "15", "1", "0", "2" })), "NTP synchronization is verified before the RTC is changed");
            }
            else await Verify.Reject(() => preflight.SyncClock(true, () => now), "Clock failure stops the preflight: " + failure);
            if (failure is "service" or "online" or "makestep" or "burst" or "waitsync" or "timeout" or "missing")
                Verify.That(!runtime.Commands.Any(c => c.Contains("hwclock")), "Failed NTP leaves the RTC untouched");
        }
        var noRtc = new FakeRuntime();
        await new InstallerPreflight(noRtc).SyncClock(false);
        Verify.That(noRtc.Commands.Count == 5 && !noRtc.Commands.Any(c => c.Contains("hwclock")), "No-RTC machines synchronize system time without hardware-clock commands");
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.PathOf("usr/bin"));
        Directory.CreateSymbolicLink(fixture.PathOf("usr/sbin"), "bin");
        foreach (var program in InstallerPreflight.Programs)
        { fixture.Write("usr/bin/" + program, "#!/bin/sh\n"); File.SetUnixFileMode(fixture.PathOf("usr/bin/" + program), (UnixFileMode)493); }
        InstallerPreflight.CheckRuntime(fixture.Root);
        foreach (var program in InstallerPreflight.Programs)
        {
            File.SetUnixFileMode(fixture.PathOf("usr/bin/" + program), (UnixFileMode)420);
            await Verify.Reject(() => { InstallerPreflight.CheckRuntime(fixture.Root); return Task.CompletedTask; }, "Runtime rejects non-executable " + program, program);
            File.SetUnixFileMode(fixture.PathOf("usr/bin/" + program), (UnixFileMode)493);
        }
        Directory.Delete(fixture.PathOf("usr/sbin")); Directory.CreateDirectory(fixture.PathOf("usr/sbin"));
        await Verify.Reject(() => { InstallerPreflight.CheckRuntime(fixture.Root); return Task.CompletedTask; }, "Runtime rejects an overlay replacing Fedora's merged sbin symlink", "symlink");
    }

    public static async Task Display()
    {
        static JsonObject Output() => new()
        {
            ["name"] = VirtualDisplay.Output, ["connected"] = true, ["enabled"] = true, ["currentModeId"] = "0",
            ["modes"] = new JsonArray((JsonNode)new JsonObject { ["id"] = "0", ["size"] = new JsonObject { ["width"] = 1920, ["height"] = 1080 }, ["refreshRate"] = 60 })
        };
        static JsonObject Mode(string id, int width, int height, double rate) => new()
            { ["id"] = id, ["size"] = new JsonObject { ["width"] = width, ["height"] = height }, ["refreshRate"] = rate };
        var runtime = new FakeRuntime(); var display = new VirtualDisplay(runtime);
        foreach (var request in new[] { (0, 1080, 60), (1921, 1080, 60), (7682, 1080, 60), (1920, 1080, 0) })
            await Verify.Reject(() => display.Resize(request.Item1, request.Item2, request.Item3), "Invalid display request is rejected");
        Verify.That(runtime.Commands.Count == 0, "Invalid display requests never execute KDE commands");
        var state = Output(); state["name"] = "HDMI-A-1";
        runtime.OnRun = (command, _) => Task.FromResult(Encoding.UTF8.GetBytes(new JsonObject { ["outputs"] = new JsonArray(state.DeepClone()) }.ToJsonString()));
        await Verify.Reject(() => display.Resize(1280, 800, 90), "Physical outputs cannot be resized", "unavailable");
        Verify.That(runtime.Commands.Single()[^1] == "--json", "Selecting physical output runs only the read command");
        state = Output(); runtime.Commands.Clear();
        runtime.OnRun = (command, _) =>
        {
            Verify.That(command[..3].SequenceEqual(new[] { "/usr/bin/env", "QT_QPA_PLATFORM=wayland", "/usr/bin/kscreen-doctor" }), "KDE uses Wayland even when Sunshine's parent environment selects offscreen");
            if (command[^1].Contains(".addCustomMode.")) ((JsonArray)state["modes"]!).Add((JsonNode)Mode("1", 1280, 800, 90));
            if (command[^1].EndsWith(".mode.1")) state["currentModeId"] = "1";
            return Task.FromResult(Encoding.UTF8.GetBytes(new JsonObject { ["outputs"] = new JsonArray(state.DeepClone()) }.ToJsonString()));
        };
        var actual = await display.Resize(1280, 800, 90);
        Verify.That(actual["currentModeId"]!.GetValue<string>() == "1", "Custom mode is applied and verified");
        Verify.That(runtime.Commands.Where(c => c[^1] != "--json").Select(c => c[^1]).SequenceEqual(new[] { "output.Virtual-Xur-Stream.addCustomMode.1280.800.90000.full", "output.Virtual-Xur-Stream.mode.1" }), "Only Xur's named virtual output receives mutations");
        runtime.Commands.Clear(); await display.Resize(1280, 800, 90);
        Verify.That(runtime.Commands.Count(c => c[^1].Contains(".addCustomMode.")) == 0, "An existing mode is reused");
        runtime.Commands.Clear();
        runtime.OnRun = (c, _) => Task.FromResult(Encoding.UTF8.GetBytes(new JsonObject { ["outputs"] = new JsonArray(state.DeepClone()) }.ToJsonString()));
        await Verify.Reject(() => display.Resize(2560, 1440, 120), "A rejected mode retains the previous display", "retained");
        Verify.That(runtime.Commands.All(c => !c[^1].Contains(".mode.")), "Rejected custom mode is never selected");
        state["currentModeId"] = "0";
        await Verify.Reject(() => display.Resize(1280, 800, 90), "A mode that was not applied cannot report success", "did not switch");
        ((JsonArray)state["modes"]!).Add((JsonNode)Mode("2", 1280, 800, 29.644));
        ((JsonArray)state["modes"]!).Add((JsonNode)Mode("3", 1280, 800, 29.835)); state["currentModeId"] = "3";
        runtime.Commands.Clear(); await display.Resize(1280, 800, 30);
        Verify.That(runtime.Commands.Any(c => c[^1].EndsWith(".mode.3")) && runtime.Commands.All(c => !c[^1].Contains(".addCustomMode.")), "The closest nominal CVT refresh rate is reused");
    }

    public static async Task Updates()
    {
        foreach (var scenario in new[] { "normal", "os-fails", "queued", "current", "not-staged", "busy", "rollback" })
        {
            using var fixture = new Fixture(); var calls = new List<(string Tool, string Action)>(); var staged = false;
            Task<JsonObject?> Invoke(string tool, string action, CancellationToken token)
            {
                calls.Add((tool, action));
                if (tool == "os-update" && scenario == "os-fails") throw new UserError("Offline");
                if (tool == "os-update" && action == "stage" && scenario != "not-staged") staged = true;
                var key = tool == "os-update" ? "digest" : "id";
                return Task.FromResult<JsonObject?>(action != "status" ? null : new JsonObject
                {
                    ["busy"] = tool == "os-update" && scenario == "busy", ["rollbackQueued"] = tool == "os-update" && scenario == "rollback",
                    ["pending"] = tool == "os-update" && (staged || scenario == "queued") ? new JsonObject { ["version"] = "next" } : null,
                    ["server"] = scenario == "queued" ? "" : "server", ["current"] = new JsonObject { [key] = "old" },
                    ["available"] = new JsonObject { [key] = scenario == "current" ? "old" : "new" }
                });
            }
            var updates = new UpdateAll(fixture.Root, "", new FakeRuntime(), new DurableFiles(), Invoke);
            var result = await updates.Execute();
            var results = (JsonArray)result["results"]!;
            Verify.That(results.Count == 2 && !calls.Any(c => c.Action is "reboot" or "rollback"), "Both components produce receipts and never reboot or roll back automatically");
            Verify.That(DurableFiles.ReadJson(fixture.PathOf("operation.json"))!.ToJsonString() == result.ToJsonString(), "Final update receipt is durable");
            if (scenario == "normal") Verify.That(calls.IndexOf(("os-update", "stage")) < calls.IndexOf(("app-update", "update")), "OS stages before the application update");
            if (scenario is "os-fails" or "not-staged" or "busy")
                Verify.That(JsonValues.Text(result["stage"]) == "Failed" && calls.Contains(("app-update", "update")), "OS failures remain visible without skipping application updates");
            if (scenario == "queued") Verify.That(calls.Count == 2 && results.All(r => JsonValues.Text(r?["stage"]) == "Skipped"), "Queued OS and unconfigured app only report status");
            if (scenario == "rollback") Verify.That(!calls.Contains(("os-update", "check")) && calls.Contains(("app-update", "update")), "A queued rollback also leaves the OS untouched");
            if (scenario == "current") Verify.That(!calls.Any(c => c.Action is "stage" or "update"), "Current versions are not updated again");
            new DurableFiles().WriteJson(fixture.PathOf("operation.json"), new JsonObject { ["stage"] = "Running" });
            Verify.That(JsonValues.Text(updates.Status()["operation"]?["stage"]) == "Interrupted", "An abandoned durable operation is reported as interrupted");
            using (Linux.Lock(fixture.PathOf("lock"))) Verify.That(JsonValues.Boolean(updates.Status()["busy"]), "A held process lock reports a busy operation");
        }
    }
}
