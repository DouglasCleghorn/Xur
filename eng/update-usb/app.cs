#:package TeeForge@0.1.0
#:package LibArchive.Net@0.3.1
#:package System.CommandLine@2.0.12
#:property PublishAot=false
#:property RestorePackagesWithLockFile=false

// MIT licensed. See the repository LICENSE. Requires Linux and .NET 10.
using System.Diagnostics;
using System.CommandLine;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LibArchive.Net;
using TeeForge.Broadcasting;
using TeeForge.Hashing;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await UsbUpdate.CreateCommand(UsbUpdate.Run).Parse(args).InvokeAsync(
                new() { EnableDefaultExceptionHandler = false });
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("USB update failed: " + error.Message);
            Console.Error.WriteLine("If streaming had started, rerun before booting the USB. Its configuration is preserved.");
            return 1;
        }
    }
}

internal static class UsbUpdate
{
    internal const string Repo = "DouglasCleghorn/Xur";
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };
    internal static readonly string[] Grub = ["EFI/BOOT/grub.cfg", "boot/grub2/grub.cfg"];
    internal static readonly HashSet<string> ConfigNames = new(StringComparer.OrdinalIgnoreCase)
        { "xur-diagnostics.yml", "xur-diagnostics.yaml", "xur.yml", "xur.yaml" };

    internal static string Command(string name, params string[] args)
    {
        var start = new ProcessStartInfo(name) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start " + name);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException(name + ": " + stderr.GetAwaiter().GetResult().Trim());
        return stdout.GetAwaiter().GetResult().Trim();
    }

    internal static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Xur-USB-updater");
        return client;
    }

    internal static Release? ChooseRelease(IEnumerable<GitHubRelease> releases)
    {
        foreach (var release in releases.OrderByDescending(r => r.PublishedAt))
        {
            if (release.Draft || release.PublishedAt == null) continue;
            var assets = release.Assets.ToDictionary(a => a.Name);
            var isos = assets.Keys.Where(n => Regex.IsMatch(n, @"^xur-.*x86_64\.iso$", RegexOptions.CultureInvariant)).ToArray();
            var split = assets.Keys.Any(n => Regex.IsMatch(n, @"^xur-installer-x86_64\.iso\.part\d+$"));
            if (isos.Length == 0 && !split) continue; // Newer app-only releases do not replace installer media.
            if (isos.Length > 1) throw new IOException("Latest installer release contains multiple x86_64 ISOs.");
            return new(release.TagName, assets, isos.SingleOrDefault());
        }
        return null;
    }

    internal static async Task<Release> Latest(HttpClient client)
    {
        var releases = new List<GitHubRelease>();
        for (var page = 1; ; page++)
        {
            using var response = await client.GetAsync($"https://api.github.com/repos/{Repo}/releases?per_page=100&page={page}");
            response.EnsureSuccessStatusCode();
            using var json = await response.Content.ReadAsStreamAsync();
            var batch = ReadJson<GitHubRelease[]>(json);
            releases.AddRange(batch);
            if (batch.Length < 100) break;
        }
        return ChooseRelease(releases) ?? throw new IOException("No published installer ISO found; app update archives are not bootable media.");
    }

    internal static string AssetUrl(ReleaseAsset asset)
    {
        var url = asset.BrowserDownloadUrl;
        if (!url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.Ordinal))
            throw new IOException("Unexpected GitHub asset URL.");
        return url;
    }

    internal static T ReadJson<T>(Stream json) where T : class =>
        JsonSerializer.Deserialize<T>(json, Json) ?? throw new IOException("Missing release metadata.");

    internal static async Task<(MediaPart Iso, MediaPart[] Parts)> Media(HttpClient client, Release release, string workspace)
    {
        if (release.Assets.ContainsKey("installer.json") || release.Assets.ContainsKey("installer.json.sig"))
        {
            foreach (var name in new[] { "installer.json", "installer.json.sig" })
            {
                var asset = release.Assets[name];
                var size = asset.Size;
                if (size is <= 0 or > 1048576) throw new IOException("Invalid descriptor/signature size.");
                using var response = await client.GetAsync(AssetUrl(asset), HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                using var input = await response.Content.ReadAsStreamAsync();
                var data = new byte[size];
                await input.ReadExactlyAsync(data);
                if (input.ReadByte() != -1) throw new IOException("Descriptor exceeds its advertised size.");
                File.WriteAllBytes(Path.Combine(workspace, name), data);
            }
            Command("openssl", "pkeyutl", "-verify", "-pubin", "-inkey", FindTrustKey(), "-rawin", "-in",
                Path.Combine(workspace, "installer.json"), "-sigfile", Path.Combine(workspace, "installer.json.sig"));
            using var json = File.OpenRead(Path.Combine(workspace, "installer.json"));
            var descriptor = ReadJson<InstallerDescriptor>(json);
            if (descriptor.Schema != 1) throw new IOException("Unsupported installer descriptor.");
            var iso = descriptor.Iso.Validated();
            if (iso.File != "xur-installer-x86_64.iso") throw new IOException("Unexpected signed ISO filename.");
            var parts = descriptor.Parts.Select(p => p.Validated()).ToArray();
            var whole = parts.Length == 1 && parts[0].File == iso.File;
            if (parts.Length == 0 || parts.Sum(p => p.Bytes) != iso.Bytes) throw new IOException("Invalid installer parts.");
            for (var i = 0; i < parts.Length; i++)
                if ((!whole && parts[i].File != iso.File + ".part" + (i + 1).ToString("D3")) ||
                    release.Assets[parts[i].File].Size != parts[i].Bytes)
                    throw new IOException("Missing, changed or out-of-order installer part.");
            return (iso, parts);
        }
        if (release.IsoName == null) throw new IOException("Split media requires a signed installer.json descriptor.");
        var direct = release.Assets[release.IsoName];
        var digest = direct.Digest;
        if (digest == null || !Regex.IsMatch(digest, "^sha256:[a-f0-9]{64}$"))
            throw new IOException("Published ISO has no GitHub SHA-256 digest or signed descriptor.");
        var single = new MediaPart(release.IsoName, direct.Size, digest[7..]).Validated();
        return (single, [single]);
    }

    private static string FindTrustKey([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../os/bootc/application-update-key.pem"));

    internal static List<UsbPartition> Partitions(JsonElement tree)
    {
        var result = new List<UsbPartition>();
        foreach (var disk in tree.GetProperty("blockdevices").EnumerateArray())
        {
            string? Text(JsonElement node, string key) => node.GetProperty(key).GetString();
            if (Text(disk, "type") != "disk" || Text(disk, "tran") != "usb" || !disk.GetProperty("rm").GetBoolean() || disk.GetProperty("ro").GetBoolean()) continue;
            var nodes = new List<JsonElement> { disk };
            for (var i = 0; i < nodes.Count; i++)
                if (nodes[i].TryGetProperty("children", out var children)) nodes.AddRange(children.EnumerateArray());
            string[] Mounts(JsonElement node) => node.GetProperty("mountpoints").EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).ToArray();
            if (nodes.SelectMany(Mounts).Any(p => p is "/" or "/home" or "/usr" or "/var" or "/etc" or "/boot" || p.StartsWith("/boot/"))) continue;
            foreach (var part in nodes.Skip(1))
            {
                if (Text(part, "type") != "part" || Text(part, "fstype") != "vfat" || part.GetProperty("ro").GetBoolean()) continue;
                var id = string.Join('|', new[] { Text(part, "path"), Text(part, "maj:min"), Text(part, "uuid"), Text(part, "label"),
                    part.GetProperty("size").ToString(), Text(disk, "path"), Text(disk, "serial"), disk.GetProperty("size").ToString() });
                result.Add(new(Text(part, "path")!, Text(part, "label") ?? "", id, Mounts(part)));
            }
        }
        return result;
    }

    internal static List<UsbPartition> Discover()
    {
        // --tree is required when NAME is not selected, otherwise partitions are flat.
        using var devices = JsonDocument.Parse(Command("lsblk", "--json", "--tree", "--bytes", "--paths", "-o",
            "PATH,TYPE,TRAN,RM,RO,FSTYPE,LABEL,UUID,SIZE,SERIAL,MAJ:MIN,MOUNTPOINTS"));
        return Partitions(devices.RootElement);
    }

    internal static Dictionary<string, byte[]> Configurations(string root)
    {
        var saved = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFileSystemEntries(root).Where(p => ConfigNames.Contains(Path.GetFileName(p))))
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget != null || info.Length > 32768) throw new IOException("Configuration must be a regular file of at most 32 KiB.");
            saved.Add(info.Name, File.ReadAllBytes(path));
        }
        if (saved.Keys.Count(n => n.StartsWith("xur-diagnostics.", StringComparison.OrdinalIgnoreCase)) != 1)
            throw new IOException("USB must contain exactly one xur-diagnostics.yml or .yaml.");
        return saved;
    }

    internal static bool SameConfig(Dictionary<string, byte[]> left, Dictionary<string, byte[]> right) =>
        left.Count == right.Count && left.All(p => right.TryGetValue(p.Key, out var data) && p.Value.AsSpan().SequenceEqual(data));

    internal static bool IsInstaller(string root) => File.Exists(Path.Combine(root, "EFI/BOOT/BOOTX64.EFI")) &&
        File.Exists(Path.Combine(root, "LiveOS/squashfs.img")) && Grub.All(n => File.Exists(Path.Combine(root, n)) &&
        File.ReadAllText(Path.Combine(root, n)).Contains("xur.installer=1", StringComparison.Ordinal));

    internal static (UsbPartition Usb, Dictionary<string, byte[]> Saved) SelectUsb(string workspace, string? device)
    {
        var candidates = Discover().Where(p => device == null || Path.GetFullPath(p.Path) == Path.GetFullPath(device)).ToArray();
        var found = new List<(UsbPartition, Dictionary<string, byte[]>)>();
        foreach (var part in candidates)
        {
            using var mount = new Mount(part.Path, Path.Combine(workspace, "probe-" + found.Count), false);
            if (IsInstaller(mount.Path)) found.Add((part, Configurations(mount.Path)));
        }
        if (found.Count != 1) throw new IOException($"Found {found.Count} eligible Xur USBs. Use --device /dev/sdX1 to select an existing removable USB FAT32 partition.");
        if (!Regex.IsMatch(found[0].Item1.Label, "^[A-Za-z0-9_]{1,11}$")) throw new IOException("USB label must contain 1–11 letters, digits or underscores.");
        return found[0];
    }

    internal static string SafePath(string root, string name)
    {
        if (name.StartsWith('/') || name.Contains('\\') || name.Split('/').Any(p => p == "..")) throw new IOException("Unsafe ISO path.");
        var target = Path.GetFullPath(Path.Combine(root, name));
        if (target != root && !target.StartsWith(root + "/", StringComparison.Ordinal)) throw new IOException("ISO path escapes the USB.");
        for (var current = target; current != root; current = Path.GetDirectoryName(current)!)
            if (new FileInfo(current).LinkTarget != null) throw new IOException("USB contains a symlink: " + name);
        return target;
    }

    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    internal static void StreamIso(Stream source, MediaPart iso, string destination, string usbLabel, Dictionary<string, byte[]> saved)
    {
        using var broadcast = new BroadcastHashStream(HashAlgorithmName.SHA256, out var results, source, readerCount: 2,
            new BroadcastStreamOptions(bufferSize: 65536, pauseWriterThreshold: 131072, resumeWriterThreshold: 65536, leaveOpen: true));
        var prefix = new byte[65536];
        using (var probe = broadcast.Readers[0]) probe.ReadExactly(prefix);
        var input = broadcast.Readers[1];
        if (prefix[32768] != 1 || Encoding.ASCII.GetString(prefix, 32769, 5) != "CD001") throw new IOException("Download is not an ISO9660 image.");
        var label = Encoding.ASCII.GetString(prefix, 32808, 32).TrimEnd(' ');
        if (!Regex.IsMatch(label, "^[A-Za-z0-9_]{1,32}$")) throw new IOException("Unsupported ISO label.");
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var reader = new LibArchiveReader(input, blockSize: 65536);
            foreach (var entry in IsoEntry.Read(reader))
            {
                var target = SafePath(destination, entry.Name);
                var name = Path.GetRelativePath(destination, target);
                if (target == destination || ConfigNames.Contains(name)) continue;
                if (entry.IsDirectory) { Directory.CreateDirectory(target); continue; }
                if (!entry.IsRegularFile) throw new IOException("Unsupported ISO entry: " + name);
                if (entry.Hardlink is { } link)
                {
                    var original = Path.GetRelativePath(destination, SafePath(destination, link));
                    if (!hashes.TryGetValue(original, out var expected)) throw new IOException("Unresolved ISO hardlink: " + name);
                    if (!hashes.TryAdd(name, expected)) throw new IOException("Duplicate ISO filename: " + name);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(SafePath(destination, original), target, true);
                    continue;
                }
                var size = entry.LengthBytes ?? -1;
                if (size < 0 || size > uint.MaxValue) throw new IOException("Unsupported ISO entry: " + name);
                if (hashes.ContainsKey(name)) throw new IOException("Duplicate ISO filename: " + name);
                if (new DriveInfo(destination).AvailableFreeSpace + (File.Exists(target) ? new FileInfo(target).Length : 0) < size + 1048576)
                    throw new IOException("USB has insufficient free space.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string hash;
                if (Grub.Contains(name))
                {
                    if (size > 1048576) throw new IOException("Oversized boot configuration.");
                    using var data = new MemoryStream();
                    CopyEntry(entry, data, size, broadcast, iso.Bytes);
                    var content = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(data.ToArray()).Replace(label, usbLabel, StringComparison.Ordinal));
                    File.WriteAllBytes(target, content);
                    hash = Convert.ToHexStringLower(SHA256.HashData(content));
                }
                else
                {
                    using var file = File.Create(target);
                    var tee = new TeeHashStream(HashAlgorithmName.SHA256, out var fileHash, file);
                    using (tee) CopyEntry(entry, tee, size, broadcast, iso.Bytes);
                    hash = fileHash[HashAlgorithmName.SHA256].Hex.ToLowerInvariant();
                }
                hashes.Add(name, hash);
            }
            input.CopyTo(Stream.Null, 65536); // ISO padding must also reach EOF for whole-image verification.
            broadcast.Completion.GetAwaiter().GetResult();
            if (broadcast.BytesBroadcast != iso.Bytes || results[HashAlgorithmName.SHA256].Hex != iso.Sha256.ToUpperInvariant())
                throw new IOException("Full ISO length/SHA-256 mismatch.");
            Command("sync", "-f", destination);
            foreach (var (name, expected) in hashes)
                if (HashFile(SafePath(destination, name)) != expected) throw new IOException("USB readback verification failed: " + name);
            if (!IsInstaller(destination) || !Grub.All(hashes.ContainsKey) || !hashes.ContainsKey("LiveOS/squashfs.img") || !hashes.ContainsKey("EFI/BOOT/BOOTX64.EFI"))
                throw new IOException("Streamed ISO is missing required Xur installer files.");
        }
        catch (ApplicationException error) { throw new IOException("ISO reader: " + error.Message, error); }
        finally
        {
            foreach (var (name, content) in saved)
            {
                var path = SafePath(destination, name);
                if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(content)) File.WriteAllBytes(path, content);
            }
            Command("sync", "-f", destination); // Flush only this filesystem, never the host SSD.
        }
        if (!SameConfig(Configurations(destination), saved)) throw new IOException("USB configuration changed.");
    }

    private static void CopyEntry(IsoEntry entry, Stream destination, long expected, BroadcastHashStream broadcast, long isoBytes)
    {
        using var input = entry.Stream;
        var buffer = new byte[65536];
        long written = 0, nextProgress = broadcast.BytesBroadcast + 256 * 1024 * 1024;
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            written += count;
            if (written > expected) throw new IOException("ISO entry exceeds its advertised length.");
            destination.Write(buffer, 0, count);
            if (broadcast.BytesBroadcast >= nextProgress)
            {
                Console.WriteLine($"Streamed {broadcast.BytesBroadcast / 1073741824.0:F2} GiB ({100.0 * broadcast.BytesBroadcast / isoBytes:F0}%)");
                nextProgress += 256 * 1024 * 1024;
            }
        }
        if (written != expected) throw new IOException("Truncated ISO file.");
    }

    internal static RootCommand CreateCommand(Func<string?, bool, Task> action)
    {
        var device = new Option<string?>("--device")
        {
            Description = "Existing removable USB FAT32 partition, such as /dev/sdX1; otherwise auto-detect",
            Arity = ArgumentArity.ExactlyOne
        };
        device.Validators.Add(result =>
        {
            var path = result.GetValueOrDefault<string?>();
            if (path != null && (!Path.IsPathFullyQualified(path) || !path.StartsWith("/dev/", StringComparison.Ordinal)))
                result.AddError("Specify an absolute USB partition path under /dev/.");
        });
        var check = new Option<bool>("--check")
        {
            Description = "Check USB and latest release without downloading the ISO or changing USB files"
        };
        var root = new RootCommand("Stream the latest GitHub installer to an existing FAT32 Xur USB; preserve diagnostics.");
        root.Options.Add(device);
        root.Options.Add(check);
        root.SetAction(async (result, _) => await action(result.GetValue(device), result.GetValue(check)));
        return root;
    }

    internal static async Task Run(string? device, bool check)
    {
        if (!OperatingSystem.IsLinux() || Native.geteuid() != 0) throw new IOException("Run on Linux as root: sudo ./eng/update-usb.sh");
        using var workspace = new Workspace();
        var (usb, saved) = SelectUsb(workspace.Path, device);
        using var client = Client();
        var release = await Latest(client);
        var (iso, parts) = await Media(client, release, workspace.Path);
        Console.WriteLine($"USB: {usb.Path} ({usb.Label}); preserve: {string.Join(", ", saved.Keys)}");
        Console.WriteLine($"Latest installer: {release.Tag} ({iso.Bytes / 1073741824.0:F2} GiB)");
        if (check) { Console.WriteLine("Check passed. No ISO downloaded and no USB files changed."); return; }
        var current = Discover().SingleOrDefault(p => p.Identity == usb.Identity) ?? throw new IOException("USB identity changed; refusing to write.");
        foreach (var mountpoint in current.Mountpoints.OrderByDescending(p => p.Length)) Command("umount", mountpoint);
        using (var mount = new Mount(usb.Path, System.IO.Path.Combine(workspace.Path, "usb"), true))
        {
            if (!IsInstaller(mount.Path) || !SameConfig(Configurations(mount.Path), saved)) throw new IOException("USB installer/configuration changed; refusing to write.");
            Console.WriteLine("Streaming directly to USB; no ISO file or SSD staging. Keep the drive connected until verification finishes.");
            using var stream = new ReleaseStream(client, release, parts);
            StreamIso(stream, iso, mount.Path, usb.Label, saved);
        }
        Console.WriteLine("USB updated, verified and unmounted. Diagnostic configuration preserved byte-for-byte.");
    }
}

