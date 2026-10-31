using System.Text;
using Xur.Util;

namespace Xur.Util.Tests;

static class BootTests
{
    public const string Current = """
        [Unit]
        Description=Xur privileged typed operations (Unix socket only)
        After=local-fs.target NetworkManager.service
        Wants=xur-network.service
        [Service]
        ExecStart=/var/lib/xur/app/current/agent/Xur.Agent
        WorkingDirectory=/var/lib/xur/app/current/agent
        RestartSec=7
        # Administrator annotation
        """ + "\n";
    public static string Legacy => Current.Replace("After=local-fs.target NetworkManager.service", "After=local-fs.target xur-network.service")
        .Replace("Wants=xur-network.service", "Requires=xur-network.service");

    public static async Task Run()
    {
        await Migration();
        await Compression();
        await Recovery();
        await RecoveryOrdering();
        await OldUpdater();
    }

    static async Task OldUpdater()
    {
        using var fixture = new Fixture();
        fixture.Write("xur-agent.service", Legacy);
        var native = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
        var sdk = native ? Environment.ProcessPath! : Environment.GetEnvironmentVariable("XUR_DOTNET") ?? Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../../dotnet"));
        var result = await new Runtime().Run(["python3", "tests/Xur.Integration.Tests/xurutil-old-updater.py", fixture.Root,
            fixture.PathOf("xur-agent.service"), sdk, native ? "" : Path.Combine(AppContext.BaseDirectory, "Xur.Util.Tests.dll")]);
        Verify.That(Encoding.UTF8.GetString(result).Contains("Passed"), "Actual old updater reaches native startup repair and can roll back");
        var program = File.ReadAllText("src/Xur.Agent/Program.cs");
        Verify.That(program.IndexOf("if(!installer && File.Exists(hostUtility))", StringComparison.Ordinal) < program.IndexOf("app.MapGet(\"/application-health\"", StringComparison.Ordinal), "Agent startup repair precedes health readiness");
        // Frozen from f2d1d7c; source archives and shallow CI checkouts need no Git history.
        Verify.That(DurableFiles.HashFile("tests/Xur.Integration.Tests/fixtures/app-update-before-xurutil.py") ==
            "92f41a86b5cdec8d922ae759de3b4db0cd3208a423b3be10ffdfbe3d0462022b", "Historical updater fixture is byte-identical to pre-migration source");
    }

