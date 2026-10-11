using Microsoft.AspNetCore.SignalR;

namespace Xur.Robot;

public record RobotSetupUpdate(RobotStatus Robot,HandCalibrationState Calibration);

// No callable hub methods: mutations remain strict, CSRF-protected HTTP tasks.
// Send a source-generated JSON string to keep the SignalR boundary AOT-safe.
public sealed class RobotSetupHub(RoboticsRuntime robot):Hub
{
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("setup",RobotJson.Serialize(new RobotSetupUpdate(robot.Status(),robot.HandCalibration())),Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }
}
public sealed class RobotSetupUpdates(IHubContext<RobotSetupHub> hub,RoboticsRuntime robot):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        while(await timer.WaitForNextTickAsync(stoppingToken))
            await hub.Clients.All.SendAsync("setup",RobotJson.Serialize(new RobotSetupUpdate(robot.Status(),robot.HandCalibration())),stoppingToken);
    }
}
