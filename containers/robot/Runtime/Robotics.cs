namespace Xur.Robot;

// Public requests describe tasks and reviewed skills, never motor targets.
public record RobotSkill(string Id, string Name, string Kind, string Arm,
    string Task, string AssetPath, bool Verified = false, string PolicyType = "act",
    string Bin = "", int Episode = 0, int Seconds = 15, string CalibrationHash = "");
public record RoboticsConfiguration(string RobotId, string LeftPort, string RightPort,
    string ControllerDevice, string HeadCamera, string HandCamera,
    RobotSkill[] Skills, bool MotionEnabled = false, RobotMotorLimits? MotorLimits = null);
public record RobotMotorLimits(int MaxLoadRaw,int MaxCurrentRaw,double MaxFollowingErrorDegrees);
public record RobotArmRequest(int Seconds = 60);
public record RobotControllerRequest(int Seconds = 60);
public record RobotCalibrationRequest();
public record RobotResetStopRequest();
public record RobotEmoteRequest(string Skill);
public record RobotSortItem(string Object, string Bin, string Skill);
public record RobotTaskRequest(string Kind, string Instructions = "", RobotSortItem[]? Items = null);
public record RobotRecordRequest(string Dataset, string Arm, string Task, int Seconds = 30);
public record RobotTrainRequest(string Dataset, string Policy, int Steps = 1000);
public record RobotSkillReview(RobotSkill Skill);
public record RobotDevice(string Path, string Name);
public record RobotDevices(RobotDevice[] Ports, RobotDevice[] Controllers, RobotDevice[] Cameras);
public record RobotBusInventory(string Port, Dictionary<int,int> Motors);
public record RobotBusDetection(string Adapter, string LeftPort, string RightPort, RobotBusInventory[] Buses)
{
    public static RobotBusDetection MatchXLeRobot(RobotBusInventory[] buses)
    {
        // This signature belongs to the pinned three-omniwheel upstream model.
        // It cannot distinguish modified wiring with the same IDs/models.
        static bool Matches(RobotBusInventory bus,int count)=>bus.Motors.Count==count
            && Enumerable.Range(1,count).All(id=>bus.Motors.GetValueOrDefault(id)==777);
        if(buses.Length!=2 || buses.Select(b=>b.Port).Distinct(StringComparer.Ordinal).Count()!=2)
            throw new InvalidOperationException("Expected two distinct motor buses.");
        var left=buses.Where(b=>Matches(b,8)).ToArray();var right=buses.Where(b=>Matches(b,9)).ToArray();
        if(left.Length!=1 || right.Length!=1)
            throw new InvalidOperationException("Motor inventories do not uniquely match the upstream XLeRobot: eight STS3215 motors on the left/head bus and nine on the right/wheel bus. Check power, wiring and model; detection does not guess from missing responses.");
        return new("xlerobot-three-omniwheels",left[0].Port,right[0].Port,buses);
    }
}
public record RobotJob(string Id, string Kind, string State, string Detail,
    DateTimeOffset Created, DateTimeOffset Updated, string[] CompletedSkills, RobotTaskRequest? Request = null);
public record RobotStatus(string? WorkloadId, string Mode, bool Configured,
    bool MotionEnabled, DateTimeOffset? ArmedUntil, bool StopLatched,
    RobotJob? Job, RobotSkill[] Skills, string[] Problems, string? CalibrationHash = null,bool EmergencyStopLatched=false);
public record RobotCalibrationAssessment(DateTimeOffset ObservedAt,string State,string Summary,
    string[] Blockers,bool MotorCommandsIssued=false,bool JointCalibrationApproved=false);
