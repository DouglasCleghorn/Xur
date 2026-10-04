namespace Xur.Control;

internal readonly record struct ConsoleControllerInput(ConsoleKeyAction Action=ConsoleKeyAction.None,char? Character=null,ConsoleKeyboardPreview? Preview=null,bool Activity=true)
{
    public bool IsEmpty=>Action==ConsoleKeyAction.None && Character==null && Preview==null;
    public static implicit operator ConsoleControllerInput(ConsoleKeyAction action)=>new(action);
}

internal sealed record ConsoleKeyboardPreview(bool Active,int Set,int? Left,int? Right,char? Character,
    double LeftX=0,double LeftY=0,double RightX=0,double RightY=0);
internal readonly record struct ConsoleGamepadAxis(int Code,int Value,int Minimum,int Maximum,int Flat=0);

// A positional alphabet: left is the high digit, right the low digit. Each set
// uses ceil(sqrt(character count)) slices, starting at up and moving clockwise.
internal sealed class ConsoleStickKeyboard
{
    internal static readonly string[] Alphabets=["abcdefghijklmnopqrstuvwxyz","ABCDEFGHIJKLMNOPQRSTUVWXYZ","0123456789",
        new string(Enumerable.Range(32,95).Select(n=>(char)n).Where(c=>!char.IsAsciiLetterOrDigit(c)).ToArray())];
    internal static readonly string[] Names=["Lowercase","Uppercase","Numbers","Symbols"];
    readonly Dictionary<int,ConsoleGamepadAxis> axes=[];
    readonly HashSet<int> pressed=[];
    readonly HashSet<int> triggerAxes=[];
    string? context;
    int set;
    bool held,suppressed;
    int? left,right;
    ConsoleKeyboardPreview? previous;
    ConsoleKeyAction pending;
    char? edit;
    bool cancel;
    public bool Editing=>context!=null;
    public ConsoleKeyboardPreview Hidden=>new(false,set,null,null,null);
    public int Slices=>(int)Math.Ceiling(Math.Sqrt(Alphabets[set].Length));
    bool TriggerHeld=>pressed.Contains(0x138) || pressed.Contains(0x139)
        || triggerAxes.Count>0;

    public void SetContext(string? value)
    {
        if(context==value)return;
        context=value;held=TriggerHeld;suppressed=held;left=right=null;previous=null;pending=ConsoleKeyAction.None;edit=null;cancel=false;
    }
    public void Synchronize(IEnumerable<int> buttons,IEnumerable<ConsoleGamepadAxis> values)
    {
        pressed.Clear();pressed.UnionWith(buttons);axes.Clear();triggerAxes.Clear();
        foreach(var axis in values)
        {
            axes[axis.Code]=axis;
            if(axis.Code is 2 or 5 or 0x14 or 0x15 && axis.Maximum>axis.Minimum
                && axis.Value>axis.Minimum+(axis.Maximum-(double)axis.Minimum)*.25)triggerAxes.Add(axis.Code);
        }
        held=TriggerHeld;suppressed=held;left=right=null;previous=null;pending=ConsoleKeyAction.None;edit=null;cancel=false;
    }
    public void SuppressGesture(){suppressed=true;left=right=null;previous=null;pending=ConsoleKeyAction.None;edit=null;cancel=false;}

    double Coordinate(int code)
    {
        if(!axes.TryGetValue(code,out var axis) || axis.Maximum<=axis.Minimum)return 0;
        return Math.Clamp((axis.Value-(axis.Minimum+(double)axis.Maximum)/2)/((axis.Maximum-(double)axis.Minimum)/2),-1,1);
    }
    int? Slice(int xCode,int yCode,int? old)
    {
        if(!axes.ContainsKey(xCode) || !axes.ContainsKey(yCode))return null;
        var x=Coordinate(xCode);var y=Coordinate(yCode);
        var flat=new[]{axes[xCode],axes[yCode]}.Max(a=>a.Maximum>a.Minimum?2.0*a.Flat/(a.Maximum-(double)a.Minimum):1);
        if(Math.Sqrt(x*x+y*y)<Math.Max(old.HasValue ? .25 : .4,flat))return null;
        var angle=(Math.Atan2(x,-y)*180/Math.PI+360)%360;var width=360.0/Slices;
        if(old.HasValue)
        {
            var distance=Math.Abs((angle-old.Value*width+540)%360-180);
            if(distance<width/2+4)return old;
        }
        return (int)Math.Floor((angle+width/2)/width)%Slices;
    }
    ConsoleKeyboardPreview Preview(bool active)
    {
        if(active){left=Slice(0,1,left);right=Slice(3,4,right);}
        else left=right=null;
        var index=left.HasValue && right.HasValue?left.Value*Slices+right.Value:-1;
        // Quantize visual positions to terminal-cell precision, independently of
        // slice hysteresis. Thumb motion can move a dot without changing a letter.
        double Dot(int code)=>active?Math.Round(Coordinate(code)*10)/10:0;
        return new(active,set,left,right,index>=0 && index<Alphabets[set].Length?Alphabets[set][index]:null,
            Dot(0),Dot(1),Dot(3),Dot(4));
    }

    public ConsoleControllerInput Read(ushort type,ushort code,int value)
    {
        if(type==3 && axes.TryGetValue(code,out var axis))
        {
            axes[code]=axis with{Value=value};
            if(code is 2 or 5 or 0x14 or 0x15 && axis.Maximum>axis.Minimum)
            {
                var position=(value-(double)axis.Minimum)/(axis.Maximum-(double)axis.Minimum);
                if(position>=.45)triggerAxes.Add(code);else if(position<=.15)triggerAxes.Remove(code);
            }
        }
        if(type==1 && value is 0 or 1)
        {
            var fresh=value==1?pressed.Add(code):!pressed.Remove(code);
            if(Editing && value==1 && fresh)
                switch(code)
                {
                    case 0x136: set=(set+Alphabets.Length-1)%Alphabets.Length;left=right=null;break;
                    case 0x137: set=(set+1)%Alphabets.Length;left=right=null;break;
                    case 0x134: edit='\b';cancel=held||TriggerHeld;break; // X
                    case 0x133: edit=' ';cancel=held||TriggerHeld;break; // Y
                    case 0x130: if(!held && !TriggerHeld)pending=ConsoleKeyAction.Enter;break;
                    case 0x131: if(held || TriggerHeld)cancel=true;else pending=ConsoleKeyAction.Back;break;
                }
        }
        if(type!=0 || code!=0 || !Editing)return default; // Commit only complete SYN_REPORT packets.
        var now=TriggerHeld;
        if(cancel)suppressed=true;
        var preview=Preview((now||held) && !suppressed);
        char? character=edit;
        if(held && !now && !suppressed && character==null)character=preview.Character;
        if(!now){suppressed=false;left=right=null;preview=Hidden;}
        var changed=previous!=preview;previous=preview;held=now;
        var result=new ConsoleControllerInput(pending,character,changed?preview:null);
        pending=ConsoleKeyAction.None;edit=null;cancel=false;
        return result;
    }

}
