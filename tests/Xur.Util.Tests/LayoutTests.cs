using System.Text.Json.Nodes;
using Xur.Util;

namespace Xur.Util.Tests;

static class LayoutTests
{
    public static async Task Run(string publish)
    {
        publish = Path.GetFullPath(publish);
        using var fixture = new Fixture();
        var checkout = fixture.PathOf("checkout");
        void CopyTree(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            foreach (var directory in Directory.EnumerateDirectories(source)) CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
        CopyTree("os", Path.Combine(checkout, "os"));
        fixture.Write("checkout/LICENSE", File.ReadAllText("LICENSE"));
        fixture.Write("checkout/docs/licensing.md", File.ReadAllText("docs/licensing.md"));
        fixture.Write("checkout/eng/prepare-rootfs.py", File.ReadAllText("eng/prepare-rootfs.py"));
        fixture.Write("checkout/.build/context/catalog/fixture.json", "{}");
        foreach (var name in new[] { "control", "agent", "gateway" })
        {
            var executable = "Xur." + char.ToUpperInvariant(name[0]) + name[1..];
            var relative = "checkout/.build/context/publish/" + name + "/" + executable;
            fixture.Write(relative, "fixture " + name);
            File.SetUnixFileMode(fixture.PathOf(relative), (UnixFileMode)0x1ed);
        }
        foreach (var name in new[] { "console", "virtual-display", "streaming" }) fixture.Write("checkout/.build/" + name + "-runtime/fixture", name);
        foreach (var name in new[] { "tailscale", "tailscaled" }) fixture.Write("checkout/.build/context/tailscale/" + name, name);
        CopyTree(publish, Path.Combine(checkout, ".build/context/publish/util"));
        await new Runtime().Run(["python3", Path.Combine(checkout, "eng/prepare-rootfs.py")]);
        var root = Path.Combine(checkout, ".build/context/rootfs");
        var bundle = Path.Combine(root, "usr/share/xur/app-bundle");
        Verify.That(File.ReadAllBytes(Path.Combine(bundle, "host/xurutil")).SequenceEqual(File.ReadAllBytes(Path.Combine(publish, "xurutil"))), "Application bundle contains the published native utility");
        Verify.That(File.ReadAllBytes(Path.Combine(root, "usr/libexec/xurutil")).SequenceEqual(File.ReadAllBytes(Path.Combine(publish, "xurutil"))), "Live OS contains the same native utility");
        Verify.That((File.GetUnixFileMode(Path.Combine(bundle, "host/xurutil")) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0, "Bundled native utility is protected before installation");
        Verify.That(!Directory.EnumerateFiles(bundle, "*.dbg", SearchOption.AllDirectories).Any(), "Debug symbols are excluded from the shipped bundle");
        foreach (var name in new[] { "System.CommandLine-LICENSE.txt", "dotnet-LICENSE.txt", "dotnet-THIRD-PARTY-NOTICES.TXT", "TeeForge-LICENSE.txt", "TeeForge-THIRD-PARTY-NOTICES.txt" })
            Verify.That(File.Exists(Path.Combine(bundle, "host/licenses", name)) && File.Exists(Path.Combine(root, "usr/share/licenses/xurutil", name)), "Native dependency notices ship in both layouts");
        Verify.That(File.Exists(Path.Combine(bundle, "LICENSE")) && File.Exists(Path.Combine(bundle, "licensing.md")), "Bundle carries Xur licensing");
        // Match the installed permissions applied by install-manager and archive extraction.
        foreach (var path in Directory.EnumerateFileSystemEntries(bundle, "*", SearchOption.AllDirectories))
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) & ~(UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
        var metadata = DurableFiles.ReadObject(Path.Combine(bundle, "bundle.json"));
        BundleArchive.Validate(bundle, metadata);
        Verify.That(SignedUpdater.IsHash(JsonValues.Text(metadata["id"])), "Python rootfs manifest validates with the native canonical manifest implementation");
    }
}
