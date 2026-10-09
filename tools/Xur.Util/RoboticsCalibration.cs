using Xur.IO;

namespace Xur.Util;

public static class RoboticsCalibration
{
    public static readonly string[] IdentificationJoints = [
        "left-shoulder-pan", "left-shoulder-lift", "left-elbow-flex", "left-wrist-flex", "left-wrist-roll", "left-gripper",
        "right-shoulder-pan", "right-shoulder-lift", "right-elbow-flex", "right-wrist-flex", "right-wrist-roll", "right-gripper",
        "head-pan", "head-tilt"];

    public static int Identify(string joint)
    {
        if(!IdentificationJoints.Contains(joint,StringComparer.Ordinal))throw new UserError("Choose one of the named arm/head joints; wheel identification is disabled.");
        if(Linux.EffectiveUser()!=0)throw new UserError("Root privileges are required to use Xur's prepared robotics container.");
        if(Console.IsInputRedirected)throw new UserError("Motor identification requires an operator on an interactive terminal beside the robot with immediate access to motor power.");
        Console.WriteLine("Testing one expected arm/head motor with a 2.1-degree target and 4% drive-output cap. Keep arms and tags untouched, with clear space and immediate access to cut motor power. This does not approve calibration.");
        return TerminalProcess.Execute("/usr/bin/podman",["exec","--interactive","--tty","xur-robotics-tools","python","/opt/xur/motor_probe.py",joint,"--operator-present"]);
    }

    public static int Run()
    {
        if(Linux.EffectiveUser()!=0)throw new UserError("Root privileges are required to use Xur's prepared robotics container.");
        if(Console.IsInputRedirected)throw new UserError("Calibration requires an operator on an interactive terminal beside the robot, supporting both arms.");
        Console.WriteLine("Starting XLeRobot's hand-guided calibration. The routine records joint ranges with torque disabled; it does not drive a search for hard stops.");
        return TerminalProcess.Execute("/usr/bin/podman",["exec","--interactive","--tty","xur-robotics-tools","python","/opt/xur/bridge.py","--calibrate"]);
    }
}
