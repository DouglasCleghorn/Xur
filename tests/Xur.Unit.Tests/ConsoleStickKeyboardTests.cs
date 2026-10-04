using Xur.Control;

static class ConsoleStickKeyboardTests
{
    static ConsoleGamepadAxis[] Axes(bool unsigned=false)=>new[]{0,1,3,4}.Select(code=>new ConsoleGamepadAxis(code,unsigned?32768:0,unsigned?0:-32768,unsigned?65535:32767))
        .Concat([new(2,0,0,1023),new(5,0,0,1023)]).ToArray();
    static ConsoleStickKeyboard Keyboard(bool unsigned=false)
    {
        var keyboard=new ConsoleStickKeyboard();keyboard.Synchronize([],Axes(unsigned));keyboard.SetContext("fixture");return keyboard;
    }
    static ConsoleControllerInput Report(ConsoleStickKeyboard keyboard)=>keyboard.Read(0,0,0);
    static void Button(ConsoleStickKeyboard keyboard,ushort code)
    {keyboard.Read(1,code,1);Report(keyboard);keyboard.Read(1,code,0);Report(keyboard);}
    static void Set(ConsoleStickKeyboard keyboard,int set){for(var i=0;i<set;i++)Button(keyboard,0x137);}
    static void Point(ConsoleStickKeyboard keyboard,int left,int right,bool unsigned=false)
    {
        var step=2*Math.PI/keyboard.Slices;var center=unsigned?32768:0;
        foreach(var (code,slice) in new[]{(0,left),(3,right)})
        {
            keyboard.Read(3,(ushort)code,center+(int)Math.Round(28000*Math.Sin(slice*step)));
            keyboard.Read(3,(ushort)(code+1),center-(int)Math.Round(28000*Math.Cos(slice*step)));
        }
    }
    static ConsoleControllerInput Hold(ConsoleStickKeyboard keyboard,ushort trigger=5){keyboard.Read(3,trigger,1023);return Report(keyboard);}
    static ConsoleControllerInput Release(ConsoleStickKeyboard keyboard,ushort trigger=5){keyboard.Read(3,trigger,0);return Report(keyboard);}

