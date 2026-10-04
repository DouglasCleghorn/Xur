using Xur.Control;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

static class ConsoleGamepadTests
{
    sealed class Clock:TimeProvider
    {
        public long Ticks;
        public override long GetTimestamp()=>Ticks;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public void Advance(int milliseconds)=>Ticks+=TimeSpan.FromMilliseconds(milliseconds).Ticks;
    }
    static ConsoleGamepadReader Reader(Clock clock,bool hat=true,int minimum=-32768,int maximum=32767)
    {
        var reader=new ConsoleGamepadReader(clock);reader.Synchronize([],hat,0,minimum,maximum,0,(minimum+maximum)/2);
        reader.Keyboard.Synchronize([],new[]{0,1,3,4}.Select(code=>new ConsoleGamepadAxis(code,(minimum+maximum)/2,minimum,maximum))
            .Concat([new(2,0,0,1023),new(5,0,0,1023)]));return reader;
    }
    public static async Task Run(Action<bool,string> check)
    {
        var clock=new Clock();var keys=Reader(clock);
        ConsoleKeyAction Button(int code,int value=1)=>keys.Read(1,(ushort)code,value);
        ConsoleKeyAction Axis(int code,int value)=>keys.Read(3,(ushort)code,value);
        check(Button(0x130)==ConsoleKeyAction.Enter && Button(0x131)==ConsoleKeyAction.Back,"Xbox A opens and B backs out through shared console actions");
        clock.Advance(1000);
        check(Button(0x130)==ConsoleKeyAction.None && Button(0x130,2)==ConsoleKeyAction.None && keys.Repeat()==ConsoleKeyAction.None,"Held, duplicate and kernel-repeat A events cannot confirm another action");
        Button(0x130,0);check(Button(0x130)==ConsoleKeyAction.Enter,"Xbox A accepts another action only after release and a new press");
        check(Button(0x133)==ConsoleKeyAction.None && Axis(5,32767)==ConsoleKeyAction.None,"Unmapped face buttons and triggers do not invoke menu actions");
        check(Axis(0x11,-1)==ConsoleKeyAction.Up && Button(0x220)==ConsoleKeyAction.None,"Hat D-pad navigates once even when a driver also reports digital D-pad buttons");
        check(Axis(0x11,0)==ConsoleKeyAction.None && Axis(0x11,1)==ConsoleKeyAction.Down,"Hat release does not move selection and downward motion selects the next row");
        clock.Advance(399);check(keys.Repeat()==ConsoleKeyAction.None,"Held navigation waits for its initial repeat delay");
        clock.Advance(1);check(keys.Repeat()==ConsoleKeyAction.Down,"Held D-pad starts repeating after 400 milliseconds");
        clock.Advance(99);check(keys.Repeat()==ConsoleKeyAction.None,"Navigation repeat does not run faster than ten actions a second");
        clock.Advance(1);check(keys.Repeat()==ConsoleKeyAction.Down,"Navigation repeats at a stable cadence without more evdev motion");
        Axis(0x11,0);clock.Advance(1000);check(keys.Repeat()==ConsoleKeyAction.None,"Releasing the D-pad stops repeating");
        keys=Reader(clock,hat:false);
        check(Button(0x220)==ConsoleKeyAction.Up && Button(0x220,0)==ConsoleKeyAction.None && Button(0x221)==ConsoleKeyAction.Down,"Digital D-pad layouts used by HID controllers navigate in both directions");
        Button(0x221,0);
        check(Button(0x136)==ConsoleKeyAction.PageUp,"LB scrolls backward");Button(0x136,0);
        check(Button(0x137)==ConsoleKeyAction.PageDown,"RB scrolls forward");Button(0x137,0);
        check(Axis(1,4000)==ConsoleKeyAction.None && Axis(1,-4000)==ConsoleKeyAction.None,"Left-stick drift around center does not move selection");
        check(Axis(1,-24000)==ConsoleKeyAction.Up && Axis(1,-13000)==ConsoleKeyAction.None && Axis(1,-15000)==ConsoleKeyAction.None,"Stick hysteresis prevents duplicate navigation near the activation threshold");
        Axis(1,0);check(Axis(1,24000)==ConsoleKeyAction.Down,"Left-stick centering rearms downward navigation");
        keys=Reader(clock,minimum:0,maximum:65535);
        check(Axis(1,32768)==ConsoleKeyAction.None && Axis(1,0)==ConsoleKeyAction.Up && Axis(1,65535)==ConsoleKeyAction.Down,"Unsigned HID stick ranges normalize using the reported minimum and maximum");
        keys.Synchronize([0x130,0x137],true,1,-32768,32767,0,24000);clock.Advance(1000);
        check(keys.Repeat()==ConsoleKeyAction.None && Button(0x130)==ConsoleKeyAction.None,"Reconnection and overrun synchronization ignore already held buttons and directions");
        Button(0x130,0);check(Button(0x130)==ConsoleKeyAction.Enter,"A fresh press works after synchronization");

        // The same mapper feeds real menu selection, disabled options and wake gates.
        keys=Reader(clock);
        var confirmation=new ConsoleScreen("controller-confirm","Confirm action","Cancel is selected first.",[new('0',"Cancel"),new('y',"Confirm")]);
        LocalConsole.OpenMaintenance(confirmation);
        check(LocalConsole.Navigate(Button(0x130))=='0',"Xbox A honors the default Cancel selection on a confirmation screen");
        LocalConsole.OpenMaintenance(confirmation with{Id="controller-disabled",Options=[new('y',"Unavailable",false),new('0',"Back")]});
        Button(0x130,0);check(LocalConsole.Navigate(Button(0x130))==null,"Xbox A cannot invoke a disabled option");
        LocalConsole.OpenMaintenance(confirmation);
        LocalConsole.Navigate(Axis(0x11,1));
        Axis(0x11,0);
        Button(0x130,0);check(LocalConsole.Navigate(Button(0x130))=='y',"D-pad selection and a fresh A press use the same explicit confirmation as Enter");
        using(var idle=LocalConsole.UseIdleClock(clock))
        {
            clock.Advance(600000);
            Button(0x130,0);var action=Button(0x130);
            check(LocalConsole.ConsumeWakeInput(false,null,action),"The first Xbox A press after idle is consumed only to wake the display");
            clock.Advance(1000);
            check(Button(0x130)==ConsoleKeyAction.None && keys.Repeat()==ConsoleKeyAction.None,"Continuing to hold the wake button cannot confirm the selected action");
            Button(0x130,0);action=Button(0x130);
            check(!LocalConsole.ConsumeWakeInput(false,null,action) && LocalConsole.Navigate(action)=='y',"A second fresh Xbox A press can confirm after waking");
        }
        LocalConsole.OpenLogs();Button(0x130,0);check(LocalConsole.Navigate(Button(0x130))=='0',"Xbox A returns from the full-screen log view");
        LocalConsole.OpenQr(false);Button(0x131,0);check(LocalConsole.Navigate(Button(0x131))=='0',"Xbox B returns from the QR view");
        check(LocalConsole.Clean(LocalConsole.Frame("Controller","Ready",100,40)).Contains("Enter/A: Open"),"Physical menu hints show Xbox select and back controls");
        Transport(check);
        await Routing(check);
    }