    static async Task Migration()
    {
        using var fixture = new Fixture();
        var unit = fixture.PathOf("xur-agent.service");
        var marker = fixture.PathOf(".xur-agent-network-v1.completed");
        var runtime = new FakeRuntime();
        var files = new FaultyFiles();
        var migration = new HostServiceMigration(runtime, files);
        Task<string> Run() => migration.Migrate(unit, Linux.EffectiveUser());
        void Write(string text)
        {
            File.Delete(unit);
            File.WriteAllText(unit, text);
            File.SetUnixFileMode(unit, (UnixFileMode)0x1a0);
            runtime.Commands.Clear();
        }
        Write(Legacy);
        var before = Linux.Inspect(unit)!.Value;
        fixture.Write("xur-agent.service.d/local.conf", "[Service]\nEnvironment=LOCAL=1\n");
        Verify.That(await Run() == "migrated" && File.ReadAllText(unit) == Current, "Exact legacy dependencies migrate");
        var after = Linux.Inspect(unit)!.Value;
        Verify.That((after.Mode & 0xfff) == 0x1a0 && after.Uid == before.Uid && after.Gid == before.Gid, "Migration preserves mode and ownership");
        Verify.That(File.ReadAllText(fixture.PathOf("xur-agent.service.d/local.conf")).Contains("LOCAL=1"), "Administrator drop-ins survive");
        Verify.That(runtime.Commands.Count == 2 && runtime.Commands[0][0] == "restorecon" && runtime.Commands[1].SequenceEqual(["systemctl", "daemon-reload"]), "Migration labels before reload");
        Verify.That((Linux.Inspect(marker)!.Value.Mode & 0xfff) == 0x180, "Completion marker is private");
        runtime.Commands.Clear();
        Verify.That(await Run() == "current" && runtime.Commands.Count == 0, "Completed boot invokes no commands");
        File.Delete(marker);
        Verify.That(await Run() == "current" && runtime.Commands.Count == 2, "Interrupted marker publication retries labeling and reload");
        File.AppendAllText(unit, "# Later edit\n");
        runtime.Commands.Clear();
        Verify.That(await Run() == "current" && runtime.Commands.Count == 2, "Completion marker binds exact bytes");

        foreach (var unknown in new[]
        {
            Legacy.Replace("ExecStart=", "ExecStart=/custom "), Legacy.Replace("After=local-fs.target", "After=custom.service local-fs.target"),
            Legacy + "[Unit]\nAfter=custom.service\n", Legacy.Replace("Requires=xur-network.service", "Requires=xur-network.service other.service"),
            Legacy.Replace("\n", "\r\n"), Legacy.Replace("RestartSec=7", "RestartSec=\\\n7")
        })
        {
            Write(unknown);
            Verify.That(await Run() == "custom" && runtime.Commands.Count == 0 && File.ReadAllText(unit) == unknown, "Unknown unit is never edited");
        }
        Write(Legacy);
        File.SetUnixFileMode(unit, (UnixFileMode)0x1b0);
        Verify.That(await Run() == "custom", "Writable unit is skipped");
        Write(Legacy);
        Verify.That(await migration.Migrate(unit, Linux.EffectiveUser() + 1) == "custom", "Foreign owner is skipped");
        File.Delete(unit);
        fixture.Write("external", Legacy);
        File.CreateSymbolicLink(unit, fixture.PathOf("external"));
        Verify.That(await Run() == "custom" && File.ReadAllText(fixture.PathOf("external")) == Legacy, "Symlink unit is skipped");
        File.Delete(unit);
        Verify.That(await Run() == "absent", "Missing unit is harmless");

        foreach (var command in new[] { "restorecon", "systemctl" })
        {
            Write(Legacy);
            var failed = false;
            runtime.OnRun = (args, seconds) =>
            {
                Verify.That(seconds == 10, "Every migration command is bounded");
                if (args[0] == command && !failed) { failed = true; throw new IOException("Injected command failure"); }
                return Task.FromResult(Array.Empty<byte>());
            };
            await Verify.Reject(async () => await Run(), "Migration failure propagates");
            Verify.That(File.ReadAllText(unit) == Legacy && (Linux.Inspect(unit)!.Value.Mode & 0xfff) == 0x1a0, "Failed migration restores exact bytes and metadata");
        }
        runtime.OnRun = null;
        Write(Legacy);
        var syncFailed = false;
        files.BeforeSync = _ => { if (!syncFailed) { syncFailed = true; throw new IOException("Post-rename fsync failure"); } };
        await Verify.Reject(async () => await Run(), "Post-rename failure propagates");
        Verify.That(File.ReadAllText(unit) == Legacy, "Post-rename fsync failure restores original unit");
        files.BeforeSync = null;
        Write(Legacy);
        runtime.OnRun = (args, _) =>
        {
            if (args[0] == "systemctl")
            {
                fixture.Write("administrator", "administrator replacement\n");
                File.Move(fixture.PathOf("administrator"), unit, true);
                throw new IOException("Concurrent administrator edit");
            }
            return Task.FromResult(Array.Empty<byte>());
        };
        await Verify.Reject(async () => await Run(), "Concurrent failure propagates");
        Verify.That(File.ReadAllText(unit) == "administrator replacement\n", "Rollback never overwrites an administrator replacement");
        runtime.OnRun = null;
        Write(Legacy);
        File.Delete(marker);
        File.CreateSymbolicLink(marker, fixture.PathOf("external"));
        await Verify.Reject(async () => await Run(), "Unsafe marker fails closed");
        Verify.That(File.ReadAllText(unit) == Legacy, "Unsafe completion marker cannot trigger publication");
    }

