using System.Text.Json;

namespace Xur.Robot;

public sealed class RoboticsRuntime
{
    readonly object sync = new();
    readonly string directory, reservation;
    readonly IRobotTools tools;
    readonly RobotMetrology metrology;
    readonly TimeProvider clock;
    readonly RoboticsContainer container;
    readonly Func<RobotDevices> deviceInventory;
    readonly RecordingBackups backups;
    readonly RobotCameraCapabilities cameraCapabilities;
    readonly Dictionary<string, RobotJob> jobs = new();
    readonly RobotMotorDiagnostics motorDiagnostics=new();
    string? workloadId;
    RoboticsConfiguration? configuration;
    CancellationTokenSource? cancellation;
    Task? worker;
    Task<RobotStatus>? stopWorker;
    string? activeJob;
    string? armedCalibrationHash, recoveryProblem, stopProblem;
    bool stopping;
    DateTimeOffset? armedUntil;
    bool stopLatched = true;
    string[] configurationProblems = [];
    JsonElement? observation;
    DateTimeOffset? observationReadAt;
    RobotCalibrationAssessment? calibrationAssessment;
    bool observing;
    string? observationProblem;
    bool emergencyStop;
    long emergencyStopEpoch;
    string EmergencyStopPath=>Path.Combine(directory,"estop-latched");

    public RoboticsRuntime(string directory, string reservation, IRobotTools? tools = null, TimeProvider? clock = null,RoboticsContainer? toolContainer=null,Func<RobotDevices>? deviceInventory=null,RecordingBackups? backups=null,RobotMetrology? metrology=null,RobotCameraCapabilities? cameraCapabilities=null)
    {this.directory=directory; this.reservation=reservation;this.backups=backups??new RecordingBackups(directory);this.metrology=metrology??new(directory);this.cameraCapabilities=cameraCapabilities??new(); this.tools=tools??new LeRobotTools(); this.clock=clock??TimeProvider.System;container=toolContainer??new(directory);this.deviceInventory=deviceInventory??Devices;emergencyStop=File.Exists(EmergencyStopPath);}

