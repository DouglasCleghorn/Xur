using System.Security.Cryptography;
using System.Text.Json;
using Xur.Robot;

static class MotorDiagnosticsTests
{
    sealed class Clock:TimeProvider
    {public DateTimeOffset Now;public override DateTimeOffset GetUtcNow()=>Now;}
    sealed class Tools(Clock clock):IRobotTools
    {
        public int Calls,Temperature=34;
        public Task<JsonElement> Run(RoboticsConfiguration c,string operation,object? request,int seconds,CancellationToken token)
        {
            if(operation!="dashboard")throw new Exception("Diagnostics attempted an extra hardware operation.");
            Calls++;return Task.FromResult(Reading(c,clock.Now,Temperature));
        }
    }
    sealed class AssessmentTools(Clock clock):IRobotTools
    {
        public int DashboardCalls,MarkerCalls,Temperature=57;
        public Task<JsonElement> Run(RoboticsConfiguration c,string operation,object? request,int seconds,CancellationToken token)
        {
            if(operation=="dashboard")
            {
                DashboardCalls++;
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    observedAt=clock.Now,
                    buses=new[]{(c.LeftPort,8),(c.RightPort,9)}.Select(bus=>new
                    {
                        port=bus.Item1,motors=Enumerable.Range(1,bus.Item2).Select(id=>new
                        {id,observedAt=clock.Now,registers=new{Present_Temperature=Temperature,Homing_Offset=0,Torque_Enable=0,Moving=0,Status=0}}).ToArray()
                    }).ToArray(),cameras=Array.Empty<object>()
                }));
            }
            if(operation!="inspect-markers")throw new Exception("Cached dashboard ingestion attempted an extra hardware operation.");
            MarkerCalls++;
            RobotMarkerSurvey.AdapterCamera Camera(string name)=>new(name,Enumerable.Range(0,3).Select(i=>
                new RobotMarkerSurvey.AdapterFrame(clock.Now.AddMilliseconds(i),100,100,
                    [new RobotMarkerDetection(0,0,100,[15,15],[[10,10],[20,10],[20,20],[10,20]])])).ToArray(),"/9j/2Q==");
            return Task.FromResult(RobotJson.Element(new RobotMarkerSurvey.AdapterResult("tagStandard41h12",new string('a',64),[Camera("head"),Camera("hand")])));
        }
    }
    static RoboticsConfiguration Configuration()=>new("fixture","/dev/serial/by-id/left","/dev/serial/by-id/right","",
        "/dev/v4l/by-id/head-video-index0","/dev/v4l/by-id/hand-video-index0",[]);
    static JsonElement Reading(RoboticsConfiguration c,DateTimeOffset at,int left=34,int right=31,int offset=0)=>JsonSerializer.SerializeToElement(new
    {
        observedAt=at,
        buses=new[]{new{port=c.LeftPort,motors=new[]{new{id=1,observedAt=at,registers=new{Present_Temperature=left,Homing_Offset=offset,Min_Position_Limit=100,Max_Position_Limit=3995}},new{id=7,observedAt=at,registers=new{Present_Temperature=30,Homing_Offset=offset,Min_Position_Limit=100,Max_Position_Limit=3995}}}},
            new{port=c.RightPort,motors=new[]{new{id=1,observedAt=at,registers=new{Present_Temperature=right,Homing_Offset=offset,Min_Position_Limit=100,Max_Position_Limit=3995}},new{id=7,observedAt=at,registers=new{Present_Temperature=32,Homing_Offset=offset,Min_Position_Limit=100,Max_Position_Limit=3995}}}}}
    });
    static void Calibration(string directory,RoboticsConfiguration c,bool disagree=false,int maximum=3995)
    {
        var path=Path.Combine(directory,"calibration");Directory.CreateDirectory(path);
        var joints=new[]{"shoulder_pan","shoulder_lift","elbow_flex","wrist_flex","wrist_roll","gripper"};
        var files=new Dictionary<string,string>();
        foreach(var name in new[]{c.RobotId,c.RobotId+"-left",c.RobotId+"-right"})
        {
            var names=name==c.RobotId?joints.SelectMany(j=>new[]{"left_arm_"+j,"right_arm_"+j})
                .Concat(["head_motor_1","head_motor_2","base_left_wheel","base_back_wheel","base_right_wheel"]):joints;
            var values=names.Select(n=>(n,value:new{id=n switch{"head_motor_1"=>7,"head_motor_2"=>8,"base_left_wheel"=>7,"base_back_wheel"=>8,"base_right_wheel"=>9,_=>Array.IndexOf(joints,n.Replace("left_arm_","").Replace("right_arm_",""))+1},
                drive_mode=0,homing_offset=0,range_min=100+(disagree&&name==c.RobotId+"-left"?1:0),range_max=maximum})).ToDictionary(x=>x.n,x=>x.value);
            var file=Path.Combine(path,name+".json");File.WriteAllText(file,JsonSerializer.Serialize(values));
            files[name+".json"]=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
        }
        File.WriteAllText(Path.Combine(path,"receipt.json"),JsonSerializer.Serialize(new{robotId=c.RobotId,leftPort=c.LeftPort,rightPort=c.RightPort,files}));
    }
    public static async Task Run(Action<bool,string> check)
    {
        var directory=Path.GetFullPath(".build/evidence/motor-diagnostics-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var c=Configuration();var now=new DateTimeOffset(2026,10,10,12,0,0,TimeSpan.Zero);var diagnostics=new RobotMotorDiagnostics();
            var data=Reading(c,now);diagnostics.Observe(c,data,now);
            var motors=diagnostics.Report(directory,c,data,now);
            check(motors.Length==4&&motors[0].Temperature.MaximumCelsius==34&&motors[2].Temperature.MaximumCelsius==31,
                "Temperature histories use stable bus path and motor ID, keeping identical IDs on different buses separate");
            check(motors[0].CalibratedRange is {State:"not-calibrated",MinimumCounts:null}&&motors[0].CalibratedRange.Detail.Contains("EEPROM"),
                "Readable stored EEPROM limits cannot fabricate a calibrated motor range");
            check(motors[3].CalibratedRange.State=="not-applicable"&&motors[1].CalibratedRange.State=="not-calibrated",
                "Wheel angular ranges are not applicable while head joints still require calibration evidence");
            now=now.AddSeconds(10);data=Reading(c,now,57);diagnostics.Observe(c,data,now);
            now=now.AddSeconds(10);data=Reading(c,now,34);diagnostics.Observe(c,data,now);
            var peak=diagnostics.Report(directory,c,data,now)[0].Temperature;
            check(peak is {MaximumCelsius:57,SampleCount:3,ObservedSpanSeconds:20}&&peak.Coverage.Contains("gaps"),
                "Five-minute maximum retains a real temperature spike and describes sampled rather than continuous coverage");
            diagnostics.Observe(c,data,now);
            diagnostics.Observe(c,Reading(c,now.AddSeconds(-5),99),now);
            diagnostics.Observe(c,Reading(c,now.AddSeconds(1),99),now);
            diagnostics.Observe(c,Reading(c,now.AddSeconds(-61),99),now);
            check(diagnostics.Report(directory,c,data,now)[0].Temperature is {MaximumCelsius:57,SampleCount:3},
                "Repeated, reversed, future and stale capture timestamps cannot add temperature samples or renew their lifetime");
            var unrelated=Reading(c with{LeftPort="/dev/serial/by-id/unrelated",RightPort="/dev/serial/by-id/other"},now,99);
            diagnostics.Observe(c,unrelated,now);
            check(diagnostics.Report(directory,c,data,now)[0].Temperature.MaximumCelsius==57,
                "Unknown serial buses cannot contaminate configured motor temperature histories");
            check(diagnostics.Report(directory,c,data,now.AddSeconds(300))[0].Temperature is {MaximumCelsius:null,SampleCount:0},
                "Expired peaks and samples disappear even when only cached observations are rendered");
            Calibration(directory,c);motors=diagnostics.Report(directory,c,data,now);
            check(motors[0].CalibratedRange is {State:"verified",MinimumCounts:100,MaximumCounts:3995,SourceFile:"fixture.json",ReceiptHash:not null}
                &&motors[1].CalibratedRange.State=="verified"&&motors[3].CalibratedRange.State=="not-applicable",
                "Motor ranges require matching calibration files and receipt, expose raw-count convention and preserve wheel semantics");
            check(diagnostics.Report(directory,c,Reading(c,now,offset:1),now)[0].CalibratedRange.State=="not-calibrated",
                "An observed homing offset that differs from calibration cannot display a verified range");
            File.AppendAllText(Path.Combine(directory,"calibration","fixture-left.json")," ");
            check(diagnostics.Report(directory,c,data,now)[0].CalibratedRange.State=="not-calibrated",
                "Changing a hashed calibration file invalidates displayed calibrated ranges immediately");
            Calibration(directory,c,disagree:true);
            check(diagnostics.Report(directory,c,data,now)[0].CalibratedRange.State=="not-calibrated",
                "Hash-valid whole-robot and single-arm calibration files must agree before their ranges can be displayed");
            Calibration(directory,c,maximum:int.MinValue);
            check(diagnostics.Report(directory,c,data,now)[0].CalibratedRange.State=="not-calibrated",
                "Negative calibration endpoints that would overflow a span subtraction are rejected before arithmetic");
            Calibration(directory,c);File.Delete(Path.Combine(directory,"calibration","receipt.json"));
            check(diagnostics.Report(directory,c,data,now)[0].CalibratedRange.State=="not-calibrated",
                "Unapproved calibration files without their robot-matched receipt remain uncalibrated");
            var json=RobotJson.Serialize(new RobotObservation(data,false,null,null,motors));
            check(RobotJson.Deserialize<RobotObservation>(json)?.Motors?[0].Temperature.Coverage.Contains("gaps")==true,
                "Motor diagnostic output serializes with the Native AOT source-generated JSON context");
            diagnostics.Clear();check(diagnostics.Report(directory,c,data,now)[0].Temperature.SampleCount==0,
                "Changing robot identity or starting the app clears previous motor temperature samples");
            var clock=new Clock{Now=now};var tools=new Tools(clock);var runtime=new RoboticsRuntime(directory,Path.Combine(directory,"reservation"),tools,clock);
            runtime.Start("fixture");runtime.Configure(c);
            var fresh=await runtime.Observation(CancellationToken.None);var cached=await runtime.Observation(CancellationToken.None);
            check(tools.Calls==1&&fresh.Motors?[0].Temperature.SampleCount==1&&cached.Motors?[0].Temperature.SampleCount==1,
                "Dashboard diagnostics reuse the existing snapshot cache without extra device polls or duplicate samples");
            clock.Now=clock.Now.AddSeconds(6);tools.Temperature=57;await runtime.Observation(CancellationToken.None);
            clock.Now=clock.Now.AddSeconds(6);tools.Temperature=34;var next=await runtime.Observation(CancellationToken.None);
            check(tools.Calls==3&&next.Motors?[0].Temperature is {MaximumCelsius:57,SampleCount:3},
                "Backend temperature history survives separate dashboard requests and retains sampled peaks");
            runtime.Configure(c with{RobotId="different"});var changed=await runtime.Observation(CancellationToken.None);
            check(changed.Motors?[0].Temperature is {MaximumCelsius:34,SampleCount:1},
                "Reconfiguring a different physical robot cannot reuse the previous robot's temperature maximum");
            var enabled=c with{MotionEnabled=true,MotorLimits=new(100,100,2)};runtime.Configure(enabled);
            Calibration(directory,enabled,maximum:int.MinValue);var rejected=false;
            try{runtime.Arm(new());}catch(InvalidOperationException){rejected=true;}
            check(rejected&&runtime.Status().StopLatched&&tools.Calls==4,
                "Hash-valid calibration endpoints that overflow span subtraction cannot arm motion or trigger motor tools");
            await CachedAssessmentAndReset(directory,c,check);
        }
        finally{Directory.Delete(directory,true);}
    }
    static async Task CachedAssessmentAndReset(string directory,RoboticsConfiguration c,Action<bool,string> check)
    {
        var clock=new Clock{Now=new DateTimeOffset(2026,10,10,12,0,0,TimeSpan.Zero)};
        var tools=new AssessmentTools(clock);var path=Path.Combine(directory,"cached-assessment");
        var runtime=new RoboticsRuntime(path,Path.Combine(path,"reservation"),tools,clock);
        runtime.Start("fixture");runtime.Configure(c);
        try
        {
            var assessment=runtime.AutoCalibrate();await Finished(assessment.Id);
            var cached=await runtime.Observation(CancellationToken.None);
            check(runtime.Job(assessment.Id)?.State=="completed"&&cached.Motors?[0].Temperature is{MaximumCelsius:57,SampleCount:1}
                &&tools.DashboardCalls==1&&tools.MarkerCalls==1&&!runtime.CalibrationAssessment()!.JointCalibrationApproved,
                "Fresh assessment feedback enters temperature history before cache reuse without an extra dashboard poll or approval");
            runtime.EmergencyStop();await runtime.Stop();clock.Now=clock.Now.AddSeconds(6);tools.Temperature=34;
            var reset=runtime.ResetEmergencyStop();await Finished(reset.Id);
            cached=await runtime.Observation(CancellationToken.None);
            check(runtime.Job(reset.Id)?.State=="completed"&&cached.Motors?[0].Temperature is{MaximumCelsius:57,SampleCount:2}
                &&tools.DashboardCalls==2&&tools.MarkerCalls==1&&runtime.Status().StopLatched,
                "Successful reset feedback enters sampled temperature history while cached reads retain prior peaks without extra polls");
        }
        finally{await runtime.Shutdown();}
        async Task Finished(string id)
        {
            for(var i=0;i<300&&runtime.Job(id)?.State=="running";i++)await Task.Delay(10);
            if(runtime.Job(id)?.State=="running")throw new Exception("Cached dashboard test job did not finish.");
        }
    }
}