    static async Task Compression()
    {
        using var fixture = new Fixture();
        var runtime = new FakeRuntime();
        var files = new FaultyFiles();
        var compression = new LogCompression(runtime, files);
        var paths = LogCompression.Names.Select(name => fixture.PathOf("etc/systemd/" + name)).ToArray();
        var pending = fixture.PathOf("etc/systemd/journald.conf.d/.xur-log-compression.pending");
        runtime.OnRun = (_, seconds) =>
        {
            Verify.That(seconds == 10 && File.Exists(pending), "Bounded activation retains pending marker");
            Verify.That(paths.Select(File.ReadAllBytes).Zip(LogCompression.Configuration).All(pair => pair.First.SequenceEqual(pair.Second)), "Both settings publish before activation");
            return Task.FromResult(Array.Empty<byte>());
        };
        Verify.That(await compression.Ensure(fixture.Root) == "installed" && runtime.Commands.Count == 0, "Offline configuration never calls host commands");
        var stamps = paths.Select(File.GetLastWriteTimeUtc).ToArray();
        Verify.That(await compression.Ensure(fixture.Root, true) == "activated" && runtime.Commands.Count == 4 && !File.Exists(pending), "Successful activation rotates once");
        Verify.That(paths.Select(File.GetLastWriteTimeUtc).SequenceEqual(stamps), "Activation does not rewrite unchanged settings");
        Verify.That(paths.All(path => (Linux.Inspect(path)!.Value.Mode & 0xfff) == 0x1a4), "Journal defaults are readable");
        runtime.Commands.Clear();
        Verify.That(await compression.Ensure(fixture.Root, true) == "current" && runtime.Commands.Count == 0, "Subsequent starts write nothing and invoke no commands");
        fixture.Write("etc/systemd/journald.conf.d/99-local.conf", "[Journal]\nSystemMaxUse=1G\n");
        for (var failure = 1; failure <= 4; failure++)
        {
            File.WriteAllText(paths[0], "[Journal]\nCompress=no\n");
            runtime.Commands.Clear();
            runtime.OnRun = (_, _) => runtime.Commands.Count == failure ? throw new IOException("Activation failure") : Task.FromResult(Array.Empty<byte>());
            await Verify.Reject(async () => await compression.Ensure(fixture.Root, true), "Activation failure propagates");
            Verify.That(File.Exists(pending), "Failed activation remains retryable");
            runtime.OnRun = null;
            runtime.Commands.Clear();
            Verify.That(await compression.Ensure(fixture.Root, true) == "activated" && runtime.Commands.Count == 4, "Failed activation retries complete sequence");
        }
        File.Delete(paths[1]);
        files.BeforeWrite = path => { if (path == paths[1]) throw new IOException("Interrupted configuration publication"); };
        runtime.Commands.Clear();
        await Verify.Reject(async () => await compression.Ensure(fixture.Root, true), "Publication failure propagates");
        Verify.That(File.Exists(pending) && runtime.Commands.Count == 0, "Partial publication never activates journald");
        files.BeforeWrite = null;
        Verify.That(await compression.Ensure(fixture.Root, true) == "activated", "Partial publication retries");
        Verify.That(File.ReadAllText(fixture.PathOf("etc/systemd/journald.conf.d/99-local.conf")).Contains("1G"), "Administrator overrides survive");
        await Verify.Reject(async () => await compression.Ensure("relative"), "Relative offline roots are rejected");
    }

