using System.CommandLine;
using System.Runtime.Versioning;

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
        return root;
    }
}
