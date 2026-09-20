using System.Runtime.InteropServices;

namespace Xur.Agent;

internal static class SysfsPaths
{
    // Resolve every path component in filesystem order. A relative device link
    // beneath /sys/class/drm/cardN must be evaluated after cardN's own symlink.
    public static string Resolve(string path)
    {
        var resolved=RealPath(path,IntPtr.Zero);
        if(resolved==IntPtr.Zero)return "";
        try{return Marshal.PtrToStringUTF8(resolved)??"";}
        finally{Free(resolved);}
    }
    [DllImport("libc",EntryPoint="realpath",SetLastError=true)]
    static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path,IntPtr buffer);
    [DllImport("libc",EntryPoint="free")]
    static extern void Free(IntPtr buffer);
}
