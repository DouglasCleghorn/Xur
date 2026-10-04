#!/usr/bin/env dotnet
#:include ../../eng/update-usb/app.cs
#:property StartupObject=UsbUpdateTests

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class UsbUpdateTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (IOException) { return; }
        throw new Exception(message);
    }
    private static void Write(string root, string name, byte[] content)
    {
        var target = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, content);
    }
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static async Task<int> Main()
    {
        var calls = 0;
        string? selectedDevice = null;
        var checkOnly = false;
        var command = UsbUpdate.CreateCommand((device, check) =>
        {
            calls++; selectedDevice = device; checkOnly = check;
            return Task.CompletedTask;
        });
        Check(await command.Parse(["--check", "--device", "/dev/sdz1"]).InvokeAsync() == 0 &&
              calls == 1 && selectedDevice == "/dev/sdz1" && checkOnly, "CLI did not bind options.");
        Check(await command.Parse(["--device=/dev/sdy1"]).InvokeAsync() == 0 &&
              calls == 2 && selectedDevice == "/dev/sdy1" && !checkOnly, "CLI equals syntax/defaults failed.");
        foreach (var invalid in new[] { new[] { "--device" }, new[] { "--device", "--check" }, new[] { "--unknown" } })
            Check(command.Parse(invalid).Errors.Count > 0, "CLI accepted missing/unknown arguments: " + string.Join(' ', invalid));
        var previousOut = Console.Out; var previousError = Console.Error;
        using var help = new StringWriter(); using var errors = new StringWriter();
        try
        {
            Console.SetOut(help); Console.SetError(errors);
            Check(await command.Parse(["--help"]).InvokeAsync() == 0 && calls == 2,
                  "CLI help invoked the updater.");
            Check(help.ToString().Contains("--device") && help.ToString().Contains("--check"), "Generated help omitted options.");
            Check(await command.Parse(["--device"]).InvokeAsync() != 0 && calls == 2,
                  "Invalid CLI invoked the updater.");
        }
        finally { Console.SetOut(previousOut); Console.SetError(previousError); }
        Check(Native.geteuid() == 0, "Run this fixture test as root.");
        using var workspace = new Workspace();
        var evidence = Path.Combine(workspace.Path, ".build/evidence/usb-update");
        var source = Path.Combine(evidence, "source");
        Directory.CreateDirectory(source);
        var payload = Enumerable.Range(0, 230123).Select(n => (byte)(n * 37)).ToArray();
        Write(source, "LiveOS/squashfs.img", payload);
        Write(source, "EFI/BOOT/BOOTX64.EFI", [1, 2, 3, 4]);
        foreach (var name in UsbUpdate.Grub) Write(source, name, Encoding.UTF8.GetBytes(
            "search --no-floppy --label XUR_SETUP_A --set=xur_media\nlinux inst.stage2=hd:LABEL=XUR_SETUP_A xur.installer=1\n"));
        Write(source, "xur-diagnostics.yml", Encoding.UTF8.GetBytes("Never overwrite the existing secret"));
        Write(source, "xur.yml", Encoding.UTF8.GetBytes("Never overwrite the answer"));
        Write(source, "images/empty", []);
        var isoPath = Path.Combine(evidence, "fixture.iso");
        UsbUpdate.Command("xorriso", "-as", "mkisofs", "-quiet", "-R", "-V", "XUR_SETUP_A", "-o", isoPath, source);
        var isoBytes = File.ReadAllBytes(isoPath);
        var iso = new MediaPart("fixture.iso", isoBytes.Length, Digest(isoBytes));
        var raw = Path.Combine(evidence, "fat32.raw");
        using (var file = File.Create(raw)) file.SetLength(128 * 1024 * 1024);
        var loop = UsbUpdate.Command("losetup", "--find", "--show", raw);
        try
        {
            UsbUpdate.Command("mkfs.vfat", "-F", "32", "-n", "XUR_TEST", loop);
            using var mount = new Mount(loop, Path.Combine(evidence, "usb"), true);
            var dest = mount.Path;
            var diagnostics = Encoding.UTF8.GetBytes("schemaVersion: 1\napiKey: " + new string('a', 64) + "\nallowControl: true\n");
            var answer = Encoding.UTF8.GetBytes("schemaVersion: 1\nnetwork: {}\n");
            Write(dest, "xur-diagnostics.yml", diagnostics);
            Write(dest, "xur.yml", answer);
            Write(dest, "xur-diagnostics-old.txt", [7, 8, 9]);
            Write(dest, "unrelated/photo.bin", [8, 9, 10]);
            Write(dest, "LiveOS/squashfs.img", [0]);
            var saved = UsbUpdate.Configurations(dest);
            using (var input = new ForwardOnly(new MemoryStream(isoBytes)))
                UsbUpdate.StreamIso(input, iso, dest, "XUR_TEST", saved);
            Check(File.ReadAllBytes(Path.Combine(dest, "LiveOS/squashfs.img")).AsSpan().SequenceEqual(payload), "Multi-buffer ISO entry was corrupted.");
            Check(UsbUpdate.SameConfig(UsbUpdate.Configurations(dest), saved), "Configurations changed.");
            Check(File.ReadAllBytes(Path.Combine(dest, "unrelated/photo.bin")).SequenceEqual(new byte[] { 8, 9, 10 }), "Unrelated file changed.");
            Check(File.Exists(Path.Combine(dest, "xur-diagnostics-old.txt")), "Saved logs removed.");
            Check(UsbUpdate.Grub.All(n => File.ReadAllText(Path.Combine(dest, n)).Contains("LABEL=XUR_TEST")), "USB label replacement failed.");
            Check(File.ReadAllBytes(Path.Combine(dest, "images/empty")).Length == 0, "Empty file extraction failed.");

            var tampered = isoBytes.ToArray(); tampered[^1] ^= 1;
            Reject(() => { using var input = new ForwardOnly(new MemoryStream(tampered)); UsbUpdate.StreamIso(input, iso, dest, "XUR_TEST", saved); }, "Tampered ISO accepted.");
            Check(UsbUpdate.SameConfig(UsbUpdate.Configurations(dest), saved), "Configuration lost after a checksum failure.");
            Reject(() => { using var input = new ForwardOnly(new MemoryStream(isoBytes[..^4096])); UsbUpdate.StreamIso(input, iso, dest, "XUR_TEST", saved); }, "Truncated ISO accepted.");
            Check(UsbUpdate.SameConfig(UsbUpdate.Configurations(dest), saved), "Configuration lost after truncation.");

            // Signed release parts are concatenated and validated without creating an assembled ISO.
            var split = isoBytes.Length / 2;
            var chunks = new[] { isoBytes[..split], isoBytes[split..] };
            var parts = chunks.Select((bytes, i) => new MediaPart("fixture.iso.part" + (i + 1).ToString("D3"), bytes.Length, Digest(bytes))).ToArray();
            var assets = parts.ToDictionary(p => p.File, p => JsonSerializer.SerializeToElement(new { browser_download_url = $"https://github.com/{UsbUpdate.Repo}/releases/download/fixture/{p.File}" }));
            var release = new Release("fixture", assets, null);
            using var client = new HttpClient(new FixtureHandler(parts.Zip(chunks).ToDictionary(p => p.First.File, p => p.Second)));
            using (var input = new ReleaseStream(client, release, parts)) UsbUpdate.StreamIso(input, iso, dest, "XUR_TEST", saved);
            var invalid = parts.ToArray(); invalid[1] = invalid[1] with { Sha256 = new string('0', 64) };
            Reject(() => { using var input = new ReleaseStream(client, release, invalid); UsbUpdate.StreamIso(input, iso, dest, "XUR_TEST", saved); }, "Tampered split part accepted.");
            Check(UsbUpdate.SameConfig(UsbUpdate.Configurations(dest), saved), "Configuration lost after part hash failure.");
        }
        finally { UsbUpdate.Command("losetup", "--detach", loop); }

        var folder = Path.Combine(evidence, "safe-root"); Directory.CreateDirectory(folder);
        foreach (var unsafeName in new[] { "../escape", "./../escape", "/etc/passwd", "back\\slash" })
            Reject(() => UsbUpdate.SafePath(folder, unsafeName), "Path traversal accepted.");
        Directory.CreateSymbolicLink(Path.Combine(folder, "link"), evidence);
        Reject(() => UsbUpdate.SafePath(folder, "link/escape"), "Destination symlink accepted.");
        Check(Path.GetFileName(UsbUpdate.SafePath(folder, "./xur-diagnostics.yml")) == "xur-diagnostics.yml", "Path normalization failed.");

        using var releases = JsonDocument.Parse("""
            [{"tag_name":"new-app","draft":false,"published_at":"2026-10-04","assets":[{"name":"xur-update.tar.gz"}]},
             {"tag_name":"old-iso","draft":false,"published_at":"2026-10-03","assets":[{"name":"xur-nightly-x86_64.iso"}]},
             {"tag_name":"draft-iso","draft":true,"published_at":"2026-10-05","assets":[{"name":"xur-nightly-x86_64.iso"}]}]
            """);
        Check(UsbUpdate.ChooseRelease(releases.RootElement.EnumerateArray())?.Tag == "old-iso", "Newer app-only/draft release selected.");

        using var disks = JsonDocument.Parse("""
            {"blockdevices":[
              {"path":"/dev/nvme0n1","type":"disk","tran":"nvme","rm":false,"ro":false},
              {"path":"/dev/sdb","type":"disk","tran":"usb","rm":false,"ro":false},
              {"path":"/dev/sdc","type":"disk","tran":"usb","rm":true,"ro":false,"size":10000,"serial":"fixture","mountpoints":[],"children":[
                {"path":"/dev/sdc1","type":"part","fstype":"vfat","ro":false,"label":"XUR_TEST","uuid":"fixture","maj:min":"8:33","size":9000,"mountpoints":[]}]},
              {"path":"/dev/sdd","type":"disk","tran":"usb","rm":true,"ro":false,"size":10000,"serial":"system","mountpoints":[],"children":[
                {"path":"/dev/sdd1","type":"part","fstype":"vfat","ro":false,"label":"XUR_TEST","uuid":"system","maj:min":"8:49","size":9000,"mountpoints":["/"]}]}]}
            """);
        Check(UsbUpdate.Partitions(disks.RootElement).Select(p => p.Path).SequenceEqual(new[] { "/dev/sdc1" }), "SSD or system disk considered eligible.");
        Console.WriteLine("Passed: CLI binding/help/errors, streaming FAT32 extraction, byte-identical configuration, custom label, preserved files/logs, full/part checksums, truncation, traversal/symlinks, release selection and SSD/system protection.");
        Console.WriteLine("Physical USB writing: not run.");
        return 0;
    }
}

internal sealed class FixtureHandler(Dictionary<string, byte[]> parts) : HttpMessageHandler
{
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(parts[Path.GetFileName(request.RequestUri!.AbsolutePath)]) };
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Send(request, cancellationToken));
}

internal sealed class ForwardOnly(Stream source) : Stream
{
    public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, Math.Min(count, 8193));
    public override bool CanRead => true;
    public override bool CanWrite => false;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
}
