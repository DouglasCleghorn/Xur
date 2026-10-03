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
        }
        finally{Directory.Delete(root,true);}
    }
}
