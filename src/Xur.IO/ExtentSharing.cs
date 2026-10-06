using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Xur.IO;

public readonly record struct ExtentResult(ulong Bytes, int Status);

/// <summary>Kernel-verified FIDEDUPERANGE; no userspace content writes or inode replacements.</summary>
[SupportedOSPlatform("linux")]
public partial class ExtentSharing
{
    public virtual bool Supported(SafeFileHandle fd)
    {
        Check(Fstatfs(fd, out var data));
        return (data.Type & 0xffffffff) is 0x9123683e or 0x58465342; // Btrfs and XFS.
    }
    public virtual ExtentResult Share(SafeFileHandle source, SafeFileHandle destination, ulong offset, ulong length)
    {
        var data = new Range { SourceOffset = offset, SourceLength = length, Count = 1, DestinationOffset = offset };
        // Keep both descriptors alive across the ioctl, including the embedded destination fd.
        var retained = false;
        try { destination.DangerousAddRef(ref retained); data.Destination = destination.DangerousGetHandle().ToInt64(); Check(Ioctl(source, 0xc0189436, ref data)); }
        finally { if (retained) destination.DangerousRelease(); }
        if (data.Status < 0) throw new IOException("Extent sharing failed.", new Win32Exception(-data.Status));
        return new(data.Shared, data.Status);
    }
    static void Check(int result) { if (result < 0) throw new IOException("Filesystem extent operation failed.", new Win32Exception(Marshal.GetLastPInvokeError())); }
    [StructLayout(LayoutKind.Sequential)] struct Range
    {
        public ulong SourceOffset, SourceLength;
        public ushort Count, Reserved1;
        public uint Reserved2;
        public long Destination;
        public ulong DestinationOffset, Shared;
        public int Status;
        public uint Reserved3;
    }
    [StructLayout(LayoutKind.Explicit, Size=256)] struct FileSystem { [FieldOffset(0)] public long Type; }
    [LibraryImport("libc", EntryPoint="fstatfs", SetLastError=true)] private static partial int Fstatfs(SafeFileHandle fd, out FileSystem data);
    [LibraryImport("libc", EntryPoint="ioctl", SetLastError=true)] private static partial int Ioctl(SafeFileHandle fd, nuint operation, ref Range data);
}
