using System.Buffers.Binary;
using System.Runtime.InteropServices;
namespace Xur.Agent;

// Read device-local memory from the kernel's i915/xe memory-region queries.
// The two UAPIs report system RAM separately; it is never counted as VRAM.
// UAPI definitions: https://docs.kernel.org/gpu/driver-uapi.html
public static class IntelGpuMemory
{
    public static long Read(string node,string driver)
    {
        if(driver is not ("i915" or "xe"))return 0;
        try
        {
            using var file=File.OpenHandle(node,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            var query=new byte[driver=="xe"?40:16];var item=new byte[24];
            var itemPin=GCHandle.Alloc(item,GCHandleType.Pinned);
            try
            {
                if(driver=="xe")BinaryPrimitives.WriteUInt32LittleEndian(query.AsSpan(8),1);
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(query,1);
                    BinaryPrimitives.WriteInt64LittleEndian(query.AsSpan(8),itemPin.AddrOfPinnedObject().ToInt64());
                    BinaryPrimitives.WriteUInt64LittleEndian(item,4);
                }
                nuint request=driver=="xe"?0xc0286440u:0xc0106479u;
                var fd=file.DangerousGetHandle().ToInt32();
                if(Ioctl(fd,request,query)!=0)return 0;
                var size=BinaryPrimitives.ReadInt32LittleEndian(driver=="xe"?query.AsSpan(12):item.AsSpan(8));
                if(size is <8 or >65536)return 0;
                var data=new byte[size];var dataPin=GCHandle.Alloc(data,GCHandleType.Pinned);
                try
                {
                    BinaryPrimitives.WriteInt64LittleEndian((driver=="xe"?query:item).AsSpan(16),dataPin.AddrOfPinnedObject().ToInt64());
                    return Ioctl(fd,request,query)==0?DeviceBytes(data,driver):0;
                }
                finally{dataPin.Free();}
            }
            finally{itemPin.Free();}
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException){return 0;}
    }
    public static long DeviceBytes(ReadOnlySpan<byte> data,string driver)
    {
        if(driver is not ("i915" or "xe"))return 0;
        int header=driver=="xe"?8:16;const int stride=88;
        if(data.Length<header)return 0;
        uint count=BinaryPrimitives.ReadUInt32LittleEndian(data);
        if(count>(data.Length-header)/stride)return 0;
        ulong total=0;
        for(int i=0;i<count;i++)
        {
            var region=data.Slice(header+i*stride,stride);
            if(BinaryPrimitives.ReadUInt16LittleEndian(region)!=1)continue;
            var bytes=BinaryPrimitives.ReadUInt64LittleEndian(region[8..]);
            if(bytes>(ulong)long.MaxValue-total)return 0;
            total+=bytes;
        }
        return (long)total;
    }
    [DllImport("libc",EntryPoint="ioctl",SetLastError=true)]
    static extern int Ioctl(int fd,nuint request,[In,Out] byte[] query);
}
