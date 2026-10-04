using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Xur.Control;

// Linux's gamepad positions cover xpad (USB/receiver) and HID (Bluetooth).
// Select/Back never repeat, including drivers that send repeated press events.
internal sealed class ConsoleGamepadReader(TimeProvider? clock=null)
{
    internal const int South=0x130,East=0x131,LeftBumper=0x136,RightBumper=0x137,Up=0x220,Down=0x221;
    internal const int StickY=1,HatY=0x11;
    readonly TimeProvider time=clock??TimeProvider.System;
    readonly HashSet<int> pressed=[];
    bool hasHat;
    int hat,stick;
    double center,enter,leave;
    ConsoleKeyAction repeat;
    long lastRepeat;
    bool repeating;

    ConsoleKeyAction Held
    {
        get
        {
            var direction=hasHat?hat:(pressed.Contains(Down)?1:0)-(pressed.Contains(Up)?1:0);
            if(direction==0)direction=stick;
            if(direction!=0)return direction<0?ConsoleKeyAction.Up:ConsoleKeyAction.Down;
            if(pressed.Contains(LeftBumper)==pressed.Contains(RightBumper))return ConsoleKeyAction.None;
            return pressed.Contains(LeftBumper)?ConsoleKeyAction.PageUp:ConsoleKeyAction.PageDown;
        }
    }

    public void Synchronize(IEnumerable<int> buttons,bool hatAvailable,int hatValue,int minimum,int maximum,int flat,int stickValue)
    {
        pressed.Clear();pressed.UnionWith(buttons);hasHat=hatAvailable;hat=Math.Sign(hatValue);
        center=(minimum+(double)maximum)/2;
        enter=maximum>minimum?Math.Max((maximum-(double)minimum)*.20,flat):double.PositiveInfinity;
        leave=maximum>minimum?Math.Max((maximum-(double)minimum)*.125,flat):double.PositiveInfinity;
        stick=0;SetStick(stickValue);repeat=ConsoleKeyAction.None;
    }

    void SetStick(int value)
    {
        var delta=value-center;
        if(Math.Abs(delta)<=leave || stick!=0 && Math.Sign(delta)!=stick)stick=0;
        if(Math.Abs(delta)>=enter)stick=Math.Sign(delta);
    }

    public ConsoleKeyAction Read(ushort type,ushort code,int value)
    {
        var before=Held;
        if(type==1) // EV_KEY
        {
            if(code is not (South or East or LeftBumper or RightBumper or Up or Down) || value is not (0 or 1))return ConsoleKeyAction.None;
            if(value==0)pressed.Remove(code);
            else if(!pressed.Add(code))return ConsoleKeyAction.None;
            else if(code==South)return ConsoleKeyAction.Enter;
            else if(code==East)return ConsoleKeyAction.Back;
        }
        else if(type==3 && code==HatY && hasHat)hat=Math.Sign(value); // EV_ABS
        else if(type==3 && code==StickY)SetStick(value);
        else return ConsoleKeyAction.None;
        var after=Held;
        if(before==after)return ConsoleKeyAction.None;
        repeat=after;lastRepeat=time.GetTimestamp();repeating=false;
        return after;
    }

    public ConsoleKeyAction Repeat()
    {
        if(repeat==ConsoleKeyAction.None || Held!=repeat || time.GetElapsedTime(lastRepeat)<TimeSpan.FromMilliseconds(repeating?100:400))return ConsoleKeyAction.None;
        lastRepeat=time.GetTimestamp();repeating=true;return repeat;
    }
}

internal interface IConsoleGamepadDevice:IDisposable
{
    bool Read(out ConsoleKeyAction action);
    ConsoleKeyAction Repeat();
}

