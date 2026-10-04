#:package TeeForge@0.1.0
#:package System.CommandLine@2.0.12
#:property PublishAot=false
#:property RestorePackagesWithLockFile=false

// MIT licensed. See the repository LICENSE. Requires Linux, .NET 10 and libarchive.
using System.Diagnostics;
using System.CommandLine;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    internal static Release? ChooseRelease(IEnumerable<JsonElement> releases)
    {
        foreach (var release in releases.OrderByDescending(r => r.GetProperty("published_at").GetString()))
        {
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("published_at").ValueKind == JsonValueKind.Null) continue;
            var assets = release.GetProperty("assets").EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!);
            var isos = assets.Keys.Where(n => Regex.IsMatch(n, @"^xur-.*x86_64\.iso$", RegexOptions.CultureInvariant)).ToArray();
            var split = assets.Keys.Any(n => Regex.IsMatch(n, @"^xur-installer-x86_64\.iso\.part\d+$"));
            if (isos.Length == 0 && !split) continue; // Newer app-only releases do not replace installer media.
            if (isos.Length > 1) throw new IOException("Latest installer release contains multiple x86_64 ISOs.");
            return new(release.GetProperty("tag_name").GetString()!, assets, isos.SingleOrDefault());
        }
        return null;
    }

    internal static async Task<Release> Latest(HttpClient client)
    {
        var releases = new List<JsonElement>();
        for (var page = 1; ; page++)
        {
            using var response = await client.GetAsync($"https://api.github.com/repos/{Repo}/releases?per_page=100&page={page}");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
            var batch = document.RootElement.EnumerateArray().Select(r => r.Clone()).ToArray();
            releases.AddRange(batch);
            if (batch.Length < 100) break;
        }
        return ChooseRelease(releases) ?? throw new IOException("No published installer ISO found; app update archives are not bootable media.");
    }

    internal static string AssetUrl(JsonElement asset)
    {
        var url = asset.GetProperty("browser_download_url").GetString()!;
        if (!url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.Ordinal))
            throw new IOException("Unexpected GitHub asset URL.");
        return url;
    }

    internal static MediaPart Record(JsonElement record)
    {
        var part = new MediaPart(record.GetProperty("file").GetString()!, record.GetProperty("bytes").GetInt64(), record.GetProperty("sha256").GetString()!);
        if (part.Bytes <= 0 || !Regex.IsMatch(part.Sha256, "^[a-f0-9]{64}$")) throw new IOException("Invalid installer length or SHA-256.");
        return part;
    }

    internal static async Task<(MediaPart Iso, MediaPart[] Parts)> Media(HttpClient client, Release release, string workspace)
    {
        if (release.Assets.ContainsKey("installer.json") || release.Assets.ContainsKey("installer.json.sig"))
        {
            foreach (var name in new[] { "installer.json", "installer.json.sig" })
            {
                var asset = release.Assets[name];
                var size = asset.GetProperty("size").GetInt64();
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
            using var descriptor = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(workspace, "installer.json")));
            var root = descriptor.RootElement;
            if (root.GetProperty("schema").GetInt32() != 1) throw new IOException("Unsupported installer descriptor.");
            var iso = Record(root.GetProperty("iso"));
            if (iso.File != "xur-installer-x86_64.iso") throw new IOException("Unexpected signed ISO filename.");
            var parts = root.GetProperty("parts").EnumerateArray().Select(Record).ToArray();
            var whole = parts.Length == 1 && parts[0].File == iso.File;
            if (parts.Length == 0 || parts.Sum(p => p.Bytes) != iso.Bytes) throw new IOException("Invalid installer parts.");
            for (var i = 0; i < parts.Length; i++)
                if ((!whole && parts[i].File != iso.File + ".part" + (i + 1).ToString("D3")) ||
                    release.Assets[parts[i].File].GetProperty("size").GetInt64() != parts[i].Bytes)
                    throw new IOException("Missing, changed or out-of-order installer part.");
            return (iso, parts);
        }
        if (release.IsoName == null) throw new IOException("Split media requires a signed installer.json descriptor.");
        var direct = release.Assets[release.IsoName];
        var digest = direct.TryGetProperty("digest", out var value) ? value.GetString() : null;
        if (digest == null || !Regex.IsMatch(digest, "^sha256:[a-f0-9]{64}$"))
            throw new IOException("Published ISO has no GitHub SHA-256 digest or signed descriptor.");
        var single = new MediaPart(release.IsoName, direct.GetProperty("size").GetInt64(), digest[7..]);
        if (single.Bytes <= 0) throw new IOException("Invalid ISO size.");
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
        var prefix = new byte[65536];
        source.ReadExactly(prefix);
        if (prefix[32768] != 1 || Encoding.ASCII.GetString(prefix, 32769, 5) != "CD001") throw new IOException("Download is not an ISO9660 image.");
        var label = Encoding.ASCII.GetString(prefix, 32808, 32).TrimEnd(' ');
        if (!Regex.IsMatch(label, "^[A-Za-z0-9_]{1,32}$")) throw new IOException("Unsupported ISO label.");
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new IsoReader(source, prefix, iso);
        try
        {
            while (reader.Next(out var entry))
            {
                var rawName = Marshal.PtrToStringUTF8(Native.archive_entry_pathname(entry))!;
                var target = SafePath(destination, rawName);
                var name = Path.GetRelativePath(destination, target);
                if (target == destination || ConfigNames.Contains(name)) continue;
                if (Native.archive_entry_filetype(entry) == 0x4000) { Directory.CreateDirectory(target); continue; }
                if (Native.archive_entry_symlink(entry) != IntPtr.Zero) throw new IOException("ISO symlinks are unsupported: " + name);
                var link = Native.archive_entry_hardlink(entry);
                if (link != IntPtr.Zero)
                {
                    var original = Path.GetRelativePath(destination, SafePath(destination, Marshal.PtrToStringUTF8(link)!));
                    if (!hashes.TryGetValue(original, out var expected)) throw new IOException("Unresolved ISO hardlink: " + name);
                    if (!hashes.TryAdd(name, expected)) throw new IOException("Duplicate ISO filename: " + name);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(SafePath(destination, original), target, true);
                    continue;
                }
                var size = Native.archive_entry_size(entry);
                if (Native.archive_entry_filetype(entry) != 0x8000 || size < 0 || size > uint.MaxValue) throw new IOException("Unsupported ISO entry: " + name);
                if (hashes.ContainsKey(name)) throw new IOException("Duplicate ISO filename: " + name);
                if (new DriveInfo(destination).AvailableFreeSpace + (File.Exists(target) ? new FileInfo(target).Length : 0) < size + 1048576)
                    throw new IOException("USB has insufficient free space.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string hash;
                if (Grub.Contains(name))
                {
                    if (size > 1048576) throw new IOException("Oversized boot configuration.");
                    using var data = new MemoryStream();
                    reader.CopyEntry(data, size);
                    var content = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(data.ToArray()).Replace(label, usbLabel, StringComparison.Ordinal));
                    File.WriteAllBytes(target, content);
                    hash = Convert.ToHexStringLower(SHA256.HashData(content));
                }
                else
                {
                    using var file = File.Create(target);
                    var tee = new TeeHashStream(HashAlgorithmName.SHA256, out var results, file);
                    using (tee) reader.CopyEntry(tee, size);
                    hash = results[HashAlgorithmName.SHA256].Hex.ToLowerInvariant();
                }
                hashes.Add(name, hash);
            }
            reader.Complete(); // Drain ISO padding as well as extracted bytes before checking the full hash.
            Command("sync", "-f", destination);
            foreach (var (name, expected) in hashes)
                if (HashFile(SafePath(destination, name)) != expected) throw new IOException("USB readback verification failed: " + name);
            if (!IsInstaller(destination) || !Grub.All(hashes.ContainsKey) || !hashes.ContainsKey("LiveOS/squashfs.img") || !hashes.ContainsKey("EFI/BOOT/BOOTX64.EFI"))
                throw new IOException("Streamed ISO is missing required Xur installer files.");
        }
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
        if (!NativeLibrary.TryLoad("libarchive.so.13", out var library)) throw new IOException("Install libarchive (libarchive13 on Ubuntu; libarchive on Fedora).");
        NativeLibrary.Free(library);
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