    static void Transport(Action<bool,string> check)
    {
        var descriptors=new int[2];if(Pipe(descriptors,0x800|0x80000)!=0)throw new IOException("Fixture pipe could not be opened.");
        var clock=new Clock();var reader=Reader(clock);var synchronized=0;
        using var device=new ConsoleGamepadDevice(new SafeFileHandle((IntPtr)descriptors[0],true),reader,
            ()=>{synchronized++;reader.Synchronize([0x130],true,0,-32768,32767,0,0);});
        using var writer=new FileStream(new SafeFileHandle((IntPtr)descriptors[1],true),FileAccess.Write);
        void Event(ushort type,ushort code,int value)
        {
            var bytes=new byte[2*IntPtr.Size+8];
            BitConverter.TryWriteBytes(bytes.AsSpan(2*IntPtr.Size),type);
            BitConverter.TryWriteBytes(bytes.AsSpan(2*IntPtr.Size+2),code);
            BitConverter.TryWriteBytes(bytes.AsSpan(2*IntPtr.Size+4),value);
            writer.Write(bytes);writer.Flush();
        }
        check(!device.Read(out _),"Native evdev transport returns immediately when its nonblocking descriptor has no events");
        Event(1,0x130,1);check(device.Read(out var action) && action.Action==ConsoleKeyAction.Enter,"Native Linux input_event bytes decode Xbox A through the real read syscall");
        Event(1,0x130,2);check(device.Read(out action) && action.Action==ConsoleKeyAction.None,"Native event transport suppresses kernel button repeat");
        Event(3,0x11,1);device.Read(out _);clock.Advance(1000);
        Event(0,3,0);check(device.Read(out action) && action.Action==ConsoleKeyAction.None && device.Repeat().Action==ConsoleKeyAction.None,"SYN_DROPPED immediately stops held navigation repeats");
        Event(1,0x130,0);Event(1,0x130,1);Event(3,0x11,-1);
        for(var i=0;i<3;i++)check(device.Read(out action) && action.Action==ConsoleKeyAction.None,"Events between SYN_DROPPED and SYN_REPORT cannot operate the menu");
        Event(0,0,0);check(device.Read(out action) && action.Action==ConsoleKeyAction.None && synchronized==1,"SYN_REPORT after overrun queries fresh device state without dispatching an action");
        Event(1,0x130,1);check(device.Read(out action) && action.Action==ConsoleKeyAction.None,"A held in the recovery snapshot cannot confirm another screen");
        Event(1,0x130,0);device.Read(out _);Event(1,0x130,1);
        check(device.Read(out action) && action.Action==ConsoleKeyAction.Enter,"Native event handling resumes on a fresh A press after overrun recovery");
        device.SetTextContext("pipe-field");
        Event(3,1,-28000);Event(3,4,-28000);Event(3,5,1023);
        for(var i=0;i<3;i++)check(device.Read(out action) && action.IsEmpty,"Native stick and trigger updates wait for the complete input packet before previewing");
        Event(0,0,0);check(device.Read(out action) && action.Preview is {Active:true,Character:'a'} && action.Character==null,"Native controller packets display a two-stick candidate without inserting text");
        Event(3,5,0);check(device.Read(out action) && action.IsEmpty,"Native trigger release waits for SYN_REPORT before typing");
        Event(0,0,0);check(device.Read(out action) && action.Character=='a',"Native trigger release inserts the candidate through the text-input event path");
        writer.Dispose();var disconnected=false;try{device.Read(out _);}catch(IOException){disconnected=true;}
        check(disconnected,"Native event transport reports closed descriptors as disconnections");
    }
    [DllImport("libc",EntryPoint="pipe2",SetLastError=true)] static extern int Pipe(int[] descriptors,int flags);

