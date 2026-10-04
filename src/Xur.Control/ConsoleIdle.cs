namespace Xur.Control;

// Only user input counts as activity; polling, logs and network changes do not.
public sealed class ConsoleIdle(TimeProvider? clock=null,TimeSpan? timeout=null,string? statePath=null)
{
    readonly TimeProvider time=clock??TimeProvider.System;
    readonly TimeSpan delay=timeout??TimeSpan.FromMinutes(10);
    long lastInput=(clock??TimeProvider.System).GetTimestamp();
    string? path=statePath;
    bool? published;
    public bool IsBlank
    {
        get{var idle=time.GetElapsedTime(lastInput)>=delay;Publish(idle);return idle;}
    }
    // The root-private marker tells display recovery that DPMS Off is intentional.
    void Publish(bool idle)
    {
        if(path==null||published==idle)return;
        if(idle)File.WriteAllText(path,"");else File.Delete(path);
        published=idle;
    }
    public void ConfigureState(string stateFile)
    {path=stateFile;published=null;lastInput=time.GetTimestamp();Publish(false);}
    public bool Touch(){var wasBlank=IsBlank;lastInput=time.GetTimestamp();Publish(false);return wasBlank;}
}