internal sealed record Release(string Tag, Dictionary<string, JsonElement> Assets, string? IsoName);
internal sealed record MediaPart(string File, long Bytes, string Sha256);
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

internal sealed class IsoReader : IDisposable
{
    private readonly Stream source;
    private readonly byte[] prefix;
    private readonly MediaPart iso;
    private readonly byte[] buffer = new byte[65536];
    private readonly IntPtr nativeBuffer = Marshal.AllocHGlobal(65536);
    private readonly IntPtr entryBuffer = Marshal.AllocHGlobal(65536);
    private readonly Native.ReadCallback callback;
    private readonly TeeHashStream hash;
    private readonly TeeHashResults results;
    private readonly IntPtr archive;
    private bool prefixRead, completed;
    private long count, nextProgress = 256 * 1024 * 1024;
    private Exception? failure;
    internal IsoReader(Stream source, byte[] prefix, MediaPart iso)
    {
        this.source = source; this.prefix = prefix; this.iso = iso;
        hash = new TeeHashStream(HashAlgorithmName.SHA256, out results, Stream.Null);
        callback = Read;
        archive = Native.archive_read_new();
        try { Check(Native.archive_read_support_format_iso9660(archive)); Check(Native.archive_read_open(archive, IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero)); }
        catch { Dispose(); throw; }
    }
    private nint Read(IntPtr archive, IntPtr data, out IntPtr block)
    {
        block = nativeBuffer;
        try
        {
            int read;
            if (!prefixRead) { prefix.CopyTo(buffer, 0); read = prefix.Length; prefixRead = true; }
            else read = source.Read(buffer, 0, buffer.Length);
            count += read;
            if (count > iso.Bytes) throw new IOException("ISO exceeds its advertised length.");
            hash.Write(buffer, 0, read);
            Marshal.Copy(buffer, 0, nativeBuffer, read);
            if (count >= nextProgress) { Console.WriteLine($"Streamed {count / 1073741824.0:F2} GiB ({100.0 * count / iso.Bytes:F0}%)"); nextProgress += 256 * 1024 * 1024; }
            return read;
        }
        catch (Exception error) { failure = error; return -1; }
    }
    private void Check(int status)
    {
        if (failure != null) throw new IOException("ISO stream failed: " + failure.Message, failure);
        if (status < 0) throw new IOException("ISO reader: " + Marshal.PtrToStringUTF8(Native.archive_error_string(archive)));
    }
    internal bool Next(out IntPtr entry) { var status = Native.archive_read_next_header(archive, out entry); Check(status); return status != 1; }
    internal void CopyEntry(Stream destination, long expected)
    {
        long written = 0;
        while (true)
        {
            var size = Native.archive_read_data(archive, entryBuffer, (nuint)buffer.Length);
            if (size < 0) { Check(-1); }
            if (size == 0) break;
            written += (long)size;
            if (written > expected) throw new IOException("ISO entry exceeds its advertised length.");
            Marshal.Copy(entryBuffer, buffer, 0, (int)size);
            destination.Write(buffer, 0, (int)size);
        }
        if (written != expected) throw new IOException("Truncated ISO file.");
    }
    internal void Complete()
    {
        // libarchive can reach its last file before the ISO's trailing padding.
        while (Read(archive, IntPtr.Zero, out _) > 0) { }
        Check(0);
        hash.Dispose(); completed = true;
        if (count != iso.Bytes || results[HashAlgorithmName.SHA256].Hex != iso.Sha256.ToUpperInvariant()) throw new IOException("Full ISO length/SHA-256 mismatch.");
    }
    public void Dispose() { Native.archive_read_free(archive); Marshal.FreeHGlobal(nativeBuffer); Marshal.FreeHGlobal(entryBuffer); if (!completed) hash.Dispose(); GC.KeepAlive(callback); }
}

