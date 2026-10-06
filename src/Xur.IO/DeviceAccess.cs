using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Xur.IO;

[SupportedOSPlatform("linux")]
public static partial class DeviceAccess
{
    public static void Check(IEnumerable<string> paths)
    {
        foreach(var path in paths)
        {
            if(!Path.IsPathFullyQualified(path)||path.Contains('\0'))throw new ArgumentException("Device paths must be absolute.");
            var fd=Open(path,2|0x80000|0x20000);
            if(fd<0)throw new IOException("Could not open device "+path+": "+new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            using var handle=new SafeFileHandle(fd,true);
        }
    }
    [LibraryImport("libc",EntryPoint="open",SetLastError=true,StringMarshalling=StringMarshalling.Utf8)] private static partial int Open(string path,int flags);
}