internal sealed record GitHubRelease(string TagName, bool Draft, DateTimeOffset? PublishedAt, ReleaseAsset[] Assets);
internal sealed record ReleaseAsset(string Name, long Size, string BrowserDownloadUrl, string? Digest = null);
internal sealed record Release(string Tag, Dictionary<string, ReleaseAsset> Assets, string? IsoName);
internal sealed record InstallerDescriptor(int Schema, MediaPart Iso, MediaPart[] Parts);
internal sealed record MediaPart(string File, long Bytes, string Sha256)
{
    internal MediaPart Validated() => Bytes > 0 && Regex.IsMatch(Sha256, "^[a-f0-9]{64}$")
        ? this : throw new IOException("Invalid installer length or SHA-256.");
}
internal sealed record UsbPartition(string Path, string Label, string Identity, string[] Mountpoints);

internal sealed class Mount : IDisposable
{
    internal string Path { get; }
    internal Mount(string device, string path, bool writable)
    {
        Path = path;
        Directory.CreateDirectory(path);
        UsbUpdate.Command("mount", "-o", (writable ? "rw" : "ro") + ",nosuid,nodev,noexec", device, path);
    }
    public void Dispose() => UsbUpdate.Command("umount", Path);
}

internal sealed class Workspace : IDisposable
{
    internal string Path { get; }
    internal Workspace([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux is required.");
        var directory = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source)!, "../../.build/usb-update"));
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    public void Dispose()
    {
        // Never recurse into a USB mount if its unmount failed.
        if (Directory.EnumerateDirectories(Path).Any(p => Native.IsMountPoint(p))) return;
        Directory.Delete(Path, true);
    }
}