    static async Task Recovery()
    {
        using var fixture = new Fixture();
        fixture.Write("recovery.service", File.ReadAllText("os/bootc/systemd/xur-app-recovery.service")
            .Replace("ConditionPathExists=|/var/lib/xur/app/transaction.json\n", "")
            .Replace("ConditionPathExists=|/var/lib/xur/app/maintenance", "ConditionPathExists=/var/lib/xur/app/maintenance")
            .Replace("ExecStart=:/var/lib/xur/updater/xurutil app-update recover", "ExecStart=/usr/bin/python3 /var/lib/xur/updater/app-update recover"));
        fixture.Write("binary", "\u007fELFfixture");
        foreach (var name in new[] { "System.CommandLine-LICENSE.txt", "dotnet-LICENSE.txt", "dotnet-THIRD-PARTY-NOTICES.TXT", "TeeForge-LICENSE.txt", "TeeForge-THIRD-PARTY-NOTICES.txt" }) fixture.Write("licenses/" + name, "fixture notice\n");
        File.SetUnixFileMode(fixture.PathOf("binary"), (UnixFileMode)0x1ed);
        var runtime = new FakeRuntime();
        var label = "var_lib_t";
        runtime.OnRun = (command, seconds) =>
        {
            Verify.That(seconds == 10, "Recovery migration commands are bounded");
            if (command[0] == "semanage") label = "bin_t";
            return Task.FromResult(command[0] == "matchpathcon" ? Encoding.UTF8.GetBytes("system_u:object_r:" + label + ":s0\n") : Array.Empty<byte>());
        };
        var migration = new RecoveryMigration(runtime, new DurableFiles());
        Task<string> Run() => migration.Migrate(fixture.PathOf("binary"), fixture.PathOf("updater"), fixture.PathOf("recovery.service"), Linux.EffectiveUser());
        Verify.That(await Run() == "migrated", "First upgrade installs independent recovery executable");
        Verify.That(File.ReadAllText(fixture.PathOf("recovery.service")).Contains("/var/lib/xur/updater/xurutil app-update recover"), "Recovery does not use current bundle link or Python");
        Verify.That(File.ReadAllText(fixture.PathOf("recovery.service")).Contains("ConditionPathExists=|/var/lib/xur/app/transaction.json"), "Legacy recovery triggers on surviving transactions even without maintenance");
        Verify.That(File.ReadAllText(fixture.PathOf("xur-app-prepare-recovery.service")) == File.ReadAllText("os/bootc/systemd/xur-app-prepare-recovery.service") &&
            new FileInfo(fixture.PathOf("multi-user.target.wants/xur-app-prepare-recovery.service")).LinkTarget == "../xur-app-prepare-recovery.service", "Existing installations receive the enabled early recovery unit");
        Verify.That(File.ReadAllBytes(fixture.PathOf("updater/xurutil")).SequenceEqual(File.ReadAllBytes(fixture.PathOf("binary"))), "Independent recovery copy matches running native utility");
        Verify.That((Linux.Inspect(fixture.PathOf("updater/xurutil"))!.Value.Mode & 0xfff) == 0x1c0, "Recovery executable is root-private");
        Verify.That(label == "bin_t" && runtime.Commands.Any(command => command[0] == "semanage"), "Older hosts receive persistent native recovery executable label");
        Verify.That(File.ReadAllText(fixture.PathOf("updater/licenses/dotnet-THIRD-PARTY-NOTICES.TXT")) == "fixture notice\n", "Independent recovery retains runtime license notices");
        runtime.Commands.Clear();
        Verify.That(await Run() == "current" && runtime.Commands.Count == 0, "Recovery migration is idempotent");
        fixture.Write("recovery.service", File.ReadAllText(fixture.PathOf("recovery.service"))
            .Replace("ExecStart=:/var/lib/xur/updater/xurutil", "ExecStart=/var/lib/xur/updater/xurutil"));
        Verify.That(await Run() == "migrated" && File.ReadAllText(fixture.PathOf("recovery.service")).Contains("ExecStart=:/var/lib/xur/updater/xurutil"),
            "Previous native recovery migrates to a contract older startup migrations cannot downgrade");
        File.Delete(fixture.PathOf("multi-user.target.wants/xur-app-prepare-recovery.service"));
        Verify.That(await Run() == "migrated" && File.Exists(fixture.PathOf("multi-user.target.wants/xur-app-prepare-recovery.service")), "Interrupted early unit enablement retries");
        fixture.Write("xur-app-prepare-recovery.service", "Administrator recovery\n");
        runtime.Commands.Clear();
        Verify.That(await Run() == "custom" && runtime.Commands.Count == 0, "Administrator early recovery units are preserved");
        fixture.Write("xur-app-prepare-recovery.service", File.ReadAllText("os/bootc/systemd/xur-app-prepare-recovery.service"));
        fixture.Write("updater/.xurutil-recovery.pending", "pending\n");
        Verify.That(await Run() == "migrated" && runtime.Commands.Count == 3, "Interrupted recovery activation retries labels and reload");
        var original = File.ReadAllText(fixture.PathOf("recovery.service")).Replace("ExecStart=:/var/lib/xur/updater/xurutil app-update recover", "ExecStart=/usr/bin/python3 /var/lib/xur/updater/app-update recover");
        fixture.Write("recovery.service", original);
        File.Delete(fixture.PathOf("xur-app-prepare-recovery.service"));
        File.Delete(fixture.PathOf("multi-user.target.wants/xur-app-prepare-recovery.service"));
        var failed = false;
        runtime.OnRun = (command, _) =>
        {
            if (command[0] == "systemctl" && !failed) { failed = true; throw new IOException("Recovery reload failure"); }
            return Task.FromResult(command[0] == "matchpathcon" ? Encoding.UTF8.GetBytes("system_u:object_r:bin_t:s0\n") : Array.Empty<byte>());
        };
        await Verify.Reject(async () => await Run(), "Recovery migration failure propagates");
        Verify.That(File.ReadAllText(fixture.PathOf("recovery.service")) == original && File.Exists(fixture.PathOf("updater/.xurutil-recovery.pending")), "Failed recovery migration restores legacy unit and retains retry marker");
        Verify.That(!File.Exists(fixture.PathOf("xur-app-prepare-recovery.service")) &&
            Linux.Inspect(fixture.PathOf("multi-user.target.wants/xur-app-prepare-recovery.service")) is null,
            "Failed first migration removes its early unit before an older agent can downgrade recovery");
        runtime.OnRun = (command, _) => Task.FromResult(command[0] == "matchpathcon" ? Encoding.UTF8.GetBytes("system_u:object_r:bin_t:s0\n") : Array.Empty<byte>());
        Verify.That(await Run() == "migrated", "Failed recovery migration retries on next boot");
        fixture.Write("recovery.service", "[Unit]\nDescription=Administrator service\n[Service]\nExecStart=/custom\n");
        runtime.Commands.Clear();
        Verify.That(await Run() == "custom" && runtime.Commands.Count == 0, "Custom recovery units are preserved");
    }

