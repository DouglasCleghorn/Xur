using System.Threading.Channels;
using Xur.Robot;

static class HandCalibrationTests
{
    sealed class Clock:TimeProvider
    {public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
    sealed class Adapter:IHandCalibrationAdapter
    {
        public bool Called;
        public async Task Run(RoboticsConfiguration configuration,Action<HandCalibrationProgress> publish,ChannelReader<string> commands,CancellationToken token)
        {
            Called=true;
            foreach(var phase in new[]{"left-reference","left-range","right-reference","right-range","review"})
            {
                publish(new(phase,"Fixture torque off",true,true,[new("left_arm_shoulder_pan",2047,1800,2300,32,0)]));
                if(await commands.ReadAsync(token)!=phase)throw new Exception("Wrong fixture phase");
            }
            publish(new("completed","Fixture saved",false,true,[]));
        }
    }
    static bool Rejected(Action action){try{action();return false;}catch(InvalidOperationException){return true;}}
    static async Task Wait(Func<bool> done){for(var i=0;i<200&&!done();i++)await Task.Delay(10);if(!done())throw new Exception("Setup fixture timed out");}
    public static async Task Run(Action<bool,string> check)
    {
        var clock=new Clock();var adapter=new Adapter();var flow=new HandCalibration(clock);
        check(flow.State().Mode=="hardware","Guided calibration starts in hardware mode");
        check(Rejected(()=>flow.Begin(new("hardware"))),"Hardware setup requires three physical operator checks");
        check(Rejected(()=>flow.Begin(new("simulation",true,true,true))),"Removed practice setup cannot start through the API");
        check(Rejected(()=>flow.Begin(new("unknown",true,true,true))),"Unknown setup mode is rejected");
        flow.Begin(new("hardware",true,true,true));
        var run=flow.Run(new("fixture","l","r","","h","w",[]),adapter,CancellationToken.None);
        await Wait(()=>flow.State().Phase=="left-reference");
        var current=flow.State();
        check(adapter.Called&&current.TorqueOffVerified,"Guided setup uses adapter feedback");
        check(Rejected(()=>flow.Advance(new(current.SessionId!,current.Phase,current.Revision,false))),"Every setup step requires explicit confirmation");
        check(Rejected(()=>flow.Advance(new("other",current.Phase,current.Revision,true))),"A different page session cannot advance setup");
        flow.Advance(new(current.SessionId!,current.Phase,current.Revision,true));
        check(Rejected(()=>flow.Advance(new(current.SessionId!,current.Phase,current.Revision,true))),"Duplicate step submissions are rejected");
        await Wait(()=>flow.State().Phase=="left-range");
        check(Rejected(()=>flow.Advance(new(current.SessionId!,current.Phase,current.Revision,true))),"Old phase acknowledgements cannot advance a new step");
        foreach(var phase in new[]{"left-range","right-reference","right-range","review"})
        {
            await Wait(()=>flow.State().Phase==phase&&flow.State().CanAdvance);current=flow.State();
            flow.Heartbeat(new(current.SessionId!));flow.Advance(new(current.SessionId!,current.Phase,current.Revision,true));
        }
        await run;
        check(flow.State().Phase=="completed"&&!flow.State().CanAdvance,"Adapter completion finishes the guided flow");
        check(Rejected(()=>flow.Heartbeat(new(current.SessionId!))),"Completed session cannot renew or replay commands");
        flow.Begin(new("hardware",true,true,true));run=flow.Run(new("fixture","l","r","","h","w",[]),adapter,CancellationToken.None);
        await Wait(()=>flow.State().Phase=="left-reference");clock.Now=clock.Now.AddSeconds(16);
        check(Rejected(()=>flow.Heartbeat(new(flow.State().SessionId!))),"An expired setup lease cannot be revived");
        try{await run;}catch(OperationCanceledException){}
        check(flow.State().Phase=="stopped"&&!flow.State().TorqueOffVerified,"Hardware setup cancels after heartbeat loss and clears torque verification");
    }
}
