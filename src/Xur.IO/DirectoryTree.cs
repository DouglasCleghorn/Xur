using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Xur.IO;

/// <summary>Descriptor-relative Linux access with kernel-enforced symlink and mount boundaries.</summary>
[SupportedOSPlatform("linux")]
public sealed partial class DirectoryTree : IDisposable
{
    const int DirectoryFlag = 0x10000, NoFollow = 0x20000, CloseOnExec = 0x80000, NonBlock = 0x800;
    const int AtNoFollow = 0x100, AtEmptyPath = 0x1000;
    const uint BasicStats = 0x7ff, MountId = 0x1000;
    public SafeFileHandle Root { get; }
    public Metadata Identity { get; }
    public DirectoryTree(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("Root must be an absolute folder path.");
        using var slash = OpenAbsolute("/", DirectoryFlag);
        Root = root == "/" ? Duplicate(slash) : Beneath(slash, string.Join('/', root.Split('/', StringSplitOptions.RemoveEmptyEntries)), true, crossMounts: true);
        try { Identity = Inspect(Root); }
        catch { Root.Dispose(); throw; }
    }
    public static string[] Parts(string path)
    {
        if (path.Length > 4096 || path.StartsWith('/') || path.Contains('\0')) throw new ArgumentException("Invalid path.");
        var parts = path.Length == 0 ? [] : path.Split('/');
        if (parts.Any(p => p is "" or "." or "..")) throw new ArgumentException("Invalid path.");
        // Do not replace unpaired surrogates with another filename during UTF-8 marshaling.
        _ = new UTF8Encoding(false, true).GetBytes(path);
        return parts;
    }
    public SafeFileHandle Open(string path, bool directory = false)
    {
        Parts(path);
        return path.Length == 0 ? Duplicate(Root) : Beneath(Root, path, directory);
    }
    public static SafeFileHandle Child(SafeFileHandle parent, string name, bool directory = false)
    {
        var parts = Parts(name);
        if (parts.Length != 1) throw new ArgumentException("Invalid child name.");
        return Beneath(parent, name, directory);
    }
    public (SafeFileHandle Folder, string Name) Parent(string path)
    {
        var parts = Parts(path);
        if (parts.Length == 0) throw new ArgumentException("The root folder cannot be changed.");
        return (Open(string.Join('/', parts[..^1]), true), parts[^1]);
    }
    public bool Within(Metadata metadata) => metadata.DeviceMajor == Identity.DeviceMajor && metadata.DeviceMinor == Identity.DeviceMinor && metadata.Mount == Identity.Mount;
    public static Metadata Inspect(SafeFileHandle parent, string name = "")
    {
        if (name.Length != 0) SingleName(name);
        Check(Statx(parent, name, name.Length == 0 ? AtEmptyPath : AtNoFollow, BasicStats | MountId, out var metadata), "Item unavailable or changed");
        if ((metadata.Mask & (BasicStats | MountId)) != (BasicStats | MountId)) throw new IOException("Required Linux file metadata is unavailable.");
        return metadata;
    }
    public static IEnumerable<string> Names(SafeFileHandle folder)
    {
        // Open a fresh directory description, so simultaneous/repeated scans
        // never share a directory cursor. procfs refers to our held descriptor.
        using var cursor = OpenAbsolute("/proc/self/fd/" + folder.DangerousGetHandle().ToInt32(), DirectoryFlag);
        var native = Fdopendir(cursor.DangerousGetHandle().ToInt32());
        if (native == IntPtr.Zero) { Check(-1, "Could not enumerate folder"); }
        cursor.SetHandleAsInvalid(); // closedir owns the descriptor after fdopendir succeeds.
        try
        {
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                var entry = Readdir(native);
                if (entry == IntPtr.Zero) { if (Marshal.GetLastPInvokeError() != 0) Check(-1, "Folder enumeration failed"); yield break; }
                var length = (ushort)Marshal.ReadInt16(entry, 16) - 19;
                if (length < 1 || length > 261) throw new IOException("Invalid Linux directory entry.");
                var bytes = new byte[length]; Marshal.Copy(entry + 19, bytes, 0, length);
                var end = Array.IndexOf(bytes, (byte)0);
                if (end < 0 || end > 255) throw new IOException("Invalid Linux directory entry name.");
                string name;
                try { name = new UTF8Encoding(false, true).GetString(bytes, 0, end); }
                catch (DecoderFallbackException) { throw new IOException("A filename is not valid UTF-8."); }
                if (name is not ("." or "..")) yield return name;
            }
        }
        finally { Closedir(native); }
    }
    static void SingleName(string name)
    { if (Parts(name).Length != 1) throw new ArgumentException("Invalid child name."); }
    public static void Delete(SafeFileHandle folder, string name, bool directory = false)
    { SingleName(name); Check(Unlinkat(folder, name, directory ? 0x200 : 0), "Could not delete item"); }
    public static void Move(SafeFileHandle source, string name, SafeFileHandle destination, string target)
    { SingleName(name); SingleName(target); Check(Renameat2(source, name, destination, target, 1), "Could not move item without overwriting"); }
    public static void Verify(Metadata expected, SafeFileHandle actual)
    {
        if (!expected.SameFile(Inspect(actual))) throw new IOException("Item changed during operation.");
    }
    public static SafeFileHandle Duplicate(SafeFileHandle handle)
    {
        var fd = Fcntl(handle, 1030, 0); Check(fd, "Could not duplicate descriptor");
        return new(fd, true);
    }
    static SafeFileHandle OpenAbsolute(string path, int flags)
    {
        var fd = OpenNative(path, flags | CloseOnExec | NonBlock); Check(fd, "Could not open item");
        return new(fd, true);
    }
    static SafeFileHandle Beneath(SafeFileHandle parent, string path, bool directory, bool crossMounts = false)
    {
        Parts(path);
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)) throw new PlatformNotSupportedException("Safe file operations require Linux x64 or arm64.");
        var how = new OpenHow { Flags = (ulong)(CloseOnExec | NoFollow | NonBlock | (directory ? DirectoryFlag : 0)), Resolve = 0x08 | 0x04 | (crossMounts ? 0UL : 0x01UL) };
        var fd = Openat2(437, parent, path, ref how, 24);
        Check((long)fd, "Links and nested mounts are not followed; item unavailable or changed");
        return new(fd, true);
    }
    static void Check(long result, string message)
    {
        if (result < 0) throw new IOException(message + ": " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
    }
    public void Dispose() => Root.Dispose();

    [StructLayout(LayoutKind.Sequential)] struct OpenHow { public ulong Flags, Mode, Resolve; }
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    public struct Metadata
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(4)] public uint BlockSize;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(48)] public ulong Blocks;
        [FieldOffset(96)] public long ChangedSeconds;
        [FieldOffset(104)] public uint ChangedNanoseconds;
        [FieldOffset(112)] public long ModifiedSeconds;
        [FieldOffset(120)] public uint ModifiedNanoseconds;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
        [FieldOffset(144)] public ulong Mount;
        public readonly string Kind => (Mode & 0xf000) switch { 0x4000 => "folder", 0x8000 => "file", 0xa000 => "link", _ => "special" };
        public readonly long Allocated => checked((long)Blocks * 512);
        public readonly double Modified => ModifiedSeconds + ModifiedNanoseconds / 1e9;
        public readonly bool SameFile(Metadata other) => DeviceMajor == other.DeviceMajor && DeviceMinor == other.DeviceMinor && Inode == other.Inode && Mount == other.Mount;
        public readonly bool Unchanged(Metadata other) => SameFile(other) && Size == other.Size && ModifiedSeconds == other.ModifiedSeconds && ModifiedNanoseconds == other.ModifiedNanoseconds;
    }
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Openat2(nint number, SafeFileHandle parent, string path, ref OpenHow how, nuint size);
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int OpenNative(string path, int flags);
    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int Statx(SafeFileHandle fd, string path, int flags, uint mask, out Metadata metadata);
    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)] private static partial int Fcntl(SafeFileHandle fd, int operation, int minimum);
    [LibraryImport("libc", EntryPoint = "fdopendir", SetLastError = true)] private static partial IntPtr Fdopendir(int fd);
    [LibraryImport("libc", EntryPoint = "readdir64", SetLastError = true)] private static partial IntPtr Readdir(IntPtr directory);
    [LibraryImport("libc", EntryPoint = "closedir")] private static partial int Closedir(IntPtr directory);
    [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int Unlinkat(SafeFileHandle fd, string name, int flags);
    [LibraryImport("libc", EntryPoint = "renameat2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int Renameat2(SafeFileHandle source, string name, SafeFileHandle destination, string target, uint flags);
}