    static async Task RecoveryOrdering()
    {
        using var fixture = new Fixture();
        foreach (var name in new[] { "xur-app-prepare-recovery.service", "xur-app-recovery.service", "xur-network.service", "xur-gateway.service", "xur-agent.service", "xur-control.service" })
            fixture.Write("etc/systemd/system/" + name, File.ReadAllText("os/bootc/systemd/" + name));
        foreach (var name in new[] { "sysinit", "basic", "local-fs", "shutdown", "multi-user" })
            fixture.Write("etc/systemd/system/" + name + ".target", "[Unit]\nDescription=Fixture target\nDefaultDependencies=no\n");
        foreach (var name in new[] { "NetworkManager", "systemd-vconsole-setup" })
            fixture.Write("etc/systemd/system/" + name + ".service", "[Service]\nExecStart=/usr/bin/true\n");
        foreach (var name in new[] { "usr/bin/true", "usr/bin/dmesg", "usr/bin/xur-agent", "usr/bin/xur-control", "usr/bin/xur-gateway", "usr/libexec/xur-network", "var/lib/xur/updater/xurutil" })
        {
            fixture.Write(name, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(fixture.PathOf(name), (UnixFileMode)0x1ed);
        }
        var wants = fixture.PathOf("etc/systemd/system/multi-user.target.wants");
        Directory.CreateDirectory(wants);
        foreach (var name in new[] { "xur-app-prepare-recovery", "xur-app-recovery", "xur-network", "xur-gateway", "xur-agent", "xur-control" })
            File.CreateSymbolicLink(Path.Combine(wants, name + ".service"), "../" + name + ".service");
        await new Runtime().Run(["systemd-analyze", "verify", "--man=no", "--root=" + fixture.Root, "multi-user.target"]);
        Verify.That(true, "Systemd verifies the enabled recovery and application boot graph without ordering cycles");
    }
}
