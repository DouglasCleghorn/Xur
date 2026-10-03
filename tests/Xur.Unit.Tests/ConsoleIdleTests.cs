using Xur.Control;
static class ConsoleIdleTests
{
    sealed class Clock:TimeProvider
    {
        public long Ticks;
        public override long GetTimestamp()=>Ticks;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
    }
    public static void Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../.build/evidence/console-idle-"+Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var state=Path.Combine(root,"console-sleep");File.WriteAllText(state,"");
            var clock=new Clock();var idle=new ConsoleIdle(clock,statePath:state);
            check(!idle.IsBlank&&!File.Exists(state),"Fresh console idle state clears a stale display-sleep marker");
            clock.Ticks=TimeSpan.FromMinutes(10).Ticks-1;check(!idle.IsBlank,"Console remains visible before the ten-minute idle deadline");
            clock.Ticks++;check(idle.IsBlank,"Console blanks at ten minutes without keyboard activity");
            check(File.Exists(state),"Idle console publishes intentional monitor sleep for display recovery");
            var frame=LocalConsole.BlankFrame(160,45);
            check(LocalConsole.Clean(frame).All(c=>c==' ')&&frame.Contains("\x1b[45;1H\x1b[0;30;40m"),"Idle frame paints every display row black without static text or a cursor");
            check(ReferenceEquals(frame,LocalConsole.BlankFrame(160,45))&&idle.IsBlank,"Background frame polling cannot wake the console and reuses its black frame");
            check(idle.Touch()&&!idle.IsBlank,"The first key after idle is consumed to wake the screen");
            check(!File.Exists(state),"Keyboard wake clears deliberate sleep before normal menu actions");
            check(!idle.Touch(),"A subsequent key can perform the selected action");
            clock.Ticks+=TimeSpan.FromMinutes(10).Ticks;check(idle.IsBlank,"The console blanks again after another idle interval");
            idle.ConfigureState(state);check(!idle.IsBlank&&!File.Exists(state),"Control startup resets the idle deadline and clears deliberate monitor sleep");

            // Exercise the frame endpoint and the same input gate used by the host,
            // rather than only the standalone deadline helper.
            clock=new Clock();using var idleClock=LocalConsole.UseIdleClock(clock);
            var address="192.0.2.10";
            var appliance=new Appliance(networkObserver:()=>[new("eno1","Up","Ethernet",[address])]);
            using var agent=appliance.Agent;var auth=new Bootstrap();LocalConsole.Status(appliance,auth);
            var keys=new ConsoleKeyReader();
            char? Input(char character)
            {
                var escaped=keys.InEscapeSequence || character=='\x1b';
                var action=keys.Read(character);char? typed=escaped?null:character;
                if(LocalConsole.ConsumeWakeInput(false,typed,action))return null;
                return action==ConsoleKeyAction.None?null:LocalConsole.Navigate(action);
            }
            bool Blank()=>LocalConsole.Clean(LocalConsole.ExportFrame(160,45)).All(c=>c==' ');
            var confirm=new ConsoleScreen("confirm","Restart server?","Choose Cancel or Restart.",[new('0',"Cancel"),new('y',"Restart")]);
            LocalConsole.OpenMaintenance(confirm);Input('2');
            var selection=LocalConsole.DiagnosticSnapshot().Selected;
            clock.Ticks+=TimeSpan.FromMinutes(10).Ticks;
            check(Blank(),"The native frame endpoint returns black at the idle deadline on a confirmation screen");
            address="192.0.2.11";LocalConsole.UpdateLogs("New background log line");LocalConsole.Refresh();
            LocalConsole.OpenMaintenance(confirm with {Body="Updated background status."},refreshOnly:true);
            check(Blank()&&LocalConsole.DiagnosticSnapshot().Selected==selection,"Log, address and maintenance refreshes cannot wake or change the idle selection");
            foreach(var character in "\x1b[?6c")Input(character);
            check(Blank(),"Terminal identification replies do not wake the display or select their embedded option number");
            check(Input('\r')==null&&!Blank()&&LocalConsole.DiagnosticSnapshot().Selected==selection,"The first Enter wakes the selected confirmation without returning its Restart action");
            check(Input('\r')=='y',"A second Enter can return the previously selected action after wake");

            LocalConsole.Status(appliance,auth);clock.Ticks+=TimeSpan.FromMinutes(10).Ticks;
            Input('5');check(!Blank()&&LocalConsole.DiagnosticSnapshot().Selected==0,"The first option-number key wakes without selecting Logs");
            Input('5');check(LocalConsole.DiagnosticSnapshot().Selected==4&&Input('\r')=='5',"The next option-number and Enter follow the normal Logs input path");
            LocalConsole.OpenLogs();clock.Ticks+=TimeSpan.FromMinutes(10).Ticks;
            LocalConsole.UpdateLogs("Newest log while asleep");LocalConsole.Refresh();check(Blank(),"New log output cannot repaint an idle Logs screen");
            Input('\x1b');
            check(!LocalConsole.ConsumeWakeInput(false,null,ConsoleKeyAction.None)&&Blank(),"An incomplete Escape sequence cannot bypass the idle gate");
            var escape=keys.FlushEscape();
            check(LocalConsole.ConsumeWakeInput(false,null,escape)&&!Blank(),"The completed first Escape wakes without leaving Logs");
            check(LocalConsole.ExportFrame(160,45).Contains("Newest log while asleep")&&LocalConsole.ExportFrame(160,45).Contains("> Back to menu"),"Wake reveals the latest full-screen Logs content and its Back action");
            check(Input('\r')=='0',"Enter on the awakened Logs screen returns Back to menu");
            LocalConsole.Status(appliance,auth);
            check(LocalConsole.ExportFrame(160,45).Contains("192.0.2.11:8443"),"Returning from Logs reveals the address learned while the display was idle");
            clock.Ticks+=TimeSpan.FromMinutes(10).Ticks;
            check(LocalConsole.ConsumeWakeInput(true,null,ConsoleKeyAction.None)&&!Blank(),"The first complete serial line also wakes without executing its command");
            LocalConsole.OpenQr(true);clock.Ticks+=TimeSpan.FromMinutes(10).Ticks;
            LocalConsole.AppendQr("Background enrollment output");LocalConsole.Refresh();
            check(Blank(),"Enrollment QR output and background refresh cannot wake the idle display");
            LocalConsole.EndQr("Background enrollment result");
            check(Blank(),"An enrollment result can update the pending screen while its physical frame remains black");
            check(LocalConsole.ConsumeWakeInput(false,'x',ConsoleKeyAction.None)&&LocalConsole.ExportFrame(160,45).Contains("Background enrollment result"),"A non-action key wakes to the latest enrollment result without invoking a menu choice");
        }
        finally{Directory.Delete(root,true);}
    }
}