    sealed class Device(Clock clock):IConsoleGamepadDevice
    {
        readonly ConsoleGamepadReader keys=Reader(clock);
        readonly Queue<(ushort Type,ushort Code,int Value)> events=[];
        public bool Disposed,Disconnected;
        public void Queue(ushort type,ushort code,int value)=>events.Enqueue((type,code,value));
        public bool Read(out ConsoleControllerInput action)
        {
            if(Disconnected)throw new IOException("Fixture disconnected.");
            action=default;if(!events.TryDequeue(out var input))return false;
            action=keys.ReadController(input.Type,input.Code,input.Value);return true;
        }
        public ConsoleControllerInput Repeat()=>keys.Keyboard.Editing?default:new(keys.Repeat());
        public void SetTextContext(string? context)=>keys.SetTextContext(context);
        public void SuppressGesture()=>keys.Keyboard.SuppressGesture();
        public void Dispose()=>Disposed=true;
    }
    static async Task Routing(Action<bool,string> check)
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../.build/evidence/console-gamepad-"+Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var sys=root+"/sys";var udev=root+"/udev";Directory.CreateDirectory(udev);
            Directory.CreateDirectory(sys+"/class/input");Directory.CreateDirectory(sys+"/class/tty/tty0");
            var active=sys+"/class/tty/tty0/active";File.WriteAllText(active,"tty3");
            string Bitmap(params int[] bits)
            {
                var words=new ulong[bits.Max()/(IntPtr.Size*8)+1];foreach(var bit in bits)words[bit/(IntPtr.Size*8)]|=1UL<<(bit%(IntPtr.Size*8));
                return string.Join(' ',words.Reverse().Select(w=>w.ToString("x")));
            }
            void Node(int index,string? seat=null,string phys="usb-fixture/input0",bool gamepad=true)
            {
                var path=sys+"/class/input/event"+index;Directory.CreateDirectory(path+"/device/capabilities");
                File.WriteAllText(path+"/dev","13:"+(64+index));File.WriteAllText(path+"/device/phys",phys);
                File.WriteAllText(path+"/device/capabilities/key",gamepad?Bitmap(0x130,0x131):Bitmap(28,103,108));
                File.WriteAllText(path+"/device/capabilities/abs",Bitmap(1,0x11));
                Properties(index,seat);
            }
            void Properties(int index,string? seat=null,string identity="1")=>File.WriteAllText(udev+"/c13:"+(64+index),"I:"+identity+"\nE:ID_INPUT_JOYSTICK=1\n"+(seat==null?"":"E:ID_SEAT="+seat+"\n"));
            Node(0);Node(1,"seat-xur-test");Node(2,phys:"xur/seat-xur-stream/input0");Node(3,gamepad:false);Node(4,"seat-xur-unassigned");Node(5);File.Delete(udev+"/c13:69");
            var opened=new List<Device>();var clock=new Clock();string? editor=null;var cleared=0;
            using var input=new ConsoleGamepadInput(sys,root+"/dev",udev,clock,_=>{var device=new Device(clock);opened.Add(device);return device;},()=>editor,()=>cleared++);
            var actions=new List<(ConsoleKeyAction Action,bool Logs)>();
            Task<bool> Dispatch(ConsoleControllerInput input,bool logs){actions.Add((input.Action,logs));return Task.FromResult(false);}
            await input.Pump(Dispatch);
            check(opened.Count==1,"Gamepad discovery excludes workstation, unassigned, streaming, keyboard and unsettled udev devices");
            opened[0].Queue(3,0x11,1);opened[0].Queue(1,0x130,1);await input.Pump(Dispatch);
            check(actions.SequenceEqual([(ConsoleKeyAction.Down,false),(ConsoleKeyAction.Enter,false)]),"Discovered controller motion and A reach the shared menu once");
            actions.Clear();Properties(0,"seat-xur-test");opened[0].Queue(1,0x131,1);await input.Pump(Dispatch);
            check(opened[0].Disposed && actions.Count==0,"Changing controller ownership closes its reader before queued input can reach setup");
            Properties(0,"seat0");await input.Pump(Dispatch);check(opened.Count==2,"Returning a controller to seat0 reopens its reader without restarting the manager");
            opened[1].Queue(1,0x130,1);File.WriteAllText(active,"tty1");await input.Pump(Dispatch);
            check(opened[1].Disposed && actions.Count==0,"Switching away from menu terminals suppresses queued controller actions and closes descriptors");
            File.WriteAllText(active,"tty2");await input.Pump(Dispatch);opened[2].Queue(1,0x131,1);await input.Pump(Dispatch);
            check(actions.SequenceEqual([(ConsoleKeyAction.Back,true)]),"Xbox B on the dedicated log VT uses its return-to-menu input path");
            actions.Clear();opened[2].Disconnected=true;await input.Pump(Dispatch);
            check(opened[2].Disposed && actions.Count==0,"A disconnected gamepad is released without invoking menu actions");
            await input.Pump(Dispatch);check(opened.Count==4,"Gamepad reads resume after reconnecting");
            Properties(0,identity:"2");opened[3].Queue(1,0x130,1);await input.Pump(Dispatch);
            check(opened[3].Disposed && actions.Count==0,"A reused event name cannot dispatch queued input from the old device identity");
            await input.Pump(Dispatch);check(opened.Count==5,"An event node with a new identity is discovered again");
            Node(6);clock.Advance(1000);await input.Pump(Dispatch);check(opened.Count==6,"A second controller hot-plugs after the periodic discovery interval");
            editor="controller-field";File.WriteAllText(active,"tty3");
            var textInputs=new List<ConsoleControllerInput>();var wake=true;
            Task<bool> Text(ConsoleControllerInput command,bool logs){textInputs.Add(command);var consumed=wake;wake=false;return Task.FromResult(consumed);}
            var typing=opened[4];typing.Queue(3,1,-28000);typing.Queue(3,4,-28000);typing.Queue(3,5,1023);typing.Queue(0,0,0);
            await input.Pump(Text);typing.Queue(3,5,0);typing.Queue(0,0,0);await input.Pump(Text);
            check(textInputs.All(command=>command.Character==null),"The shared input pump suppresses trigger-release typing after its wake gate consumes the gesture");
            textInputs.Clear();typing.Queue(3,5,1023);typing.Queue(0,0,0);await input.Pump(Text);typing.Queue(3,5,0);typing.Queue(0,0,0);await input.Pump(Text);
            check(textInputs.Count(command=>command.Character=='a')==1,"The shared input pump forwards a fresh two-stick gesture as exactly one text edit");
            typing.Queue(3,5,1023);typing.Queue(0,0,0);await input.Pump(Text);textInputs.Clear();Properties(0,"seat-xur-test");typing.Queue(3,5,0);typing.Queue(0,0,0);await input.Pump(Text);
            check(textInputs.All(command=>command.Character==null) && typing.Disposed && cleared>0,"A seat handoff cancels a pending typing gesture and removes its local preview");
            using var gate=new SemaphoreSlim(1,1);using var stop=new CancellationTokenSource();
            var running=input.Run(Dispatch,gate,stop.Token);stop.Cancel();await running;
            check(opened.All(d=>d.Disposed),"Manager cancellation releases every gamepad descriptor");
        }
        finally{Directory.Delete(root,true);}
    }
}