// One reader in the control service, never one per display. Read-only evdev
// descriptors are not grabbed; workstation and synthetic streaming seats stay out.
internal sealed class ConsoleGamepadInput(string sysRoot="/sys",string devRoot="/dev",string udevRoot="/run/udev/data",
    TimeProvider? clock=null,Func<string,IConsoleGamepadDevice>? openDevice=null):IDisposable
{
    sealed record Node(string Path,string Identity);
    readonly TimeProvider time=clock??TimeProvider.System;
    readonly Dictionary<string,(Node Node,IConsoleGamepadDevice Device)> devices=[];
    long lastScan;
    bool scanned;

    static string Read(string path)=>File.ReadAllText(path).Trim();
    internal static bool HasBit(string bitmap,int bit)
    {
        var words=bitmap.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        var word=bit/(IntPtr.Size*8);
        return word<words.Length && ulong.TryParse(words[words.Length-1-word],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var value)
            && (value&(1UL<<(bit%(IntPtr.Size*8))))!=0;
    }
    Node? Describe(string sysPath)
    {
        try
        {
            var properties=File.ReadAllLines(Path.Combine(udevRoot,"c"+Read(sysPath+"/dev")));
            var seat=properties.FirstOrDefault(p=>p.StartsWith("E:ID_SEAT=",StringComparison.Ordinal));
            if(seat!=null && seat!="E:ID_SEAT=seat0")return null;
            if(Read(sysPath+"/device/phys").StartsWith("xur/",StringComparison.Ordinal))return null;
            var keys=Read(sysPath+"/device/capabilities/key");
            var axes=Read(sysPath+"/device/capabilities/abs");
            if(!HasBit(keys,ConsoleGamepadReader.South) || !HasBit(keys,ConsoleGamepadReader.East)
                || !(HasBit(axes,ConsoleGamepadReader.StickY) || HasBit(axes,ConsoleGamepadReader.HatY)
                    || HasBit(keys,ConsoleGamepadReader.Up) && HasBit(keys,ConsoleGamepadReader.Down)))return null;
            var target=new DirectoryInfo(sysPath).ResolveLinkTarget(true)?.FullName??sysPath;
            // udev's initialization timestamp also distinguishes reused event names.
            return new(Path.Combine(devRoot,"input",Path.GetFileName(sysPath)),target+"\n"+properties.FirstOrDefault(p=>p.StartsWith("I:",StringComparison.Ordinal)));
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException){return null;}
    }

    string Foreground()
    {
        try{return Read(Path.Combine(sysRoot,"class/tty/tty0/active"));}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException){return "";}
    }

    internal async Task Pump(Func<ConsoleKeyAction,bool,Task> dispatch)
    {
        var active=Foreground();
        if(active is not ("tty3" or "tty2")){Dispose();scanned=false;return;}
        if(!scanned || time.GetElapsedTime(lastScan)>=TimeSpan.FromSeconds(1))
        {
            lastScan=time.GetTimestamp();scanned=true;
            string[] paths;
            try{paths=Directory.GetDirectories(Path.Combine(sysRoot,"class/input"),"event*");}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException){paths=[];}
            foreach(var path in paths)
            {
                var node=Describe(path);if(node==null || devices.ContainsKey(path))continue;
                try{devices[path]=(node,openDevice?.Invoke(node.Path)??new ConsoleGamepadDevice(node.Path,time));}
                catch(Exception e) when(e is IOException or UnauthorizedAccessException){/* Retry inaccessible or disconnected devices next scan. */}
            }
        }
        foreach(var (path,entry) in devices.ToArray())
        {
            if(Describe(path)!=entry.Node){Remove(path);scanned=false;continue;}
            try
            {
                // Bound work even if a broken device floods its event queue.
                for(var i=0;i<128 && entry.Device.Read(out var action);i++)
                    if(action!=ConsoleKeyAction.None)
                    {
                        if(Foreground()!=active || Describe(path)!=entry.Node){Remove(path);scanned=false;break;}
                        await dispatch(action,active=="tty2");
                    }
                if(!devices.ContainsKey(path))continue;
                var repeated=entry.Device.Repeat();
                if(repeated!=ConsoleKeyAction.None && Foreground()==active && Describe(path)==entry.Node)await dispatch(repeated,active=="tty2");
            }
            catch(IOException){Remove(path);scanned=false;}
        }
    }

    void Remove(string path){devices[path].Device.Dispose();devices.Remove(path);}
    public void Dispose(){foreach(var path in devices.Keys.ToArray())Remove(path);}
    public async Task Run(Func<ConsoleKeyAction,bool,Task> dispatch,SemaphoreSlim gate,CancellationToken stop)
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                await gate.WaitAsync(stop);
                try{await Pump(dispatch);}finally{gate.Release();}
                await Task.Delay(20,stop);
            }
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested){ }
        finally{Dispose();}
    }
}

