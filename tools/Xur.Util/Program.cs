using System.CommandLine;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Xur.IO;

[assembly: SupportedOSPlatform("linux")]

namespace Xur.Util;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("xurutil requires Linux.");
            return 1;
        }
        Linux.Umask(0x3f); // 0077: runtime state and downloaded trust material are root-private.
        return await CreateCommand().Parse(args).InvokeAsync();
    }

    public static RootCommand CreateCommand()
    {
        var runtime = new Runtime();
        var files = new DurableFiles();
        var downloads = new Downloads();
        var root = new RootCommand("Xur startup, installer and signed application update utilities");

        static void Bind(Command command, Func<CancellationToken, Task> action) => command.SetAction(async (_, token) =>
        {
            try { await action(token); return 0; }
            catch (Exception error) { Console.Error.WriteLine("xurutil: " + error.Message); return 1; }
        });

        var host = new Command("host", "Repair installed host startup dependencies");
        root.Subcommands.Add(host);
        var migrate = new Command("migrate", "Migrate recognized Xur service dependencies without changing administrator settings");
        host.Subcommands.Add(migrate);
        Bind(migrate, async token =>
        {
            if (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
                throw new UserError("Publish the Native AOT utility before migrating installed recovery");
            var result = await new HostServiceMigration(runtime, files).Migrate(cancellationToken: token);
            Console.WriteLine("Xur agent dependency migration: " + result);
            var recovery = await new RecoveryMigration(runtime, files).Migrate(Environment.ProcessPath!, cancellationToken: token);
            Console.WriteLine("Xur recovery migration: " + recovery);
        });

        var logs = new Command("logs", "Configure Xur journal compression");
        root.Subcommands.Add(logs);
        var configure = new Command("configure", "Install and activate journal compression defaults");
        var destination = new Option<string>("--root")
        {
            Description = "Absolute target root; offline roots are configured without contacting systemd",
            DefaultValueFactory = _ => "/"
        };
        configure.Options.Add(destination);
        configure.SetAction(async (parsed, token) =>
        {
            try
            {
                var result = await new LogCompression(runtime, files).Ensure(parsed.GetValue(destination)!, cancellationToken: token);
                Console.WriteLine("Journal zstd compression: " + result);
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine("xurutil: " + error.Message); return 1; }
        });
        logs.Subcommands.Add(configure);

        var installer = new Command("installer", "Start and verify the bundled installer application");
        root.Subcommands.Add(installer);
        var checkRuntime = new Command("check-runtime", "Verify installer executable paths and Fedora's merged /usr layout");
        var runtimeRoot = new Option<string>("--root") { DefaultValueFactory = _ => "/" };
        checkRuntime.Options.Add(runtimeRoot);
        checkRuntime.SetAction(parsed =>
        {
            try { InstallerPreflight.CheckRuntime(parsed.GetValue(runtimeRoot)!); return 0; }
            catch (Exception error) { Console.Error.WriteLine("xurutil: " + error.Message); return 1; }
        });
        installer.Subcommands.Add(checkRuntime);
        var syncClock = new Command("sync-clock", "Synchronize UTC and verify the hardware clock before disk erasure");
        installer.Subcommands.Add(syncClock);
        Bind(syncClock, async token =>
        {
            if (Linux.EffectiveUser() != 0) throw new UserError("Root privileges required");
            try
            {
                await new InstallerPreflight(runtime).SyncClock(token: token);
                Console.WriteLine("System time synchronized; hardware clock verified when present.");
            }
            catch (Exception error) when (error is UserError or OperationCanceledException)
            {
                var message = error.Message + " Installation stopped before disk erasure.";
                files.Write("/run/xur/install-error", System.Text.Encoding.UTF8.GetBytes(message + "\n"));
                throw new UserError(message);
            }
        });
        foreach (var name in new[] { "prepare", "verify", "check" })
        {
            var command = new Command(name, name switch
            {
                "prepare" => "Select the bundled app; refresh before startup only with xur.app-update=on",
                "verify" => "Verify service health or restore the bundled app before disk approval",
                _ => "Check for signed releases without interrupting the running installer"
            });
            installer.Subcommands.Add(command);
            Bind(command, token =>
            {
                var bootstrap = new InstallerBootstrap(new InstallerPaths(), runtime, files, downloads);
                return name switch { "prepare" => bootstrap.Prepare(token), "verify" => bootstrap.Verify(token), _ => bootstrap.Check(token) };
            });
        }

        var application = new Command("app-update", "Signed application updates and independent recovery");
        root.Subcommands.Add(application);
        var updater = new SignedUpdater(UpdatePaths.Installed, runtime, files, downloads);
        async Task Run(string action, string[] values, CancellationToken token)
        {
            var result = await updater.Execute(action, values, token);
            if (result is not null) Console.WriteLine(result.ToJsonString());
        }
        Bind(application, token => Run("status", [], token));
        foreach (var name in new[] { "status", "check", "update", "rollback", "recover" })
        {
            var command = new Command(name);
            application.Subcommands.Add(command);
            Bind(command, token => Run(name, [], token));
        }
        void WithArguments(string name, string[] names)
        {
            var command = new Command(name);
            var arguments = names.Select(argumentName => new Argument<string>(argumentName)
            {
                Arity = argumentName is "server" or "public-key" && name == "channel" ? ArgumentArity.ZeroOrOne : ArgumentArity.ExactlyOne
            }).ToArray();
            foreach (var argument in arguments) command.Arguments.Add(argument);
            command.SetAction(async (parsed, token) =>
            {
                try { await Run(name, arguments.Select(argument => parsed.GetValue(argument)).Where(value => value is not null).ToArray()!, token); return 0; }
                catch (Exception error) { Console.Error.WriteLine("xurutil: " + error.Message); return 1; }
            });
            application.Subcommands.Add(command);
        }
        WithArguments("configure", ["server"]);
        WithArguments("channel", ["channel", "server", "public-key"]);
        WithArguments("development", ["enabled"]);
        WithArguments("compatibility", ["bundle-id"]);

        var updates = new Command("update-all", "Run sequential OS and application updates independently of the manager");
        root.Subcommands.Add(updates);
        foreach (var name in new[] { "status", "run" })
        {
            var command = new Command(name);
            updates.Subcommands.Add(command);
            Bind(command, async token =>
            {
                const string state = "/var/lib/xur/update-all";
                if (name == "status")
                {
                    Console.WriteLine(new UpdateAll(state, "", runtime, files).Status().ToJsonString());
                    return;
                }
                if (Linux.EffectiveUser() != 0 || !File.Exists("/var/lib/xur/installed")) throw new UserError("Installed system required");
                Directory.CreateDirectory(state);
                using var locked = Linux.Lock(Path.Combine(state, "lock"));
                var current = Directory.ResolveLinkTarget("/var/lib/xur/app/current", true)?.FullName ?? throw new UserError("Installed application is unavailable");
                await new UpdateAll(state, Path.Combine(current, "host"), runtime, files).Execute(token);
            });
        }

        var display = new Command("display", "Control Xur's virtual KDE monitor as the workstation user");
        root.Subcommands.Add(display);
        var monitor = new VirtualDisplay(runtime);
        var observe = new Command("status");
        display.Subcommands.Add(observe);
        Bind(observe, async token => Console.WriteLine((await monitor.Observe(token)).ToJsonString()));
        var resize = new Command("resize");
        display.Subcommands.Add(resize);
        var dimensions = new[] { "width", "height", "fps" }.Select(name => new Argument<int>(name)).ToArray();
        foreach (var argument in dimensions) resize.Arguments.Add(argument);
        resize.SetAction(async (parsed, token) =>
        {
            try { Console.WriteLine((await monitor.Resize(parsed.GetValue(dimensions[0]), parsed.GetValue(dimensions[1]), parsed.GetValue(dimensions[2]), token)).ToJsonString()); return 0; }
            catch (Exception error) { Console.Error.WriteLine("xurutil: " + error.Message); return 1; }
        });
        var moonlight = new Command("moonlight", "Apply the client's requested mode; retain the desktop on failure");
        display.Subcommands.Add(moonlight);
        moonlight.SetAction(async (_, token) =>
        {
            try
            {
                var values = new[] { "SUNSHINE_CLIENT_WIDTH", "SUNSHINE_CLIENT_HEIGHT", "SUNSHINE_CLIENT_FPS" }
                    .Select(key => int.Parse(Environment.GetEnvironmentVariable(key) ?? "", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                await monitor.Resize(values[0], values[1], values[2], token);
            }
            catch (Exception error) { Console.Error.WriteLine("Xur virtual monitor: " + error.Message); }
            return 0; // A rejected client mode must not prevent streaming the existing desktop.
        });
        var fileCommands = new Command("files", "Browse, measure and stream files within an authorized root");
        root.Subcommands.Add(fileCommands);
        foreach (var mode in new[] { "list", "size", "scan", "download", "stored", "compressed", "move", "delete" })
        {
            var command = new Command(mode);
            var folder = new Argument<string>("root");
            var path = new Argument<string>("path") { Arity = ArgumentArity.ZeroOrOne, DefaultValueFactory = _ => "" };
            var query = new Argument<string>("query") { Arity = ArgumentArity.ZeroOrOne, DefaultValueFactory = _ => "" };
            command.Arguments.Add(folder); command.Arguments.Add(path); command.Arguments.Add(query);
            command.SetAction(async (parsed, token) =>
            {
                var operation = new FileOperations();
                using var output = Console.OpenStandardOutput();
                try
                {
                    var rootPath = parsed.GetValue(folder)!; var relative = parsed.GetValue(path)!;
                    if (mode == "scan")
                    {
                        var result = FolderScanner.Scan(rootPath, relative, cancellationToken: token);
                        var entries = new JsonArray();
                        foreach (var row in result.Entries) entries.Add((JsonNode)new JsonObject { ["name"] = row.Name, ["kind"] = row.Kind, ["bytes"] = row.Bytes, ["partial"] = row.Partial });
                        Console.WriteLine(new JsonObject { ["path"] = relative, ["entries"] = entries, ["bytes"] = result.Bytes, ["partial"] = result.Partial, ["errors"] = result.Errors, ["visited"] = result.Visited, ["truncated"] = result.Truncated }.ToJsonString());
                    }
                    else await operation.Run(mode, rootPath, relative, parsed.GetValue(query)!, output, token);
                    return 0;
                }
                catch (Exception error)
                {
                    // Never append JSON to a download body after its success header.
                    if (!operation.Started) Console.WriteLine(new JsonObject { ["ok"] = false, ["error"] = error.Message }.ToJsonString());
                    Console.Error.WriteLine("xurutil: " + error.Message); return 1;
                }
            });
            fileCommands.Subcommands.Add(command);
        }
        return root;
    }
}