    public static void Run(Action<bool,string> check)
    {
        check(ConsoleStickKeyboard.Alphabets.SelectMany(a=>a).Distinct().Order().SequenceEqual(Enumerable.Range(32,95).Select(n=>(char)n)),"Stick keyboard covers every printable ASCII character needed by names, addresses and passwords");
        for(var set=0;set<4;set++)
        {
            var alphabet=ConsoleStickKeyboard.Alphabets[set];var keyboard=Keyboard();Set(keyboard,set);
            check(keyboard.Slices==(set==2?4:6) && (keyboard.Slices-1)*(keyboard.Slices-1)<alphabet.Length,"Each character set uses the minimum number of radial slices for its two positional digits");
            for(var index=0;index<alphabet.Length;index++)
            {
                Point(keyboard,index/keyboard.Slices,index%keyboard.Slices);
                var preview=Hold(keyboard);
                check(preview.Character==null && preview.Preview?.Character==alphabet[index],"Stick directions preview ASCII "+(int)alphabet[index]+" without typing it");
                var result=Release(keyboard);
                check(result.Character==alphabet[index] && Release(keyboard).IsEmpty,"Trigger release types ASCII "+(int)alphabet[index]+" exactly once");
            }
        }
        var keys=Keyboard();Point(keys,2,3);
        var initial=Hold(keys);check(initial.Preview is {Left:2,Right:3,Character:'p'},"The left wheel is the most significant digit and the right wheel is the second digit");
        keys.Read(3,0,0);keys.Read(3,1,0);var centered=Report(keys);
        check(centered.Preview?.Character==null && Release(keys).Character==null,"Centering either stick clears the candidate and cancels release-to-type");
        Point(keys,0,0);Hold(keys);keys.Read(1,0x131,1);Report(keys);keys.Read(1,0x131,0);Report(keys);
        check(Release(keys).Character==null,"B during a held-trigger gesture cancels the candidate without typing or leaving the field");
        Point(keys,5,5);Hold(keys);check(Release(keys).Character==null,"Unused wheel combinations cannot insert padding characters");
        Point(keys,0,1);Hold(keys);keys.Read(1,0x130,1);var a=Report(keys);
        check(a.Action==ConsoleKeyAction.None && a.Character==null,"A during character preview cannot prematurely submit a text field");
        keys.Read(1,0x130,0);Report(keys);Release(keys);keys.Read(1,0x130,1);
        check(Report(keys).Action==ConsoleKeyAction.Enter,"A submits only after the trigger gesture has ended");
        keys.Read(1,0x130,0);Report(keys);keys.Read(1,0x134,1);
        check(Report(keys).Character=='\b',"X deletes text through the same edit operation as keyboard Backspace");
        keys.Read(1,0x134,2);check(Report(keys).Character==null,"Holding X cannot repeatedly delete text");
        keys.Read(1,0x134,0);Report(keys);keys.Read(1,0x133,1);
        check(Report(keys).Character==' ',"Y inserts a space, including in Wi-Fi passwords");
        keys=Keyboard(unsigned:true);Point(keys,1,2,unsigned:true);
        check(Hold(keys).Preview?.Character=='i' && Release(keys).Character=='i',"Unsigned HID axes produce the same two-stick letter selection");
        keys=Keyboard();Point(keys,0,0);keys.Read(3,5,300);
        check(Report(keys).Preview?.Active!=true,"A lightly touched trigger cannot arm text entry");
        Hold(keys);keys.Read(3,5,300);check(Report(keys).Character==null,"Trigger hysteresis prevents typing while a held trigger fluctuates");
        check(Release(keys).Character=='a',"A deliberate trigger release commits after the hysteresis threshold");
        keys=Keyboard();Point(keys,0,0);Hold(keys);
        void Angle(double degrees)
        {
            keys.Read(3,0,(int)Math.Round(28000*Math.Sin(degrees*Math.PI/180)));
            keys.Read(3,1,-(int)Math.Round(28000*Math.Cos(degrees*Math.PI/180)));
        }
        Angle(29);Report(keys);Angle(31);
        check(Report(keys).IsEmpty,"Angular hysteresis keeps the candidate stable around a slice boundary without repainting");
        Angle(20);var motion=Report(keys);
        check(motion.Preview is {Character:'a',Left:0,LeftX:>0} && motion.Character==null,"Stick position dots can move inside a selected sector without typing or changing its letter");
        Angle(36);check(Report(keys).Preview?.Character=='g',"A deliberate move beyond the slice boundary selects the neighboring group");
        Release(keys);Point(keys,0,0);Hold(keys);keys.Read(3,5,0);Point(keys,0,1);
        check(Report(keys).Character=='b',"Release-to-type uses the final stick coordinates in the same input packet");
        keys=Keyboard();Point(keys,0,0);Hold(keys,2);Hold(keys,5);
        check(Release(keys,2).Character==null && Release(keys,5).Character=='a',"Holding both triggers commits only when the last trigger is released");
        keys=Keyboard();Point(keys,0,0);Hold(keys);keys.SuppressGesture();
        check(Release(keys).Character==null,"A trigger used to wake the display cannot type when subsequently released");
        Hold(keys);check(Release(keys).Character=='a',"A fresh trigger gesture can type after waking");
        Hold(keys);keys.SetContext("different-field");check(Release(keys).Character==null,"Changing text fields cancels a gesture started in the previous field");
        keys.Synchronize([0x139],Axes());keys.SetContext("reconnected");keys.Read(1,0x139,0);
        check(Report(keys).Character==null,"Reconnect snapshots with a held digital trigger cannot type a stale character");
        keys.Read(1,0x139,1);Point(keys,0,0);Report(keys);keys.Read(1,0x139,0);
        check(Report(keys).Character=='a',"Digital trigger mappings support fresh release-to-type gestures");

        // Preview stays local; secret snapshots keep the existing password contract.
        var password=new ConsoleScreen("stick-password","Wi-Fi password","Enter password.",[new('0',"Cancel")],"",true);
        LocalConsole.OpenMaintenance(password);LocalConsole.EditText('s');
        var previewState=new ConsoleKeyboardPreview(true,0,0,0,'a');LocalConsole.SetControllerPreview(previewState);
        var frame=LocalConsole.ExportFrame(100,40);var snapshot=LocalConsole.DiagnosticSnapshot();
        check(frame.Contains("Preview: a") && frame.Contains("> *") && !frame.Contains("> s"),"Local stick preview remains visible while the entered password stays masked");
        foreach(var (columns,rows) in new[]{(80,25),(40,12)})
            check(LocalConsole.ExportFrame(columns,rows).Contains("Preview: a"),"The selected character remains visible on compact physical console frames");
        check(snapshot.InputValue==null && !snapshot.Body.Contains("Preview:"),"Diagnostic console snapshots omit secret values and transient character previews");
        LocalConsole.OpenMaintenance(password,refreshOnly:true);
        check(LocalConsole.ExportFrame(100,40).Contains("Preview: a"),"Background text-screen refresh retains the active two-stick preview");
        LocalConsole.SetControllerPreview(null);
        check(!LocalConsole.ExportFrame(100,40).Contains("Preview:"),"Removing a controller preview restores normal text entry without submitting it");

        var reader=new ConsoleGamepadReader();reader.Synchronize([],true,0,-32768,32767,0,0);reader.Keyboard.Synchronize([],Axes());reader.SetTextContext("editing");
        check(reader.ReadController(3,1,24000).IsEmpty,"Stick motion in a text field cannot also navigate menu rows");
        reader.SetTextContext(null);check(reader.Repeat()==ConsoleKeyAction.None,"Leaving text entry cannot repeat a held stick into a confirmation screen");
    }
}