internal sealed class ConsoleGamepadDevice:IConsoleGamepadDevice
{
    readonly SafeFileHandle handle;
    readonly ConsoleGamepadReader keys;
    readonly Action synchronize;
    bool dropped;
    int Fd=>handle.DangerousGetHandle().ToInt32();
    public ConsoleGamepadDevice(string path,TimeProvider time)
    {
        var fd=Open(path,0x800|0x80000); // O_RDONLY | O_NONBLOCK | O_CLOEXEC
        if(fd<0)throw new IOException("Gamepad could not be opened.");
        handle=new((IntPtr)fd,true);keys=new(time);synchronize=Synchronize;
        try{synchronize();}catch{handle.Dispose();throw;}
    }
    // Allow the event transport to be exercised using a nonblocking pipe and
    // state snapshots, without creating a controller on the test host.
    internal ConsoleGamepadDevice(SafeFileHandle ownedHandle,ConsoleGamepadReader reader,Action stateSnapshot)
    {handle=ownedHandle;keys=reader;synchronize=stateSnapshot;}
    // _IOR('E', number, byte[length]), as defined by linux/input.h on Linux x64.
    static nuint Request(int number,int length)=>(nuint)(0x80004500U | (uint)(length<<16) | (uint)number);
    void Synchronize()
    {
        var buttons=new byte[96]; // KEY_CNT / 8
        if(IoctlBytes(Fd,Request(0x18,buttons.Length),buttons)<0)throw new IOException("Gamepad state unavailable.");
        var held=Enumerable.Range(0,buttons.Length*8).Where(bit=>(buttons[bit/8]&(1<<(bit%8)))!=0);
        var hasHat=IoctlAxis(Fd,Request(0x40+ConsoleGamepadReader.HatY,24),out var hat)==0;
        var hasStick=IoctlAxis(Fd,Request(0x40+ConsoleGamepadReader.StickY,24),out var stick)==0;
        keys.Synchronize(held,hasHat,hat.Value,hasStick?stick.Minimum:0,hasStick?stick.Maximum:0,stick.Flat,stick.Value);
    }
    public bool Read(out ConsoleKeyAction action)
    {
        action=ConsoleKeyAction.None;
        var length=NativeRead(Fd,out var input,(nuint)Marshal.SizeOf<InputEvent>());
        if(length<0)
        {
            var error=Marshal.GetLastPInvokeError();
            if(error==11)return false; // EAGAIN: no queued events.
            if(error==4)return true; // EINTR: try again within the bounded batch.
            throw new IOException("Gamepad disconnected.");
        }
        if(length!=Marshal.SizeOf<InputEvent>())throw new IOException("Incomplete gamepad event.");
        if(input.Type==0 && input.Code==3){dropped=true;return true;} // SYN_DROPPED
        if(dropped)
        {
            // Never execute possibly stale confirmation presses after an overrun.
            if(input.Type==0 && input.Code==0){synchronize();dropped=false;}
            return true;
        }
        action=keys.Read(input.Type,input.Code,input.Value);return true;
    }
    public ConsoleKeyAction Repeat()=>dropped?ConsoleKeyAction.None:keys.Repeat();
    public void Dispose()=>handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] struct InputEvent {public nint Seconds,Microseconds;public ushort Type,Code;public int Value;}
    [StructLayout(LayoutKind.Sequential)] struct Axis {public int Value,Minimum,Maximum,Fuzz,Flat,Resolution;}
    [DllImport("libc",EntryPoint="open",SetLastError=true)] static extern int Open(string path,int flags);
    [DllImport("libc",EntryPoint="read",SetLastError=true)] static extern nint NativeRead(int fd,out InputEvent value,nuint count);
    [DllImport("libc",EntryPoint="ioctl",SetLastError=true)] static extern int IoctlBytes(int fd,nuint request,byte[] value);
    [DllImport("libc",EntryPoint="ioctl",SetLastError=true)] static extern int IoctlAxis(int fd,nuint request,out Axis value);
}
