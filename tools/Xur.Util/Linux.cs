using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Xur.Util;

/// <summary>Linux descriptors and durable publication. statx has the same layout on x64 and arm64.</summary>
public static partial class Linux
{
    public const int NoFollow = 0x20000, CloseOnExec = 0x80000, DirectoryFlag = 0x10000;
    const int AtCurrentDirectory = -100, AtNoFollow = 0x100, AtEmptyPath = 0x1000;

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    public struct Metadata
    {
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(24)] public uint Gid;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(112)] public long ModifiedSeconds;
        [FieldOffset(120)] public uint ModifiedNanoseconds;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
        public readonly bool Regular => (Mode & 0xf000) == 0x8000;
        public readonly bool SameFile(Metadata other) =>
            DeviceMajor == other.DeviceMajor && DeviceMinor == other.DeviceMinor && Inode == other.Inode;
        public readonly bool Unchanged(Metadata other) => SamePublication(other) &&
            ModifiedSeconds == other.ModifiedSeconds && ModifiedNanoseconds == other.ModifiedNanoseconds && Size == other.Size;
        public readonly bool SamePublication(Metadata other) =>
            SameFile(other) && Mode == other.Mode && Uid == other.Uid && Gid == other.Gid;
    }

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(int fd, string path, int flags, uint mask, out Metadata metadata);
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenCreate(string path, int flags, uint mode);
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int fd);
    [LibraryImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static partial int Fchmod(int fd, uint mode);
    [LibraryImport("libc", EntryPoint = "fchown", SetLastError = true)]
    private static partial int Fchown(int fd, uint uid, uint gid);
    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static partial int Flock(int fd, int operation);
    [LibraryImport("libc", EntryPoint = "rename", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Rename(string source, string target);
    [LibraryImport("libc", EntryPoint = "umask")]
    public static partial uint Umask(uint mode);
    [LibraryImport("libc", EntryPoint = "geteuid")]
    public static partial uint EffectiveUser();

    static void Check(int result, string operation)
    {
        if (result < 0) throw new IOException(operation + ": " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
    }

    public static Metadata? Inspect(string path)
    {
        if (Statx(AtCurrentDirectory, path, AtNoFollow, 0x7ff, out var metadata) == 0) return metadata;
        if (Marshal.GetLastPInvokeError() == 2) return null;
        Check(-1, "Inspect " + path);
        return null;
    }

    public static Metadata Inspect(SafeFileHandle handle)
    {
        Check(Statx(handle.DangerousGetHandle().ToInt32(), "", AtEmptyPath, 0x7ff, out var metadata), "Inspect descriptor");
        return metadata;
    }

    public static SafeFileHandle OpenRead(string path, int additionalFlags = 0)
    {
        var fd = Open(path, NoFollow | CloseOnExec | additionalFlags);
        Check(fd, "Open " + path);
        return new SafeFileHandle(fd, ownsHandle: true);
    }

    public static void SyncDirectory(string path)
    {
        using var handle = OpenRead(path, DirectoryFlag);
        Check(Fsync(handle.DangerousGetHandle().ToInt32()), "Sync directory " + path);
    }

    public static void SetMetadata(SafeFileHandle handle, uint mode, uint? uid = null, uint? gid = null)
    {
        var fd = handle.DangerousGetHandle().ToInt32();
        if (uid is not null && gid is not null) Check(Fchown(fd, uid.Value, gid.Value), "Set file ownership");
        Check(Fchmod(fd, mode), "Set file permissions");
    }

    public static FileStream Lock(string path)
    {
        // Opening a FileStream by path takes its own sharing lock on Unix. Open
        // the descriptor directly so our nonblocking flock owns the only lock.
        var fd = OpenCreate(path, 2 | 0x40 | NoFollow | CloseOnExec, 0x180);
        Check(fd, "Open update lock");
        var handle = new SafeFileHandle(fd, ownsHandle: true);
        try
        {
            if (!Inspect(handle).Regular) throw new IOException("Unsafe update lock");
            if (Flock(fd, 2 | 4) < 0)
            {
                if (Marshal.GetLastPInvokeError() == 11) throw new UserError("An application update is already running");
                Check(-1, "Lock application updates");
            }
            return new FileStream(handle, FileAccess.ReadWrite);
        }
        catch { handle.Dispose(); throw; }
    }

    public static void PublishLink(string link, string target)
    {
        var next = Path.Combine(Path.GetDirectoryName(link)!, "next");
        File.Delete(next);
        Directory.CreateSymbolicLink(next, target);
        Check(Rename(next, link), "Select application bundle");
        SyncDirectory(Path.GetDirectoryName(link)!);
    }
}
