namespace Xur.Control;

// Only keyboard input counts as activity; polling, logs and network changes do not.
public sealed class ConsoleIdle(TimeProvider? clock=null,TimeSpan? timeout=null)
{
    readonly TimeProvider time=clock??TimeProvider.System;
    readonly TimeSpan delay=timeout??TimeSpan.FromMinutes(5);
    long lastInput=(clock??TimeProvider.System).GetTimestamp();
    public bool IsBlank=>time.GetElapsedTime(lastInput)>=delay;
    public bool Touch(){var wasBlank=IsBlank;lastInput=time.GetTimestamp();return wasBlank;}
}
