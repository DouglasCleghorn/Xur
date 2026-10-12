using System.Threading.Channels;

namespace Xur.Robot;

public record HandCalibrationStart(string Mode, bool OperatorPresent=false, bool ArmsSupported=false, bool DisconnectReady=false);
public record HandCalibrationAdvance(string SessionId, string Phase, int Revision, bool Confirmed=false);
public record HandCalibrationHeartbeat(string SessionId);
public record HandCalibrationMotor(string Name, int Position, int Minimum, int Maximum, int Temperature, int Torque);
public record HandCalibrationProgress(string Phase, string Detail, bool CanAdvance, bool TorqueOffVerified, HandCalibrationMotor[] Motors);
public record HandCalibrationState(string? SessionId, string Mode, string Phase, int Revision, string Detail,
    bool CanAdvance, bool TorqueOffVerified, HandCalibrationMotor[] Motors, DateTimeOffset Updated, DateTimeOffset? LeaseUntil);

public interface IHandCalibrationAdapter
{
    Task Run(RoboticsConfiguration configuration, Action<HandCalibrationProgress> publish,
        ChannelReader<string> commands, CancellationToken cancellation);
}

// The UI sends phase acknowledgements only. All register access stays in the
// private adapter, under the same exclusive runtime job as other robot tools.
public sealed class HandCalibration(TimeProvider? time=null)
{
    readonly object sync=new();
    readonly TimeProvider clock=time??TimeProvider.System;
    Channel<string>? commands;
    bool pending;
    HandCalibrationState state=new(null,"hardware","idle",0,"Confirm the operator checks before hand-guided calibration.",false,false,[],DateTimeOffset.UtcNow,null);
    static bool Active(string phase)=>phase is not ("idle" or "completed" or "failed" or "stopped");
    public HandCalibrationState State(){lock(sync)return state;}
    public void Begin(HandCalibrationStart request)
    {
        lock(sync)
        {
            if(Active(state.Phase))throw new InvalidOperationException("A setup session is already active.");
            if(request.Mode!="hardware")throw new InvalidOperationException("Only hardware hand-guided calibration is supported.");
            if(!request.OperatorPresent||!request.ArmsSupported||!request.DisconnectReady)
                throw new InvalidOperationException("Be beside the robot, support both arms and keep motor-power disconnect within reach.");
            commands=Channel.CreateBounded<string>(new BoundedChannelOptions(1){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});pending=false;
            state=new(Guid.NewGuid().ToString("N"),request.Mode,"connecting",1,"Checking torque is off before hand-guided setup…",false,false,[],clock.GetUtcNow(),clock.GetUtcNow().AddSeconds(15));
        }
    }
    public void Heartbeat(HandCalibrationHeartbeat request)
    {
        lock(sync)
        {
            Match(request.SessionId);
            if(clock.GetUtcNow()>=state.LeaseUntil)throw new InvalidOperationException("Setup connection expired. Start a new session.");
            state=state with{LeaseUntil=clock.GetUtcNow().AddSeconds(15)};
        }
    }
    void Match(string id)
    {if(id!=state.SessionId||!Active(state.Phase))throw new InvalidOperationException("This setup session is no longer active.");}
    public void Advance(HandCalibrationAdvance request)
    {
        lock(sync)
        {
            Match(request.SessionId);
            if(clock.GetUtcNow()>=state.LeaseUntil||request.Revision!=state.Revision||request.Phase!=state.Phase||!state.CanAdvance||pending||!request.Confirmed)
                throw new InvalidOperationException("Review the current step and confirm it before continuing.");
            if(!commands!.Writer.TryWrite(request.Phase))throw new InvalidOperationException("Wait for the current setup step.");
            pending=true;state=state with{CanAdvance=false};
        }
    }
    void Publish(HandCalibrationProgress progress)
    {
        lock(sync)
        {
            if(progress.Phase is not ("left-reference" or "left-range" or "right-reference" or "right-range" or "review" or "completed"))throw new InvalidOperationException("Unexpected calibration phase.");
            string? problem=null;
            if(!progress.TorqueOffVerified)problem="The adapter did not verify torque off. Inspect motor power before restarting setup.";
            else if(progress.Motors.FirstOrDefault(m=>m.Torque!=0) is {} powered)
                problem=$"Torque-off check failed: {powered.Name} reported Torque_Enable={powered.Torque}. Support the arms and disconnect motor power.";
            else if(progress.Motors.FirstOrDefault(m=>m.Temperature is <0 or >=45) is {} hot)
                problem=$"Temperature check failed: {hot.Name} reported {hot.Temperature}°C. Setup requires every reading to be between 0°C and 44°C.";
            if(problem!=null)
            {
                // Keep the rejected sample visible for diagnosis without approving it.
                state=state with{Motors=progress.Motors,CanAdvance=false,TorqueOffVerified=false,Updated=clock.GetUtcNow()};
                throw new InvalidOperationException(problem);
            }
            var changed=progress.Phase!=state.Phase;
            if(changed)pending=false;
            state=state with{Phase=progress.Phase,Revision=state.Revision+(changed?1:0),Detail=progress.Detail,CanAdvance=progress.CanAdvance&&!pending,
                TorqueOffVerified=true,Motors=progress.Motors,Updated=clock.GetUtcNow(),LeaseUntil=progress.Phase=="completed"?null:state.LeaseUntil};
        }
    }
    public async Task Run(RoboticsConfiguration? configuration,IHandCalibrationAdapter adapter,CancellationToken token,Task? pendingObservation=null)
    {
        using var source=CancellationTokenSource.CreateLinkedTokenSource(token);
        async Task Watch()
        {
            while(!source.IsCancellationRequested)
            {
                await Task.Delay(250,source.Token);
                bool expired;lock(sync)expired=Active(state.Phase)&&clock.GetUtcNow()>=state.LeaseUntil;
                if(expired){source.Cancel();return;}
            }
        }
        var watcher=Watch();
        try
        {
            if(pendingObservation!=null)await pendingObservation.WaitAsync(source.Token);
            source.Token.ThrowIfCancellationRequested();
            await adapter.Run(configuration??throw new InvalidOperationException("Save hardware setup first."),Publish,commands!.Reader,source.Token);
            if(State().Phase!="completed")throw new InvalidOperationException("Calibration ended before review and save.");
        }
        catch(Exception error)
        {
            lock(sync)state=state with{Phase=error is OperationCanceledException?"stopped":"failed",Revision=state.Revision+1,CanAdvance=false,TorqueOffVerified=false,LeaseUntil=null,
                Detail=error is OperationCanceledException?"Setup stopped. Support the arms and inspect the robot before restarting.":Xur.Domain.Redaction.Logs(error.Message),Updated=clock.GetUtcNow()};
            throw;
        }
        finally
        {
            source.Cancel();commands!.Writer.TryComplete();
            try{await watcher;}catch(OperationCanceledException){}
        }
    }
}
