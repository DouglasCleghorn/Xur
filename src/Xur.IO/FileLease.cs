using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Xur.IO;

/// <summary>A process-owned flock, without FileStream's additional Unix sharing lock.</summary>
[SupportedOSPlatform("linux")]
public sealed partial class FileLease : IDisposable
{
    readonly SafeFileHandle handle;
    FileLease(SafeFileHandle handle) => this.handle = handle;
    public static FileLease? TryAcquire(string path, bool create = true)
    {
        var fd = Open(path, (create ? 2 | 0x40 : 0) | 0x20000 | 0x80000, 0x180);
        if (fd < 0 && !create && Marshal.GetLastPInvokeError() == 2) return null;
        Check(fd); var handle = new SafeFileHandle(fd, true);
        try
        {
            if (DirectoryTree.Inspect(handle).Kind != "file") throw new IOException("Unsafe lock file.");
            if (Flock(handle, 2 | 4) == 0) return new(handle);
            if (Marshal.GetLastPInvokeError() != 11) Check(-1);
            handle.Dispose(); return null;
        }
        catch { handle.Dispose(); throw; }
    }
    public static bool Busy(string path)
    {
        if (!File.Exists(path)) return false;
        using var lease = TryAcquire(path, create: false);
        return lease is null && File.Exists(path);
    }
    public static async Task<FileLease> Acquire(string path, CancellationToken token = default)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (TryAcquire(path) is { } lease) return lease;
            await Task.Delay(50, token);
        }
    }
    public void Dispose() => handle.Dispose();
    static void Check(int value) { if (value < 0) throw new IOException("Could not acquire file lock: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message); }
    [LibraryImport("libc", EntryPoint="open", SetLastError=true, StringMarshalling=StringMarshalling.Utf8)] private static partial int Open(string path, int flags, uint mode);
    [LibraryImport("libc", EntryPoint="flock", SetLastError=true)] private static partial int Flock(SafeFileHandle fd, int operation);
}
