using Xur.Util;

namespace Xur.Util.Tests;

static class CliTests
{
    public static async Task Run()
    {
        var command = Xur.Util.Program.CreateCommand();
        foreach (var invalid in new[]
        {
            new[] { "unknown" }, new[] { "logs", "configure", "--root" }, new[] { "app-update", "channel" },
            new[] { "app-update", "channel", "stable", "extra", "extra", "extra" },
            new[] { "display", "resize", "1;id", "1080", "60" }, new[] { "display", "resize", "1920" },
            new[] { "update-all", "unknown" }, new[] { "installer", "check-runtime", "--root" }
        }) Verify.That(command.Parse(invalid).Errors.Count != 0, "Microsoft command parser rejects malformed invocations before actions run");
        Verify.That(command.Parse(["app-update", "channel", "local", "192.0.2.10:8088", "fixture public key"]).Errors.Count == 0, "Channel arguments retain legacy positional contract");
        Verify.That(command.Parse(["app-update", "channel", "local", "192.0.2.10:8088", "--", "-----BEGIN PUBLIC KEY-----\nfixture\n-----END PUBLIC KEY-----"]).Errors.Count == 0, "PEM arguments beginning with dashes use the argument delimiter");
        using var fixture = new Fixture();
        Verify.That(await command.Parse(["logs", "configure", "--root", fixture.Root]).InvokeAsync() == 0, "CLI installs journal settings in an offline fixture root");
        Verify.That(File.Exists(fixture.PathOf("etc/systemd/journald.conf.d/.xur-log-compression.pending")), "Offline CLI keeps activation pending for the installed boot");
        Verify.That(await command.Parse(["logs", "configure", "--root", "relative"]).InvokeAsync() == 1, "CLI returns nonzero for invalid roots");
        Verify.That(await command.Parse(["display", "resize", "1921", "1080", "60"]).InvokeAsync() == 1, "CLI rejects invalid modes without invoking KDE");
        var clientWidth = Environment.GetEnvironmentVariable("SUNSHINE_CLIENT_WIDTH");
        try
        {
            Environment.SetEnvironmentVariable("SUNSHINE_CLIENT_WIDTH", "invalid");
            Verify.That(await command.Parse(["display", "moonlight"]).InvokeAsync() == 0, "Invalid Moonlight settings keep the existing desktop available");
        }
        finally { Environment.SetEnvironmentVariable("SUNSHINE_CLIENT_WIDTH", clientWidth); }
        var output = Console.Out;
        using var help = new StringWriter();
        try
        {
            Console.SetOut(help);
            Verify.That(await command.Parse(["--help"]).InvokeAsync() == 0, "CLI provides Microsoft command-line help");
        }
        finally { Console.SetOut(output); }
        Verify.That(help.ToString().Contains("installer") && help.ToString().Contains("app-update"), "CLI help exposes shipped runtime commands");
        Directory.CreateDirectory(".build/evidence/xurutil");
        File.WriteAllText(".build/evidence/xurutil/cli-help.txt", help.ToString());
    }
}
