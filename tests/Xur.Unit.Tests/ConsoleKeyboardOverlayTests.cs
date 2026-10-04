using System.Text.RegularExpressions;
using Xur.Control;

static class ConsoleKeyboardOverlayTests
{
    sealed class Clock:TimeProvider
    {
        public long Ticks;
        public override long GetTimestamp()=>Ticks;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public void Advance(int milliseconds)=>Ticks+=TimeSpan.FromMilliseconds(milliseconds).Ticks;
    }
    static readonly ConsoleKeyboardPreview Choice=new(true,0,0,2,'c',0,-.9,.8,.45);
    static ConsoleScreen Field(bool secret=false)=>new("overlay-fixture","Server name","Enter a name, then choose Save.",[new('s',"Save"),new('0',"Cancel")],"",secret);
    public static void Run(Action<bool,string> check)
    {
        var clock=new Clock();using var scope=LocalConsole.UseKeyboardClock(clock);
        LocalConsole.OpenMaintenance(Field());LocalConsole.EditText('x');var revision=LocalConsole.DiagnosticSnapshot().Revision;
        LocalConsole.SetControllerPreview(Choice);
        var before=LocalConsole.ExportFrame(100,40);var screen=new Screen(100,40);screen.Apply(before);
        check(screen.Text.Contains("Preview: c") && screen.Text.Contains("Text: x") && screen.Text.Contains("[abcdef]") && screen.Text.Contains("[c]"),"The overlay shows entered text, the selected group and its previewed letter on linked wheels");
        check(before.Contains("\x1b[0;36;44m") && before.Contains("\x1b[0;2m") && screen.Text.Contains('●'),"Selected wheel sectors and stick dots stand out over a dimmed menu");
        check(revision==LocalConsole.DiagnosticSnapshot().Revision,"Moving a local character preview cannot change diagnostic content or its revision");
        clock.Advance(5000);check(LocalConsole.ExportFrame(100,40).Contains("Preview: c"),"Holding a trigger keeps the overlay visible without a feedback timeout");

        foreach(var (width,height) in new[]{(40,12),(40,20),(60,20),(80,25),(100,40),(240,120)})
        {
            var compact=new Screen(width,height);compact.Apply(LocalConsole.ExportFrame(width,height));
            check(compact.InBounds && compact.Text.Contains("Preview: c") && compact.Text.Contains("Text: x") && compact.Text.Contains("cancel"),"The controller overlay keeps candidate, text and cancellation visible within "+width+"x"+height+" bounds");
        }
        // Match the native client's column-1 row comparison with an unchanged
        // overlay and changed background; incremental painting must be complete.
        LocalConsole.OpenMaintenance(Field() with{Body="Background content changed."},refreshOnly:true);
        var after=LocalConsole.ExportFrame(100,40);
        var oldRows=Chunks(before);var newRows=Chunks(after);
        for(var i=0;i<newRows.Length;i++)if(newRows[i]!=oldRows[i])screen.Apply(newRows[i]);
        var complete=new Screen(100,40);complete.Apply(after);
        check(screen.Text==complete.Text && screen.Text.Contains("Preview: c"),"Native changed-row painting preserves an unchanged overlay during background refreshes");

        LocalConsole.ApplyControllerInput(new(Character:'c',Preview:new(false,0,null,null,null)));
        check(LocalConsole.TextValue=="xc" && LocalConsole.ExportFrame(100,40).Contains("Typed: c"),"Release applies the character and commit feedback together, exactly once");
        var typedRevision=LocalConsole.DiagnosticSnapshot().Revision;clock.Advance(499);
        check(LocalConsole.ExportFrame(100,40).Contains("Typed: c"),"Commit feedback remains visible for half a second");
        clock.Advance(1);check(!LocalConsole.ExportFrame(100,40).Contains("Typed: c") && LocalConsole.ExportFrame(100,40).Contains("Hold LT/RT to choose"),"Commit feedback expires while the wheels remain available for the next gesture");
        clock.Advance(699);check(LocalConsole.ExportFrame(100,40).Contains("Preview:"),"The overlay stays open between quick typing gestures");
        clock.Advance(1);check(!LocalConsole.ExportFrame(100,40).Contains("Preview:") && typedRevision==LocalConsole.DiagnosticSnapshot().Revision,"Overlay expiration restores the menu without changing text or diagnostic revision");
        LocalConsole.SetControllerPreview(Choice);LocalConsole.ApplyControllerInput(new(Character:'c',Preview:new(false,0,null,null,null)));clock.Advance(200);
        LocalConsole.SetControllerPreview(Choice with{Right=0,Character='a',RightX=0,RightY=-.9});
        check(LocalConsole.ExportFrame(100,40).Contains("Preview: a") && !LocalConsole.ExportFrame(100,40).Contains("Typed:"),"A fresh hold replaces commit feedback immediately with the next candidate");
        LocalConsole.SetControllerPreview(null);check(!LocalConsole.ExportFrame(100,40).Contains("Preview:"),"Controller removal clears the overlay immediately instead of waiting for its linger deadline");
        LocalConsole.SetControllerPreview(Choice);LocalConsole.EditText('k');check(!LocalConsole.ExportFrame(100,40).Contains("Preview:"),"Ordinary keyboard entry dismisses the controller overlay and continues editing");
        LocalConsole.SetControllerPreview(Choice);LocalConsole.OpenMaintenance(Field() with{Id="different-field"});
        check(!LocalConsole.ExportFrame(100,40).Contains("Preview:"),"Changing fields clears held and lingering overlay state");
        LocalConsole.OpenMaintenance(Field() with{InputValue="c"});LocalConsole.SetControllerPreview(Choice);
        LocalConsole.ApplyControllerInput(new(Character:'c',Preview:new(false,0,null,null,null)));
        check(LocalConsole.TextValue=="c" && LocalConsole.ExportFrame(100,40).Contains("Typed: c"),"Replacing prefilled text with the same character still acknowledges the accepted controller gesture");
        LocalConsole.OpenMaintenance(Field() with{Id="long-field",InputValue=new string('x',100)+"tail"});LocalConsole.SetControllerPreview(Choice);
        var longField=new Screen(40,12);longField.Apply(LocalConsole.ExportFrame(40,12));
        check(longField.InBounds && longField.Text.Contains("Text: …") && longField.Text.Contains("tail"),"Compact overlays keep the insertion end of long text visible without overflowing");

        LocalConsole.OpenMaintenance(Field(secret:true));LocalConsole.EditText('q');LocalConsole.SetControllerPreview(Choice);
        LocalConsole.ApplyControllerInput(new(Character:'c',Preview:new(false,0,null,null,null)));
        var secretFrame=LocalConsole.ExportFrame(100,40);var secretScreen=new Screen(100,40);secretScreen.Apply(secretFrame);
        var secretSnapshot=LocalConsole.DiagnosticSnapshot();
        check(secretScreen.Text.Contains("Text: **") && secretScreen.Text.Contains("Typed: *") && !secretFrame.Contains("Typed: c") && !secretFrame.Contains("qc"),"Entered passwords and commit feedback remain masked in every terminal frame");
        check(!secretScreen.Text.Contains("[c]") && !secretScreen.Text.Contains("[abcdef]"),"Password commit feedback also clears wheel highlights that could reveal the last typed character");
        check(secretSnapshot.InputValue==null && !secretSnapshot.Body.Contains("Typed:") && !secretSnapshot.Body.Contains("Preview:"),"Diagnostics exclude both password content and transient overlay feedback");
        LocalConsole.ApplyControllerInput(new(Character:'\b'));var deleted=LocalConsole.ExportFrame(100,40);
        check(deleted.Contains("Deleted") && !deleted.Contains('\b'),"Controller deletion displays feedback without emitting terminal control characters");
        LocalConsole.ClearText();check(!LocalConsole.ExportFrame(100,40).Contains("Deleted"),"Clearing a field also clears controller feedback");

        for(var set=0;set<4;set++)
        {
            LocalConsole.SetControllerPreview(Choice with{Set=set,Left=0,Right=0,Character=ConsoleStickKeyboard.Alphabets[set][0]});
            var alphabetFrame=new Screen(100,40);alphabetFrame.Apply(LocalConsole.ExportFrame(100,40));
            check(alphabetFrame.Text.Contains("["+ConsoleStickKeyboard.Names[set]+"]") && alphabetFrame.InBounds,"Letter, number and symbol overlays identify the active set within display bounds");
        }
        LocalConsole.SetControllerPreview(null);
    }
    static string[] Chunks(string frame)
    {
        var starts=Regex.Matches(frame,"\x1b\\[\\d+;1H");
        return starts.Select((start,i)=>frame[start.Index..(i+1<starts.Count?starts[i+1].Index:frame.Length)]).ToArray();
    }
    // Interpret cursor-positioned terminal frames, including opaque overwrite,
    // rather than checking strings hidden behind the overlay's background.
    sealed class Screen(int width,int height)
    {
        readonly char[,] cells=new char[height,width];int x,y;
        public bool InBounds=true;
        public string Text=>string.Join('\n',Enumerable.Range(0,height).Select(row=>new string(Enumerable.Range(0,width).Select(col=>cells[row,col]=='\0'?' ':cells[row,col]).ToArray())));
        public void Apply(string frame)
        {
            for(var i=0;i<frame.Length;)
            {
                if(frame[i]=='\x1b')
                {
                    var escape=Regex.Match(frame[i..],"^\x1b(?:\\[([0-?]*)[ -/]*([@-~])|%G)");
                    if(!escape.Success)throw new Exception("Unknown terminal escape in overlay");
                    if(escape.Groups[2].Value=="H")
                    {
                        var parts=escape.Groups[1].Value.Split(';');y=(parts[0].Length==0?1:int.Parse(parts[0]))-1;
                        x=(parts.Length<2?1:int.Parse(parts[1]))-1;
                    }
                    i+=escape.Length;continue;
                }
                if(x<0 || x>=width || y<0 || y>=height)InBounds=false;else cells[y,x]=frame[i];
                x++;i++;
            }
        }
    }
    public static void Capture(string directory)
    {
        Directory.CreateDirectory(directory);var clock=new Clock();using var scope=LocalConsole.UseKeyboardClock(clock);
        LocalConsole.OpenMaintenance(Field());foreach(var c in "xur")LocalConsole.EditText(c);LocalConsole.SetControllerPreview(Choice);
        foreach(var (width,height) in new[]{(100,40),(80,25),(40,20),(40,12)})
            File.WriteAllText(Path.Combine(directory,$"preview-{width}x{height}.ansi"),LocalConsole.ExportFrame(width,height));
        LocalConsole.ApplyControllerInput(new(Character:'c',Preview:new(false,0,null,null,null)));
        File.WriteAllText(Path.Combine(directory,"typed-100x40.ansi"),LocalConsole.ExportFrame(100,40));
        LocalConsole.OpenMaintenance(Field(secret:true));LocalConsole.EditText('x');LocalConsole.SetControllerPreview(Choice);
        LocalConsole.ApplyControllerInput(new(Character:'c',Preview:new(false,0,null,null,null)));
        File.WriteAllText(Path.Combine(directory,"password-100x40.ansi"),LocalConsole.ExportFrame(100,40));
        LocalConsole.SetControllerPreview(null);
    }
}
