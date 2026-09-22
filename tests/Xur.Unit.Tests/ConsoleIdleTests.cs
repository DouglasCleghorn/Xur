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
        var clock=new Clock();var idle=new ConsoleIdle(clock);
        clock.Ticks=TimeSpan.FromMinutes(5).Ticks-1;check(!idle.IsBlank,"Console remains visible before the five-minute idle deadline");
        clock.Ticks++;check(idle.IsBlank,"Console blanks at five minutes without keyboard activity");
        var frame=LocalConsole.BlankFrame(160,45);
        check(LocalConsole.Clean(frame).All(c=>c==' ')&&frame.Contains("\x1b[45;1H\x1b[0;30;40m"),"Idle frame paints every display row black without static text or a cursor");
        check(ReferenceEquals(frame,LocalConsole.BlankFrame(160,45))&&idle.IsBlank,"Background frame polling cannot wake the console and reuses its black frame");
        check(idle.Touch()&&!idle.IsBlank,"The first key after idle is consumed to wake the screen");
        check(!idle.Touch(),"A subsequent key can perform the selected action");
        clock.Ticks+=TimeSpan.FromMinutes(5).Ticks;check(idle.IsBlank,"The console blanks again after another idle interval");
    }
}