internal sealed class ReleaseStream(HttpClient client, Release release, MediaPart[] parts) : Stream
{
    private int index;
    private long count;
    private HttpResponseMessage? response;
    private Stream? body;
    private TeeHashStream? hash;
    private TeeHashResults? results;
    public override int Read(byte[] buffer, int offset, int length)
    {
        while (index < parts.Length)
        {
            if (body == null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, UsbUpdate.AssetUrl(release.Assets[parts[index].File]));
                response = client.Send(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                body = response.Content.ReadAsStream();
                hash = new TeeHashStream(HashAlgorithmName.SHA256, out results, Stream.Null);
                count = 0;
            }
            var read = body.Read(buffer, offset, length);
            count += read;
            if (count > parts[index].Bytes) throw new IOException("ISO part exceeds its advertised length.");
            if (read > 0) { hash!.Write(buffer, offset, read); return read; }
            hash!.Dispose(); hash = null;
            if (count != parts[index].Bytes || results![HashAlgorithmName.SHA256].Hex != parts[index].Sha256.ToUpperInvariant()) throw new IOException("ISO part length/SHA-256 mismatch.");
            body.Dispose(); body = null; response!.Dispose(); response = null; index++;
        }
        return 0;
    }
    protected override void Dispose(bool disposing) { if (disposing) { hash?.Dispose(); body?.Dispose(); response?.Dispose(); } base.Dispose(disposing); }
    public override bool CanRead => true;
    public override bool CanWrite => false;
    public override bool CanSeek => false;
    public override long Length => parts.Sum(p => p.Bytes);
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

// LibArchive.Net 0.3.1 exposes entry streams but omits hardlink targets. Use its
// public SafeHandle and protected Entry constructor to add only that metadata.
internal sealed class IsoEntry(IntPtr entry, IntPtr archive) : LibArchiveReader.Entry(entry, archive)
{
    internal string? Hardlink => Marshal.PtrToStringUTF8(Native.archive_entry_hardlink(entryHandle));
    internal static IEnumerable<IsoEntry> Read(LibArchiveReader reader)
    {
        var archive = reader.DangerousGetHandle();
        int status;
        while ((status = Native.archive_read_next_header(archive, out var entry)) == 0)
            yield return new(entry, archive);
        if (status != 1) throw new IOException("ISO reader: " + Marshal.PtrToStringUTF8(Native.archive_error_string(archive)));
        GC.KeepAlive(reader);
    }
}

internal static class Native
{
    internal static bool IsMountPoint(string path) => File.ReadLines("/proc/self/mountinfo")
        .Any(line => line.Split(' ')[4] == path.Replace("\\", "\\134").Replace(" ", "\\040").Replace("\t", "\\011").Replace("\n", "\\012"));
    [DllImport("libc")] internal static extern uint geteuid();
    [DllImport("archive")] internal static extern int archive_read_next_header(IntPtr archive, out IntPtr entry);
    [DllImport("archive")] internal static extern IntPtr archive_error_string(IntPtr archive);
    [DllImport("archive")] internal static extern IntPtr archive_entry_hardlink(IntPtr entry);
}
