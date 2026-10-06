using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

[assembly:System.Runtime.CompilerServices.InternalsVisibleTo("Xur.Unit.Tests")]

namespace Xur.Agent;

// Linux CEC UAPI. The agent keeps device access out of desktop sessions and
// sends only directed TV power messages, never bus-wide standby or input changes.
public interface ICecDevice:IDisposable
{
    CecInfo Info();
    void Power(bool on,ushort? physicalAddress);
}
public record CecInfo(string Driver,string Name,uint Capabilities,int? Card,int? Connector);

public sealed class CecDevice:ICecDevice
{
    readonly SafeFileHandle handle;
    readonly Func<uint,byte[],int>? ioctl;
    internal CecDevice(Func<uint,byte[],int> ioctl){this.ioctl=ioctl;handle=new(IntPtr.Zero,false);}
    public CecDevice(string path)
    {
        var fd=Open(path,2|0x80000); // O_RDWR | O_CLOEXEC
        if(fd<0)throw new InvalidOperationException("CEC adapter could not be opened. Check its driver and permissions.");
        handle=new((IntPtr)fd,true);
    }
    static uint Request(uint direction,int number,int size)=>(direction<<30)|((uint)size<<16)|(0x61u<<8)|(uint)number;
    byte[] Call(uint direction,int number,byte[] data)
    {
        if((ioctl?.Invoke(Request(direction,number,data.Length),data)??Ioctl(handle,Request(direction,number,data.Length),data))<0)
            throw new InvalidOperationException("CEC adapter request failed (errno "+Marshal.GetLastPInvokeError()+"). Check the HDMI cable, adapter and TV's CEC setting.");
        return data;
    }
    public CecInfo Info()
    {
        var caps=Call(3,0,new byte[76]);var flags=BinaryPrimitives.ReadUInt32LittleEndian(caps.AsSpan(68));
        int? card=null,connector=null;
        if((flags&256)!=0)
        {
            var link=Call(2,10,new byte[68]);
            if(BinaryPrimitives.ReadUInt32LittleEndian(link)==1)
            {card=(int)BinaryPrimitives.ReadUInt32LittleEndian(link.AsSpan(4));connector=(int)BinaryPrimitives.ReadUInt32LittleEndian(link.AsSpan(8));}
        }
        string Text(int offset)=>Encoding.UTF8.GetString(caps,offset,32).TrimEnd('\0');
        return new(Text(0),Text(32),flags,card,connector);
    }
    public void Power(bool on,ushort? physicalAddress)
    {
        var info=Info();if((info.Capabilities&4)==0)throw new InvalidOperationException("This CEC adapter cannot transmit commands.");
        var address=BinaryPrimitives.ReadUInt16LittleEndian(Call(2,1,new byte[2]));
        if((info.Capabilities&1)!=0 && physicalAddress is ushort edidAddress && address!=edidAddress)
        {var data=new byte[2];BinaryPrimitives.WriteUInt16LittleEndian(data,edidAddress);Call(1,2,data);address=edidAddress;}
        var logical=Call(2,3,new byte[92]);
        if(address!=0xffff && logical[7]==0 && (info.Capabilities&2)!=0)
        {
            // Claim one playback address only when no other CEC client configured
            // this adapter. CEC 1.4 avoids advertising unsupported 2.0 features.
            logical=new byte[92];logical[6]=5;logical[7]=1;
            BinaryPrimitives.WriteUInt32LittleEndian(logical.AsSpan(8),0xffffff);
            Encoding.ASCII.GetBytes("Xur").CopyTo(logical,16);logical[31]=4;logical[35]=3;
            logical=Call(3,4,logical);
        }
        var initiator=logical[7]>0 && logical[0]<15?logical[0]:(byte)15;
        if(!on && (address==0xffff || initiator==15))throw new InvalidOperationException("CEC standby needs a valid HDMI address. Turn the TV on and check the adapter assignment.");
        // TVs may drop hotplug/EDID in standby. Linux permits Image View On
        // from the unregistered address even when the physical address is gone.
        if(address==0xffff)initiator=15;
        var message=new byte[56];BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(16),2);
        message[32]=(byte)(initiator<<4);message[33]=on?(byte)0x04:(byte)0x36;
        message=Call(3,5,message);
        if((message[50]&1)==0)throw new InvalidOperationException("The TV did not acknowledge the CEC power command. Enable CEC on the TV and check the connection.");
    }
    public void Dispose()=>handle.Dispose();
    [DllImport("libc",EntryPoint="open",SetLastError=true)]static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)]string path,int flags);
    [DllImport("libc",EntryPoint="ioctl",SetLastError=true)]static extern int Ioctl(SafeFileHandle fd,uint request,[In,Out]byte[] data);
}