    public RobotStatus Status()
    {
        lock(sync)
        {
            var armed = !stopLatched && armedUntil > clock.GetUtcNow();
            return new(workloadId, emergencyStop?"emergency-stop":activeJob!=null?jobs[activeJob].Kind:armed?"armed":"disarmed",
                configuration!=null, configuration?.MotionEnabled==true, armed?armedUntil:null, stopLatched,
                activeJob!=null?jobs[activeJob]:jobs.Values.LastOrDefault(), configuration?.Skills??[], configurationProblems.Concat(recoveryProblem==null?[]:[recoveryProblem]).Concat(stopProblem==null?[]:[stopProblem]).Concat(emergencyStop?["E-stop is latched. Inspect the robot, then reset E-stop; reset leaves motion disarmed."]:[]).Concat(configuration==null?[]:CalibrationProblems(configuration)).ToArray(),CalibrationHash(),emergencyStop);
        }
    }
    public RobotJob[] Jobs(){lock(sync)return jobs.Values.Reverse().ToArray();}
    public RobotJob? Job(string id){lock(sync)return jobs.GetValueOrDefault(id);}
    public RoboticsConfiguration? Configuration(){lock(sync)return configuration;}
    public RobotCalibrationAssessment? CalibrationAssessment(){lock(sync)return calibrationAssessment;}
    public RobotMetrologySettings? Metrology(){lock(sync)return metrology.Read();}
    public RobotMetrologySettings ConfigureMetrology(RobotMetrologySettings settings)
    {
        lock(sync)
        {
            RequireLoaded();Idle();if(!stopLatched)throw new InvalidOperationException("Stop and disarm before changing camera metrology.");
            var saved=metrology.Save(settings);calibrationAssessment=null;return saved;
        }
    }
    public async Task Recover()
    {
        // A restarted app must stop any surviving local tool before releasing
        // its controller reservation. Recovery never resumes motor motion.
        if(tools is not LeRobotTools || !File.Exists(Path.Combine(directory,".build","devices.json")))return;
        try{await container.Interrupt();File.Delete(reservation);}
        catch(Exception e){lock(sync)recoveryProblem="Previous robot tool could not be stopped. Inspect motor power before motion: "+Xur.Domain.Redaction.Logs(e.Message);}
    }
    string? CalibrationHash()
    {
        var path=Path.Combine(directory,"calibration","receipt.json");
        return File.Exists(path)?Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))):null;
    }
    public void Start(string id)
    {
        lock(sync)
        {
            if(workloadId!=null)throw new InvalidOperationException("The robot app is already active.");
            if(worker is {IsCompleted:false})throw new InvalidOperationException("Wait for the previous robot operation to stop.");
            ReadConfiguration();observation=null;observationReadAt=null;observationProblem=null;calibrationAssessment=null;motorDiagnostics.Clear();
            emergencyStop=File.Exists(EmergencyStopPath);workloadId=id;stopLatched=true;armedUntil=null;
        }
    }
    void ReadConfiguration()
    {
        configuration=null; configurationProblems=[];
        var path=Path.Combine(directory,"config.json");
        if(!File.Exists(path)){configurationProblems=["Open Robot setup and select the connected hardware before using it."]; return;}
        try
        {
            var selected=RobotJson.Deserialize<RoboticsConfiguration>(File.ReadAllText(path))??throw new InvalidOperationException("Empty robotics configuration.");
            ValidateConfiguration(selected); configuration=selected;
        }
        catch(Exception e) when(e is JsonException or IOException or InvalidOperationException)
        {configurationProblems=["Robotics configuration: "+e.Message];}
    }
    public static void ValidateConfiguration(RoboticsConfiguration c)
    {
        static bool PathValue(string? value)=>value!=null && Path.IsPathFullyQualified(value) && !value.Any(char.IsControl);
        static bool Match(string? value,string pattern)=>value!=null&&System.Text.RegularExpressions.Regex.IsMatch(value,pattern);
        if(!RobotIdentifiers.Valid(c.RobotId)
            || new[]{c.LeftPort,c.RightPort}.Any(p=>!Match(p,@"\A/dev/serial/by-id/[A-Za-z0-9._:+-]+\z"))
            || c.ControllerDevice!="" && !Match(c.ControllerDevice,@"\A/dev/input/(by-id/[A-Za-z0-9._:+-]+-event-joystick|event[0-9]+)\z")
            || new[]{c.HeadCamera,c.HandCamera}.Any(p=>!RobotCameraDevices.Valid(p))
            || c.LeftPort==c.RightPort || RobotCameraDevices.SameNode(c.HeadCamera,c.HandCamera) || c.Skills==null || c.Skills.Length>32)
            throw new InvalidOperationException("Use inventory paths: serial/by-id buses, an optional controller event device and two distinct v4l/by-id or v4l/by-path camera interfaces.");
        if(c.MotorLimits is {} limits && (limits.MaxLoadRaw is <1 or >1023 || limits.MaxCurrentRaw is <1 or >65535
            || !double.IsFinite(limits.MaxFollowingErrorDegrees) || limits.MaxFollowingErrorDegrees is <0.1 or >3))
            throw new InvalidOperationException("Provide valid robot-specific load/current limits and a following error between 0.1 and 3 degrees.");
        if(c.Skills.Any(s=>s==null))throw new InvalidOperationException("Provide valid skills.");
        if(c.Skills.Select(s=>s.Id).Distinct(StringComparer.Ordinal).Count()!=c.Skills.Length)throw new InvalidOperationException("Skill IDs must be unique.");
        foreach(var skill in c.Skills)
            if(!RobotIdentifiers.Valid(skill.Id)||string.IsNullOrWhiteSpace(skill.Name)||skill.Name.Length>80||skill.Kind is not ("sort" or "emote")||skill.Arm is not ("left" or "right")
                || !PathValue(skill.AssetPath)||!skill.AssetPath.StartsWith(skill.Kind=="sort"?"/state/policies/":"/state/datasets/",StringComparison.Ordinal)||skill.AssetPath.Split('/').Contains("..")
                ||string.IsNullOrWhiteSpace(skill.Task)||skill.Task.Length>500||skill.Seconds is <1 or >60||skill.Episode<0
                || skill.PolicyType!="act"||skill.Kind=="sort"&&!RobotIdentifiers.Valid(skill.Bin))
                throw new InvalidOperationException("Configure bounded local LeRobot policies for sorting and recorded single-arm datasets for emotes.");
    }
    void RequireLoaded(){if(workloadId==null)throw new InvalidOperationException("Load the Robotics profile first.");}
    RoboticsConfiguration RequireConfiguration()
    {RequireLoaded();return configuration??throw new InvalidOperationException(string.Join(" ",configurationProblems));}
    void Idle(){if(activeJob!=null||stopping||observing)throw new InvalidOperationException("Stop or finish the current robot operation first.");}
    void RequireController()
    {if(string.IsNullOrEmpty(RequireConfiguration().ControllerDevice))throw new InvalidOperationException("Select a connected controller and prepare the tools container before controller motion or recording.");}
    void Motion()
    {
        if(emergencyStop)throw new InvalidOperationException("E-stop is latched. Inspect and reset it before arming.");
        if(RequireConfiguration().MotionEnabled!=true)throw new InvalidOperationException("Hardware motion is disabled in the local robot configuration.");
        if(stopLatched || armedUntil<=clock.GetUtcNow() || armedUntil==null)throw new InvalidOperationException("An operator must arm the robot for a short session first.");
        if(configuration!.MotorLimits==null)throw new InvalidOperationException("Establish and configure motor load/current and following-error limits before motion.");
        if(recoveryProblem!=null)throw new InvalidOperationException(recoveryProblem);
        if(stopProblem!=null)throw new InvalidOperationException(stopProblem);
        if(armedCalibrationHash!=CalibrationHash()){stopLatched=true;armedUntil=null;throw new InvalidOperationException("Calibration changed during the armed session; inspect the robot and rearm.");}
        var problems=CalibrationProblems(configuration!);if(problems.Length!=0)throw new InvalidOperationException(string.Join(" ",problems));
    }
    string[] CalibrationProblems(RoboticsConfiguration c)
    {
        var problems=new List<string>();
        var receipt=Path.Combine(directory,"calibration","receipt.json");
        try
        {
            using var data=JsonDocument.Parse(File.ReadAllText(receipt));var root=data.RootElement;
            if(root.GetProperty("robotId").GetString()!=c.RobotId||root.GetProperty("leftPort").GetString()!=c.LeftPort||root.GetProperty("rightPort").GetString()!=c.RightPort)
                throw new InvalidOperationException("Robot identity or serial bus assignment changed.");
            foreach(var entry in root.GetProperty("files").EnumerateObject())
            {
                if(entry.Name!=c.RobotId+".json"&&entry.Name!=c.RobotId+"-left.json"&&entry.Name!=c.RobotId+"-right.json")throw new JsonException();
                var hash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(directory,"calibration",entry.Name))));
                if(hash!=entry.Value.GetString())throw new InvalidOperationException("Calibration changed since it was checked.");
            }
            if(root.GetProperty("files").EnumerateObject().Count()!=3)throw new JsonException();
        }
        catch(Exception e) when(e is JsonException or IOException or InvalidOperationException or KeyNotFoundException)
        {problems.Add("Calibration receipt is missing or differs from this robot's setup.");}
        foreach(var name in new[]{c.RobotId,c.RobotId+"-left",c.RobotId+"-right"})
        {
            var file=Path.Combine(directory,"calibration",name+".json");
            if(!File.Exists(file)){problems.Add("Calibration missing for "+name+". Complete the robot's approved calibration workflow before motion.");continue;}
            try
            {
                using var data=JsonDocument.Parse(File.ReadAllText(file));
                if(data.RootElement.ValueKind!=JsonValueKind.Object||!data.RootElement.EnumerateObject().Any())throw new JsonException();
                var joints=new[]{"shoulder_pan","shoulder_lift","elbow_flex","wrist_flex","wrist_roll","gripper"};
                var expected=name==c.RobotId?joints.SelectMany(j=>new[]{"left_arm_"+j,"right_arm_"+j}).Concat(new[]{"head_motor_1","head_motor_2","base_left_wheel","base_back_wheel","base_right_wheel"}).ToHashSet():joints.ToHashSet();
                if(!expected.SetEquals(data.RootElement.EnumerateObject().Select(p=>p.Name)))throw new JsonException();
                foreach(var motor in data.RootElement.EnumerateObject())
                {
                    var value=motor.Value;var min=value.GetProperty("range_min").GetInt32();var max=value.GetProperty("range_max").GetInt32();
                    var joint=motor.Name.Replace("left_arm_","",StringComparison.Ordinal).Replace("right_arm_","",StringComparison.Ordinal);
                    var expectedId=motor.Name switch{"head_motor_1"=>7,"head_motor_2"=>8,"base_left_wheel"=>7,"base_back_wheel"=>8,"base_right_wheel"=>9,_=>Array.IndexOf(joints,joint)+1};
                    if(min is <0 or >4095||max is <0 or >4095||min>=max||max-min<32||value.GetProperty("id").GetInt32()!=expectedId
                        ||value.GetProperty("drive_mode").GetInt32()!=0||value.GetProperty("homing_offset").GetInt32() is <-4095 or >4095)throw new JsonException();
                }
            }
            catch(Exception e) when(e is JsonException or InvalidOperationException or KeyNotFoundException or IOException)
            {problems.Add("Invalid calibration file for "+name+"; recalibrate before motion.");}
        }
        return problems.ToArray();
    }
    public static RobotDevices Devices()
    {
        static RobotDevice[] Read(string path,string pattern)
        {
            if(!Directory.Exists(path))return [];
            return Directory.GetFiles(path,pattern).Order(StringComparer.Ordinal).Select(p=>new RobotDevice(p,Path.GetFileName(p))).ToArray();
        }
        var pads=Read("/dev/input/by-id","*event-joystick");
        if(pads.Length==0)pads=Read("/dev/input","event*").Where(p=>ControllerInventory.IsGamepad(p.Path)).ToArray();
        return new(Read("/dev/serial/by-id","*"),pads,RobotCameraDevices.Read());
    }
    public RobotStatus Configure(RoboticsConfiguration selected)
    {
        lock(sync)
        {
            RequireLoaded();Idle();if(!stopLatched)throw new InvalidOperationException("Stop and disarm the robot before changing setup.");
            ValidateConfiguration(selected);Directory.CreateDirectory(directory);
            // Approval must not survive a camera/interface change. Keep the
            // existing range files and original recordings for later review.
            if(configuration==null||configuration.HeadCamera!=selected.HeadCamera||configuration.HandCamera!=selected.HandCamera)
            {
                try{File.Delete(Path.Combine(directory,"calibration","receipt.json"));}
                catch(DirectoryNotFoundException){} // First setup has no calibration directory.
            }
            var path=Path.Combine(directory,"config.json");File.WriteAllText(path+".tmp",RobotJson.Serialize(selected));File.Move(path+".tmp",path,true);
            if(configuration==null||configuration.RobotId!=selected.RobotId||configuration.LeftPort!=selected.LeftPort||configuration.RightPort!=selected.RightPort)motorDiagnostics.Clear();
            configuration=selected;configurationProblems=[];observation=null;observationReadAt=null;observationProblem=null;calibrationAssessment=null;return Status();
        }
    }
    string DetectionPath=>Path.Combine(directory,".build","bus-detection.json");
    public RobotBusDetection? Detection()
    {
        lock(sync)
        {
            if(!File.Exists(DetectionPath))return null;
            var found=RobotJson.Deserialize<RobotBusDetection>(File.ReadAllText(DetectionPath));
            return found!=null && File.Exists(found.LeftPort) && File.Exists(found.RightPort)?found:null;
        }
    }
    public RobotJob DetectBuses()
    {
        lock(sync)
        {
            RequireLoaded();Idle();if(!stopLatched)throw new InvalidOperationException("Disarm before detecting motor buses.");
            var ports=deviceInventory().Ports.Select(p=>p.Path).ToArray();
            if(ports.Length!=2)throw new InvalidOperationException("Connect exactly two serial/by-id adapters for XLeRobot bus detection.");
            if(File.Exists(DetectionPath))File.Delete(DetectionPath);
            return LaunchOperation("detect-buses",false,async token=>
            {
                var detected=await container.DetectBuses(ports,token);token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(DetectionPath)!);
                File.WriteAllText(DetectionPath+".tmp",RobotJson.Serialize(detected));File.Move(DetectionPath+".tmp",DetectionPath,true);
                return "Detected the upstream left/head and right/wheel motor buses. Save hardware setup with these roles; calibration remains required and the robot is disarmed.";
            });
        }
    }
    public RobotJob Prepare()
    {lock(sync){Idle();RequireConfiguration();if(!stopLatched)throw new InvalidOperationException("Disarm before preparing containers.");return Launch("prepare",false,async(c,token)=>{await container.Prepare(c,token);lock(sync)recoveryProblem=null;return "LeRobot/XLeRobot tools are ready in this container. Calibration and skills remain disarmed.";});}}
    public RobotJob Record(RobotRecordRequest request)
    {
        lock(sync)
        {
            Idle();RequireController();Motion();
            if(!RobotIdentifiers.Valid(request.Dataset)||request.Arm is not ("left" or "right")||string.IsNullOrWhiteSpace(request.Task)||request.Task.Length>500||request.Seconds is <5 or >60)
                throw new InvalidOperationException("Name the demonstration, select one arm, describe the task and record for 5 to 60 seconds.");
            return Launch("record",true,async(c,token)=>
            {
                ReserveController(c);
                var began=backups.BeginRecording(request.Dataset);
                var complete=false;Xur.Robot.Backups.RecordingBackupStatus? saved=null;
                try{await tools.Run(c,"record",request,request.Seconds+30,token);complete=true;}
                finally
                {
                    // The adapter releases its ownership before Run returns or
                    // throws. Preserve completed or interrupted local samples;
                    // snapshot verification never approves an incomplete episode.
                    saved=await backups.CaptureCompleted(request.Dataset,c.RobotId,request.Arm,request.Task,
                        new Xur.Robot.Backups.BackupProvenance("xbox",Environment.GetEnvironmentVariable("XUR_LEROBOT_VERSION"),Environment.GetEnvironmentVariable("XUR_XLEROBOT_REVISION"),CalibrationHash(),c.HeadCamera,c.HandCamera),
                        complete?"completed":"interrupted",began.RecordingId);
                }
                return saved.State=="snapshot-failed"?"Demonstration remains saved locally; immutable backup snapshot failed: "+saved.Error
                    :"Demonstration and immutable snapshot saved locally. Remote backup is "+saved.State+". Review samples before training or using them as an emote.";
            });
        }
    }
    public RobotJob Train(RobotTrainRequest request)
    {
        lock(sync)
        {
            Idle();RequireConfiguration();if(!stopLatched)throw new InvalidOperationException("Disarm before training.");
            if(!RobotIdentifiers.Valid(request.Dataset)||!RobotIdentifiers.Valid(request.Policy)||request.Steps is <10 or >100000)
                throw new InvalidOperationException("Select local dataset and policy names and 10 to 100000 training steps.");
            return Launch("train",false,async(c,token)=>{await tools.Run(c,"train",request,86400,token);return "ACT training finished. Evaluate the checkpoint under supervision before verifying a sorting skill.";});
        }
    }
    public RobotStatus ReviewSkill(RobotSkillReview request)
    {
        lock(sync)
        {
            Idle();var c=RequireConfiguration();if(!stopLatched)throw new InvalidOperationException("Disarm before reviewing skills.");
            var problems=CalibrationProblems(c);if(problems.Length!=0)throw new InvalidOperationException(string.Join(" ",problems));
            if(request.Skill==null)throw new InvalidOperationException("Select a skill to review.");
            var skill=request.Skill with{Verified=true,CalibrationHash=CalibrationHash()!};
            return Configure(c with{Skills=[..c.Skills.Where(s=>s.Id!=skill.Id),skill]});
        }
    }
    public RobotJob EvaluateSkill(RobotSkillReview request)
    {
        lock(sync)
        {
            Idle();Motion();var c=RequireConfiguration();
            if(request.Skill==null)throw new InvalidOperationException("Select a skill to evaluate under supervision.");
            var skill=request.Skill with{Verified=true,CalibrationHash=CalibrationHash()!};
            ValidateConfiguration(c with{Skills=[skill]});
            return Launch("evaluate",true,async(config,token)=>
            {
                await CaptureTo(config,"head","before-evaluation",token);
                await tools.Run(config,skill.Kind,new RobotSkillRequest(skill),skill.Seconds+15,token);
                await CaptureTo(config,"head","after-evaluation",token);
                return "Supervised evaluation ended. Inspect placement, clearances and camera evidence before reviewing this skill.";
            });
        }
    }
    public RobotStatus Arm(RobotArmRequest request)
    {
        lock(sync)
        {
            Idle();if(emergencyStop)throw new InvalidOperationException("E-stop is latched. Inspect and reset it before arming.");var c=RequireConfiguration();
            if(c.MotorLimits==null)throw new InvalidOperationException("Establish robot-specific motor load/current and following-error limits before arming.");
            if(!c.MotionEnabled)throw new InvalidOperationException("Verify calibration and enable motion in the local robot configuration first.");
            var problems=CalibrationProblems(c);if(problems.Length!=0)throw new InvalidOperationException(string.Join(" ",problems));
            if(request.Seconds is <5 or >120)throw new InvalidOperationException("Arm for 5 to 120 seconds.");
            if(recoveryProblem!=null)throw new InvalidOperationException(recoveryProblem);
            if(stopProblem!=null)throw new InvalidOperationException(stopProblem);
            stopLatched=false; armedCalibrationHash=CalibrationHash();armedUntil=clock.GetUtcNow().AddSeconds(request.Seconds); return Status();
        }
    }
    public RobotJob Controller(RobotControllerRequest request)
    {
        lock(sync)
        {
            Idle(); RequireController(); Motion();
            if(request.Seconds is <1 or >120)throw new InvalidOperationException("Controller sessions last 1 to 120 seconds.");
            return Launch("controller",true,async(c,token)=>
            {
                ReserveController(c);
                try{await tools.Run(c,"controller",new RobotControllerRequest(request.Seconds),request.Seconds+15,token);}
                finally{File.Delete(reservation);}
                return "Controller session ended. Robot is disarmed.";
            });
        }
    }
    public RobotJob StartController(RobotControllerRequest request)
    {
        lock(sync)
        {
            RequireController();
            if(request.Seconds is <5 or >120)throw new InvalidOperationException("Controller sessions last 5 to 120 seconds.");
            Arm(new(request.Seconds));
            try{return Controller(request);}
            catch{stopLatched=true;armedUntil=null;throw;}
        }
    }
    public RobotJob AutoCalibrate()
    {
        lock(sync)
        {
            Idle();RequireConfiguration();
            if(!stopLatched)throw new InvalidOperationException("Stop and disarm before automatic calibration.");
            calibrationAssessment=null;
            return Launch("auto-calibrate",false,async(c,token)=>
            {
                var telemetry=await tools.Run(c,"dashboard",null,60,token);
                lock(sync){observation=telemetry;observationReadAt=clock.GetUtcNow();motorDiagnostics.Observe(c,telemetry,observationReadAt.Value);}
                var survey=RobotMarkerSurvey.Read(await tools.Run(c,"inspect-markers",null,75,token));
                var metric=await metrology.Apply(survey.Report,c,token);
                var blockers=new List<string>();
                if(telemetry.TryGetProperty("buses",out var buses))
                    foreach(var bus in buses.EnumerateArray())
                    {
                        if(bus.TryGetProperty("error",out var error))blockers.Add("Motor inventory: "+error.GetString());
                        if(bus.TryGetProperty("motors",out var motors))foreach(var motor in motors.EnumerateArray())
                        {
                            if(motor.TryGetProperty("error",out var motorError))blockers.Add(motor.GetProperty("name").GetString()+": "+motorError.GetString());
                            if(motor.TryGetProperty("registers",out var registers))
                            {
                                if(registers.TryGetProperty("Torque_Enable",out var torque)&&torque.GetInt32()!=0)blockers.Add("Disable motor torque before calibration.");
                                if(registers.TryGetProperty("Min_Position_Limit",out var low)&&registers.TryGetProperty("Max_Position_Limit",out var high)&&high.GetInt32()-low.GetInt32()<32)
                                    blockers.Add(motor.GetProperty("name").GetString()+" has a suspicious stored travel range; it cannot be used as a verified mechanical limit.");
                            }
                        }
                    }
                else blockers.Add("Motor inventory is unavailable.");
                foreach(var camera in survey.Report.Cameras)
                    if(camera.Markers.Length==0)blockers.Add(camera.Name+" camera has no repeatedly visible, unambiguous tags.");
                // No supported solver has established these transforms on this robot.
                // Never turn readable tags or existing EEPROM values into a calibration receipt.
                if(metric.Metric==null)blockers.Add("Measured camera intrinsics, explicit lens distortion and actual tag reference sizes are not configured.");
                else foreach(var camera in metric.Metric.Cameras)
                    if(camera.State!="estimated"||camera.Tags.Any(t=>t.State!="estimated"))
                        blockers.Add(camera.Name+" metric references are missing, rejected or ambiguous; supplied metrology does not approve joint calibration.");
                blockers.AddRange([
                    "Measured rigid marker mounts and marker-to-link transforms are not configured.",
                    "The cameras have not established joint zero references and safe travel limits for every arm/head joint.",
                    "A verified physical motor-power emergency stop is required before powered automatic calibration.",
                    "The automatic joint-calibration solver is not implemented; this run performs its read-only assessment."]);
                var assessment=new RobotCalibrationAssessment(clock.GetUtcNow(),"blocked",
                    "Automatic calibration is blocked. Motor and AprilTag checks completed without motion; no calibration was approved.",blockers.Distinct().ToArray());
                var path=Path.Combine(directory,".build");Directory.CreateDirectory(path);
                await File.WriteAllTextAsync(Path.Combine(path,"calibration-assessment.json"),RobotJson.Serialize(assessment),token);
                lock(sync)calibrationAssessment=assessment;
                return assessment.Summary;
            });
        }
    }
    public async Task<RobotObservation> Observation(CancellationToken token)
    {
        Task pending;
        lock(sync)
        {
            RequireConfiguration();
            if(activeJob!=null||stopping||!stopLatched)
                return ObservationReply(true,activeJob==null?"armed session":jobs[activeJob].Kind,null);
            if(observationReadAt>clock.GetUtcNow().AddSeconds(-5))return ObservationReply(false,null,null);
            if(!observing)
            {
                var c=RequireConfiguration();
                observing=true;var source=new CancellationTokenSource();cancellation=source;
                worker=System.Threading.Tasks.Task.Run(async()=>
                {
                    try
                    {
                        var data=await tools.Run(c,"dashboard",null,60,source.Token);
                        lock(sync){observation=data;observationReadAt=clock.GetUtcNow();observationProblem=null;motorDiagnostics.Observe(c,data,observationReadAt.Value);}
                    }
                    catch(Exception e){lock(sync)observationProblem=Xur.Domain.Redaction.Logs(e.Message);}
                    finally{lock(sync){observing=false;cancellation=null;source.Dispose();}}
                });
            }
            pending=worker!;
        }
        // Browser disconnects do not cancel a snapshot shared by other viewers.
        await pending.WaitAsync(token);
        lock(sync)return ObservationReply(false,null,observationProblem);
    }
    RobotObservation ObservationReply(bool paused,string? operation,string? problem)=>new(observation,paused,operation,problem,
        configuration==null?[]:motorDiagnostics.Report(directory,configuration,observation,clock.GetUtcNow()));
    void ReserveController(RoboticsConfiguration c)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reservation)!);
        // Hardware resolution belongs to the real local adapter; an injected
        // test adapter owns its own device boundary.
        var target=tools is LeRobotTools?File.ResolveLinkTarget(c.ControllerDevice,true)?.FullName??c.ControllerDevice:c.ControllerDevice;
        File.WriteAllText(reservation,target);
    }
    public RobotJob Emote(RobotEmoteRequest request)
    {
        lock(sync)
        {
            Idle(); Motion(); var skill=Skill(request.Skill,"emote");
            return Launch("emote",true,async(c,token)=>{await tools.Run(c,"emote",new RobotSkillRequest(skill),skill.Seconds+15,token);Completed(skill.Id);return "Recorded emote finished. Robot is disarmed.";});
        }
    }
    RobotSkill Skill(string id,string kind)
    {
        var skill=RequireConfiguration().Skills.SingleOrDefault(s=>s.Id==id&&s.Kind==kind)??throw new InvalidOperationException("Select a configured skill of the requested kind.");
        if(!skill.Verified||skill.CalibrationHash!=CalibrationHash()||string.IsNullOrEmpty(skill.CalibrationHash))throw new InvalidOperationException("This skill has not been verified on the current robot and calibration.");
        return skill;
    }
    public RobotJob Task(RobotTaskRequest request)
    {
        lock(sync)
        {
            Idle(); RequireConfiguration();
            if(request.Kind=="inspect-table")
            {
                if(request.Items is {Length:>0})throw new InvalidOperationException("Table inspection does not take sorting items.");
                return Launch("inspect-table",false,async(c,token)=>
                {
                    foreach(var camera in new[]{"head","hand"})await CaptureTo(c,camera,"inspection",token);
                    return "Captured head and hand camera views without moving the robot.";
                });
            }
            if(request.Kind=="inspect-markers")
            {
                if(request.Items is {Length:>0})throw new InvalidOperationException("Marker inspection does not take sorting items.");
                return Launch("inspect-markers",false,async(c,token)=>
                {
                    var result=RobotMarkerSurvey.Read(await tools.Run(c,"inspect-markers",null,75,token));
                    var report=await metrology.Apply(result.Report,c,token);
                    string id;lock(sync)id=activeJob!;
                    var path=Path.Combine(directory,".build","captures",id);Directory.CreateDirectory(path);
                    foreach(var image in result.Images)
                        await File.WriteAllBytesAsync(Path.Combine(path,image.Key+"-markers.jpg"),image.Value,token);
                    await File.WriteAllTextAsync(Path.Combine(path,"markers.json"),RobotJson.Serialize(report),token);
                    var observed=string.Join("; ",result.Report.Cameras.Select(camera=>camera.Name+": "+
                        (camera.Markers.Length==0?"no unambiguous tags":string.Join(", ",camera.Markers.Select(m=>$"{m.Id:00} ({m.DetectedFrames}/3)")))));
                    return "Marker observations saved. "+observed+". Joint calibration remains unverified.";
                },request);
            }
            if(request.Kind!="sort")throw new InvalidOperationException("Choose inspect-table, inspect-markers or sort.");
            Motion();
            if(string.IsNullOrWhiteSpace(request.Instructions)||request.Instructions.Length>1000||request.Items is not {Length:>=1 and <=8})
                throw new InvalidOperationException("Describe the sorting task and supply 1 to 8 object/bin/skill selections.");
            var selected=request.Items.Select(item=>
            {
                if(item==null||string.IsNullOrWhiteSpace(item.Object)||item.Object.Length>200)throw new InvalidOperationException("Describe each selected object.");
                var skill=Skill(item.Skill,"sort");
                if(skill.Bin!=item.Bin)throw new InvalidOperationException("The skill was verified for a different bin.");
                return (item,skill);
            }).ToArray();
            // A fixed ACT policy cannot take an arbitrary object or task prompt:
            // descriptions are retained for review, never rewritten into actions.
            return Launch("sort",true,async(c,token)=>
            {
                for(var index=0;index<selected.Length;index++)
                {
                    var skill=selected[index].skill;
                    token.ThrowIfCancellationRequested();
                    await CaptureTo(c,"head","before-"+index+"-"+skill.Id,token);
                    await tools.Run(c,"sort",new RobotSkillRequest(skill),skill.Seconds+15,token);
                    await CaptureTo(c,"head","after-"+index+"-"+skill.Id,token);
                    Completed(skill.Id);
                }
                return "Policies finished; inspect the camera evidence and verify placement before starting another task.";
            },request);
        }
    }
    async Task CaptureTo(RoboticsConfiguration c,string camera,string suffix,CancellationToken token)
    {
        var result=await tools.Run(c,"camera",new RobotCameraRequest(camera),15,token);
        var data=Convert.FromBase64String(result.GetProperty("jpeg").GetString()!);
        if(data.Length>2*1024*1024)throw new IOException("Camera image exceeds its limit.");
        string id;lock(sync)id=activeJob!;
        var path=Path.Combine(directory,".build","captures",id);Directory.CreateDirectory(path);
        await File.WriteAllBytesAsync(Path.Combine(path,camera+"-"+suffix+".jpg"),data,token);
    }
    public async Task<byte[]> Camera(string camera,CancellationToken token)
    {
        if(camera is not ("head" or "hand"))throw new InvalidOperationException("Select the head or hand camera.");
        // Camera access uses the same ownership gate as manipulation.
        RobotJob job;
        byte[]? bytes=null;
        lock(sync)
        {
            Idle();RequireConfiguration();
            job=Launch("camera",false,async(c,cancel)=>{var result=await tools.Run(c,"camera",new RobotCameraRequest(camera),15,cancel);bytes=Convert.FromBase64String(result.GetProperty("jpeg").GetString()!);return "Camera captured.";});
        }
        Task pending;lock(sync)pending=worker!;
        using var registration=token.Register(()=>{lock(sync)if(activeJob==job.Id)cancellation?.Cancel();});
        await pending;
        if(Job(job.Id)?.State!="completed" || bytes==null)throw new InvalidOperationException(Job(job.Id)?.Detail??"Camera capture failed.");
        if(bytes.Length>2*1024*1024)throw new IOException("Camera image exceeds its limit.");
        return bytes;
    }
    public async Task<RobotCameraCapabilityReport> CameraCapabilities(CancellationToken token)
    {
        RobotJob job;RobotCameraCapabilityReport? report=null;Task pending;
        lock(sync)
        {
            RequireLoaded();Idle();if(!stopLatched)throw new InvalidOperationException("Stop and disarm before inspecting camera controls.");
            var cameras=deviceInventory().Cameras;var selected=configuration;
            job=LaunchOperation("camera-capabilities",false,async cancel=>
            {report=await cameraCapabilities.Read(cameras,selected,cancel);return "Read-only camera driver capabilities inspected; no settings or motor state changed.";});
            pending=worker!;
        }
        using var registration=token.Register(()=>{lock(sync)if(activeJob==job.Id)cancellation?.Cancel();});
        await pending;
        if(Job(job.Id)?.State!="completed"||report is null)throw new InvalidOperationException(Job(job.Id)?.Detail??"Camera capability inspection failed.");
        return report;
    }
    public RobotJob Probe()
    {lock(sync){Idle();RequireConfiguration();return Launch("probe",false,async(c,token)=>(await tools.Run(c,"probe",null,15,token)).GetRawText());}}
    void Completed(string skill)
    {lock(sync){var job=jobs[activeJob!];Update(job with{CompletedSkills=[..job.CompletedSkills,skill]});}}
    public string[] Captures(string id)
    {
        lock(sync)
        {
            if(!System.Text.RegularExpressions.Regex.IsMatch(id,@"\A[a-f0-9]{32}\z")||!jobs.ContainsKey(id))throw new InvalidOperationException("Select a known robot job.");
            var path=Path.Combine(directory,".build","captures",id);
            return Directory.Exists(path)?Directory.GetFiles(path,"*.jpg").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray():[];
        }
    }
    public byte[] Capture(string id,string name)
    {
        if(!Captures(id).Contains(name,StringComparer.Ordinal))throw new InvalidOperationException("Select a captured image from this job.");
        return File.ReadAllBytes(Path.Combine(directory,".build","captures",id,name));
    }
    public RobotMarkerReport Markers(string id)
    {
        lock(sync)
        {
            if(!System.Text.RegularExpressions.Regex.IsMatch(id,@"\A[a-f0-9]{32}\z")
                || !jobs.TryGetValue(id,out var job) || job.Kind!="inspect-markers" || job.State!="completed")
                throw new InvalidOperationException("Select a completed marker inspection job.");
            var path=Path.Combine(directory,".build","captures",id,"markers.json");
            if(!File.Exists(path))throw new InvalidOperationException("Marker observations are unavailable for this job.");
            return RobotJson.Deserialize<RobotMarkerReport>(File.ReadAllText(path))
                ??throw new InvalidOperationException("Marker observations are unavailable for this job.");
        }
    }
    RobotJob Launch(string kind,bool motion,Func<RoboticsConfiguration,CancellationToken,Task<string>> execute,RobotTaskRequest? request=null)
    {
        var c=RequireConfiguration();
        return LaunchOperation(kind,motion,token=>execute(c,token),request);
    }
    RobotJob LaunchOperation(string kind,bool motion,Func<CancellationToken,Task<string>> execute,RobotTaskRequest? request=null)
    {
        var now=clock.GetUtcNow();
        var job=new RobotJob(Guid.NewGuid().ToString("N"),kind,"running","Started.",now,now,[],request);
        while(jobs.Count>=64)
        {
            var expired=jobs.Keys.First();jobs.Remove(expired);
            File.Delete(Path.Combine(directory,".build","jobs",expired+".json"));
            var captures=Path.Combine(directory,".build","captures",expired);if(Directory.Exists(captures))Directory.Delete(captures,true);
        }
        Update(job);activeJob=job.Id;
        cancellation=new CancellationTokenSource();
        if(motion)cancellation.CancelAfter(armedUntil!.Value-now);
        var source=cancellation;
        worker=System.Threading.Tasks.Task.Run(async()=>
        {
            var state="completed"; string detail;
            try
            {
                detail=await execute(source.Token);source.Token.ThrowIfCancellationRequested();
                if(kind=="sort")state="awaiting-verification";
            }
            catch(OperationCanceledException){state="stopped";detail="Stopped or arm session expired; inspect the robot before rearming.";}
            catch(Exception e){state="failed";detail=Xur.Domain.Redaction.Logs(e.Message);}
            finally{try{File.Delete(reservation);}catch(IOException){state="failed";}}
            lock(sync)
            {
                try{Update(jobs[job.Id] with{State=state,Detail=detail});}
                finally
                {
                    activeJob=null;
                    if(motion || state is "failed" or "stopped"){stopLatched=true;armedUntil=null;}
                    cancellation=null;source.Dispose();
                }
            }
        });
        return job;
    }
    void Update(RobotJob job)
    {
        job=job with{Updated=clock.GetUtcNow()};jobs[job.Id]=job;
        var path=Path.Combine(directory,".build","jobs");Directory.CreateDirectory(path);
        var file=Path.Combine(path,job.Id+".json");File.WriteAllText(file+".tmp",RobotJson.Serialize(job));File.Move(file+".tmp",file,true);
    }
    public Task<RobotStatus> Stop()
    {
        lock(sync)
        {
            if(stopWorker is {IsCompleted:false})return stopWorker;
            stopLatched=true;armedUntil=null;cancellation?.Cancel();var pending=worker;stopping=true;
            return stopWorker=System.Threading.Tasks.Task.Run(async()=>
            {
                try
                {
                    try{if(pending!=null)await pending;}
                    finally{if(tools is LeRobotTools && File.Exists(Path.Combine(directory,".build","devices.json")))await container.Interrupt();}
                    lock(sync)stopProblem=null;
                    return Status();
                }
                catch(Exception e)
                {
                    lock(sync)stopProblem="Software stop cleanup failed. Inspect motor power and retry Stop & disarm before resetting or arming: "+Xur.Domain.Redaction.Logs(e.Message);
                    throw;
                }
                finally{lock(sync)stopping=false;}
            });
        }
    }
    public RobotStatus EmergencyStop()
    {
        lock(sync)
        {
            emergencyStop=true;emergencyStopEpoch++;
            // Cancel immediately. The HTTP request does not wait for adapter cleanup.
            _=Stop().ContinueWith(t=>{_=t.Exception;},CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(EmergencyStopPath+".tmp",clock.GetUtcNow().ToString("O"));
                File.Move(EmergencyStopPath+".tmp",EmergencyStopPath,true);
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException)
            {recoveryProblem="E-stop was requested but its latch could not be persisted: "+Xur.Domain.Redaction.Logs(e.Message);}
            return Status();
        }
    }
    public RobotJob ResetEmergencyStop()
    {
        lock(sync)
        {
            Idle();RequireConfiguration();
            if(!emergencyStop)throw new InvalidOperationException("E-stop is not latched.");
            if(stopProblem!=null)throw new InvalidOperationException(stopProblem);
            var epoch=emergencyStopEpoch;
            return Launch("reset-estop",false,async(c,token)=>
            {
                var result=await tools.Run(c,"dashboard",null,60,token);
                if(!result.TryGetProperty("buses",out var buses)||buses.GetArrayLength()!=2)
                    throw new InvalidOperationException("Fresh feedback from both motor buses is required to reset E-stop.");
                var count=0;
                foreach(var bus in buses.EnumerateArray())
                {
                    if(bus.TryGetProperty("error",out _)||!bus.TryGetProperty("motors",out var motors))
                        throw new InvalidOperationException("A motor bus is unavailable; E-stop remains latched.");
                    foreach(var motor in motors.EnumerateArray())
                    {
                        count++;
                        if(motor.TryGetProperty("error",out _)||!motor.TryGetProperty("registers",out var registers)
                            ||!registers.TryGetProperty("Torque_Enable",out var torque)||torque.GetInt32()!=0
                            ||!registers.TryGetProperty("Moving",out var moving)||moving.GetInt32()!=0
                            ||!registers.TryGetProperty("Status",out var status)||status.GetInt32()!=0)
                            throw new InvalidOperationException("Every motor must report torque off, stationary and no status fault. E-stop remains latched.");
                    }
                }
                if(count!=17)throw new InvalidOperationException("All seventeen motors must respond before resetting E-stop.");
                token.ThrowIfCancellationRequested();
                lock(sync)
                {
                    if(epoch!=emergencyStopEpoch)throw new OperationCanceledException("E-stop was pressed again.");
                    File.Delete(EmergencyStopPath);emergencyStop=false;
                    observation=result;observationReadAt=clock.GetUtcNow();motorDiagnostics.Observe(c,result,observationReadAt.Value);stopLatched=true;armedUntil=null;
                }
                return "E-stop reset after checking all motors. The robot remains disarmed; start a new operator session separately.";
            });
        }
    }
    public async Task Shutdown()
    {
        lock(sync)workloadId=null;
        await Stop();
        if(tools is LeRobotTools)await container.Stop();
    }
}
