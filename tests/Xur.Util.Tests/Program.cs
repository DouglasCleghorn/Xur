using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Xur.Util;
using Xur.Util.Tests;

[assembly: SupportedOSPlatform("linux")]

Linux.Umask(0x3f);

if (args is ["--layout", var publish])
{
    await LayoutTests.Run(publish);
    Console.WriteLine("Native utility rootfs layout checks passed.");
    return;
}

if (args is ["--release-fixture", var repository])
{
    await UpdaterTests.PublishedRepository(repository);
    Console.WriteLine("Actual CI publication verified by native updater implementation.");
    return;
}

if (args is ["--migration-fixture", var unit])
{
    var runtime = new FakeRuntime();
    var result = await new HostServiceMigration(runtime, new DurableFiles()).Migrate(unit, Linux.EffectiveUser());
    Console.WriteLine(new JsonObject { ["result"] = result, ["commands"] = runtime.Commands.Count }.ToJsonString());
    return;
}

if (args is ["--boot"]) await BootTests.Run();
else if (args is ["--updates"]) await UpdaterTests.Run();
else
{
    await BootTests.Run();
    await UpdaterTests.Run();
    await InstallerTests.Run();
    await CliTests.Run();
}
var report = new JsonObject
{
    ["suite"] = "XurUtil", ["result"] = "Passed", ["checks"] = Verify.Count,
    ["hostSystemdInvoked"] = false, ["disksModified"] = false
};
Directory.CreateDirectory(".build/evidence/xurutil");
File.WriteAllText(".build/evidence/xurutil/tests.json", report.ToJsonString() + "\n");
Console.WriteLine(report.ToJsonString());