internal static class Native
{
    internal static bool IsMountPoint(string path) => File.ReadLines("/proc/self/mountinfo")
        .Any(line => line.Split(' ')[4] == path.Replace("\\", "\\134").Replace(" ", "\\040").Replace("\t", "\\011").Replace("\n", "\\012"));
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint ReadCallback(IntPtr archive, IntPtr data, out IntPtr buffer);
    [DllImport("libc")] internal static extern uint geteuid();
    [DllImport("libarchive.so.13")] internal static extern IntPtr archive_read_new();
    [DllImport("libarchive.so.13")] internal static extern int archive_read_support_format_iso9660(IntPtr archive);
    [DllImport("libarchive.so.13")] internal static extern int archive_read_open(IntPtr archive, IntPtr data, IntPtr open, ReadCallback read, IntPtr close);
    [DllImport("libarchive.so.13")] internal static extern int archive_read_next_header(IntPtr archive, out IntPtr entry);
    [DllImport("libarchive.so.13")] internal static extern nint archive_read_data(IntPtr archive, IntPtr buffer, nuint size);
    [DllImport("libarchive.so.13")] internal static extern int archive_read_free(IntPtr archive);
    [DllImport("libarchive.so.13")] internal static extern IntPtr archive_error_string(IntPtr archive);
    [DllImport("libarchive.so.13")] internal static extern IntPtr archive_entry_pathname(IntPtr entry);
    [DllImport("libarchive.so.13")] internal static extern uint archive_entry_filetype(IntPtr entry);
    [DllImport("libarchive.so.13")] internal static extern long archive_entry_size(IntPtr entry);
    [DllImport("libarchive.so.13")] internal static extern IntPtr archive_entry_symlink(IntPtr entry);
    [DllImport("libarchive.so.13")] internal static extern IntPtr archive_entry_hardlink(IntPtr entry);
}
