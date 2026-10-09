using System.Security.Cryptography;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xur.Robot;

static class RoboticsTests
{
    sealed class Clock:TimeProvider
    {public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
    sealed class Tools:IRobotTools
    {
        public bool Block;
        public JsonElement? MarkerObservations;
        public JsonElement? Dashboard;
        public List<string> Calls=[];
        public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<JsonElement> Run(RoboticsConfiguration c,string operation,object? request,int seconds,CancellationToken token)
        {
            lock(Calls)Calls.Add(operation);Started.TrySetResult();
            if(Block)await Task.Delay(Timeout.Infinite,token);
            if(operation=="inspect-markers" && MarkerObservations is {} observations)return observations;
            if(operation=="dashboard")return Dashboard??MotorFeedback();
            return JsonSerializer.SerializeToElement(operation switch{"camera"=>(object)new{jpeg="/9j/2Q=="},"inspect-markers"=>MarkerResult(),
                "dashboard"=>new{observedAt=DateTimeOffset.UtcNow,buses=Array.Empty<object>(),cameras=Array.Empty<object>()},_=>new{done=true}},new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
    }
    static JsonElement MotorFeedback(int torque=0,int count=17)=>JsonSerializer.SerializeToElement(new
    {
        observedAt=DateTimeOffset.UtcNow,
        buses=new[]{new{role="left/head",motors=Enumerable.Range(1,8).Select(id=>new{id,registers=new{Torque_Enable=torque,Moving=0,Status=0}}).ToArray()},
            new{role="right/wheels",motors=Enumerable.Range(1,Math.Max(0,count-8)).Select(id=>new{id,registers=new{Torque_Enable=torque,Moving=0,Status=0}}).ToArray()}},
        cameras=Array.Empty<object>()
    });
    static RobotMarkerSurvey.AdapterResult MarkerResult()
    {
        static RobotMarkerDetection Marker(int id)=>new(id,0,100,[15,15],[[10,10],[20,10],[20,20],[10,20]]);
        var time=DateTimeOffset.UtcNow;
        RobotMarkerSurvey.AdapterCamera Camera(string name,int[] ids)=>new(name,Enumerable.Range(0,3).Select(i=>
            new RobotMarkerSurvey.AdapterFrame(time.AddMilliseconds(i),100,100,ids.Select(Marker).ToArray())).ToArray(),"/9j/2Q==");
        return new("tagStandard41h12",new string('a',64),[Camera("head",[0,1,2]),Camera("hand",[1])]);
    }
    static JsonElement MarkerJson(RobotMarkerSurvey.AdapterResult result)=>JsonSerializer.SerializeToElement(result,new JsonSerializerOptions(JsonSerializerDefaults.Web));
    static RoboticsConfiguration Configuration(bool motion=false)=>new("fixture","/dev/serial/by-id/left","/dev/serial/by-id/right",
        "/dev/input/event77","/dev/v4l/by-id/head-video-index0","/dev/v4l/by-id/hand-video-index0",[],motion,new(100,100,2));
    static bool Rejected(Action action){try{action();return false;}catch(InvalidOperationException){return true;}}
    static async Task Finished(RoboticsRuntime robot)
    {for(var i=0;i<300 && robot.Status().Job?.State=="running";i++)await Task.Delay(10);if(robot.Status().Job?.State=="running")throw new Exception("Robot job did not finish.");}
    static void Calibrate(string directory,RoboticsConfiguration c)
    {
        var path=Path.Combine(directory,"calibration");Directory.CreateDirectory(path);
        var joints=new[]{"shoulder_pan","shoulder_lift","elbow_flex","wrist_flex","wrist_roll","gripper"};
        var files=new Dictionary<string,string>();
        foreach(var name in new[]{c.RobotId,c.RobotId+"-left",c.RobotId+"-right"})
        {
            var names=name==c.RobotId?joints.SelectMany(j=>new[]{"left_arm_"+j,"right_arm_"+j}).Concat(new[]{"head_motor_1","head_motor_2","base_left_wheel","base_back_wheel","base_right_wheel"}):joints;
            var values=names.Select(n=>(n,value:new{id=n switch{"head_motor_1"=>7,"head_motor_2"=>8,"base_left_wheel"=>7,"base_back_wheel"=>8,"base_right_wheel"=>9,_=>Array.IndexOf(joints,n.Replace("left_arm_","").Replace("right_arm_",""))+1},drive_mode=0,homing_offset=0,range_min=100,range_max=3995})).ToDictionary(x=>x.n,x=>x.value);
            var file=Path.Combine(path,name+".json");File.WriteAllText(file,JsonSerializer.Serialize(values));files[name+".json"]=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
        }
        File.WriteAllText(Path.Combine(path,"receipt.json"),JsonSerializer.Serialize(new{robotId=c.RobotId,leftPort=c.LeftPort,rightPort=c.RightPort,files}));
    }
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(".build/evidence/robotics-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            static RobotBusInventory Bus(string port,int count)=>new(port,Enumerable.Range(1,count).ToDictionary(id=>id,_=>777));
            var headBus=Bus("usb-head",8);var wheelBus=Bus("usb-wheels",9);
            var detected=RobotBusDetection.MatchXLeRobot([wheelBus,headBus]);
            check(detected.LeftPort==headBus.Port && detected.RightPort==wheelBus.Port,"Bus roles are detected independently of enumeration order");
            check(Rejected(()=>RobotBusDetection.MatchXLeRobot([headBus,Bus("incomplete",7)])),"Missing motors cannot masquerade as a detected bus");
            check(Rejected(()=>RobotBusDetection.MatchXLeRobot([headBus,Bus("also-head",8)])),"Identical bus inventories are ambiguous and rejected");
            var wrong=Bus("wrong-model",9);wrong.Motors[1]=2825;
            check(Rejected(()=>RobotBusDetection.MatchXLeRobot([headBus,wrong])),"Incompatible motor models block automatic bus assignment");
            check(Rejected(()=>RobotBusDetection.MatchXLeRobot([headBus,Bus("extra",10)])),"Unexpected extra motor identities block bus assignment");
            var tools=new Tools();var clock=new Clock();var robot=new RoboticsRuntime(root,Path.Combine(root,"reservation"),tools,clock);
            robot.Start("robot");
            check(robot.Status().StopLatched && tools.Calls.Count==0,"Loading Robotics never connects motors or starts motion");
            var c=Configuration();robot.Configure(c);
            check(Rejected(()=>robot.Arm(new())) && tools.Calls.Count==0,"Uncalibrated and disabled robots cannot be armed");
            robot.Task(new("inspect-table"));await Finished(robot);
            check(robot.Status().Job?.State=="completed" && tools.Calls.SequenceEqual(["camera","camera"]),"Autonomous table inspection uses both cameras without motion");
            var captureId=robot.Status().Job!.Id;
            check(robot.Captures(captureId).Length==2 && robot.Capture(captureId,"head-inspection.jpg").Length==4,"Inspection evidence is retrievable through known job captures");
            check(Rejected(()=>robot.Capture(captureId,"../../config.json")),"Camera evidence cannot traverse outside the selected job");
            robot.Task(new("inspect-markers"));await Finished(robot);
            var markerId=robot.Status().Job!.Id;var markers=robot.Markers(markerId);
            check(robot.Status().Job?.State=="completed" && tools.Calls.Last()=="inspect-markers" && robot.Captures(markerId).Length==2
                && markers.SharedIds.SequenceEqual([1]) && markers.Cameras[0].Markers.All(m=>m.DetectedFrames==3),
                "Marker inspection persists repeated head/hand observations and camera evidence without calibration");
            check(!markers.MotorCommandsIssued && !markers.JointCalibrationApproved && !markers.MetricPoseAvailable && !markers.SynchronizedStereoFrames
                && robot.Status().StopLatched && Rejected(()=>robot.Arm(new())),"Readable markers cannot authorize motor motion or imply metric/stereo pose");
            check(Rejected(()=>robot.Markers(captureId)) && Rejected(()=>robot.Markers("../../config")),"Marker reports are restricted to completed marker inspection jobs");
            robot.Configure(c with{ControllerDevice=""});robot.Task(new("inspect-markers"));await Finished(robot);
            check(robot.Status().Job?.State=="completed" && Rejected(()=>robot.Controller(new()))
                && Rejected(()=>robot.Record(new("demo","left","Pick a block"))),"Marker inspection works before controller pairing while recording and controller motion require a selected controller");
            robot.Configure(c);
            MarkerValidation(check);
            var jobsBeforeSnapshot=robot.Jobs().Length;
            await robot.Observation(CancellationToken.None);await robot.Observation(CancellationToken.None);
            check(tools.Calls.Count(call=>call=="dashboard")==1&&robot.Jobs().Length==jobsBeforeSnapshot,
                "Dashboard snapshots share a cache and cannot evict inspection evidence with polling jobs");
            robot.AutoCalibrate();await Finished(robot);
            check(robot.CalibrationAssessment() is {State:"blocked",MotorCommandsIssued:false,JointCalibrationApproved:false}
                &&!File.Exists(Path.Combine(root,"calibration","receipt.json"))&&robot.Status().StopLatched,
                "Automatic calibration assessment performs read-only checks without fabricating approval or moving motors");
            check(Rejected(()=>robot.StartController(new(60)))&&robot.Status().StopLatched,"One-click controller start cannot bypass missing calibration");
            c=c with{MotionEnabled=true};robot.Configure(c);
            check(Rejected(()=>robot.Arm(new())),"Motion enablement does not bypass missing calibration");
            Calibrate(root,c);robot.Configure(c with{MotorLimits=null});
            check(Rejected(()=>robot.Arm(new())),"Motion requires explicit robot-specific resistance limits even with valid calibration");
            robot.Configure(c);robot.Arm(new(5));clock.Now=clock.Now.AddSeconds(6);
            check(Rejected(()=>robot.Controller(new())),"An expired operator session cannot start controller motion");
            await robot.Stop();
            robot.StartController(new(60));await Finished(robot);
            check(tools.Calls.Last()=="controller"&&robot.Status().StopLatched,"One-click controller start arms a bounded session and finishes disarmed");
            var skill=new RobotSkill("red-left","Red block","sort","right","Put the red block in the left bin","/state/policies/red-left",Bin:"left");
            robot.Arm(new());var evaluation=robot.EvaluateSkill(new(skill));await Finished(robot);
            check(robot.Job(evaluation.Id)?.State=="completed" && robot.Status().Skills.Length==0 && robot.Status().StopLatched,"Supervised policy evaluation disarms and does not grant persistent skill approval");
            c=c with{Skills=[skill]};robot.Configure(c);robot.Arm(new());
            check(Rejected(()=>robot.Task(new("sort","Sort red blocks",[new("red block","left","red-left")]))),"Unreviewed policies cannot run sorting tasks");
            await robot.Stop();robot.ReviewSkill(new(skill));robot.Arm(new());
            check(Rejected(()=>robot.Task(new("sort","Sort red blocks",[new("red block","right","red-left")]))),"Sorting rejects a bin that differs from the reviewed skill");
            var sorting=robot.Task(new("sort","Sort red blocks",[new("red block","left","red-left")]));await Finished(robot);
            check(robot.Job(sorting.Id)?.State=="awaiting-verification" && robot.Job(sorting.Id)?.CompletedSkills.SequenceEqual(["red-left"])==true && robot.Status().StopLatched,"Policy completion records evidence and requests placement verification instead of claiming sorting success");
            File.AppendAllText(Path.Combine(root,"calibration/fixture.json")," ");
            check(Rejected(()=>robot.Arm(new())),"Calibration file tampering invalidates motion approval");
            Calibrate(root,c);robot.Arm(new());File.AppendAllText(Path.Combine(root,"calibration/receipt.json")," ");
            check(Rejected(()=>robot.Controller(new())) && robot.Status().StopLatched,"Changing calibration during an armed session cancels that motion authorization");
            Calibrate(root,c);File.AppendAllText(Path.Combine(root,"calibration/receipt.json")," ");robot.Arm(new());
            check(Rejected(()=>robot.Task(new("sort","Sort",[new("red","left","red-left")]))),"A new calibration invalidates old skill reviews");
            await robot.Stop();robot.ReviewSkill(new(skill));robot.Arm(new());
            tools.Block=true;tools.Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending=robot.Task(new("sort","Sort",[new("red","left","red-left")]));await tools.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(Rejected(()=>robot.Emote(new("wave"))) && Rejected(()=>robot.Task(new("inspect-table"))) && Rejected(()=>robot.Task(new("inspect-markers"))),"Controller, cameras, marker inspection and autonomous motion share one exclusive operation gate");
            await robot.Stop();
            check(robot.Job(pending.Id)?.State=="stopped" && robot.Status().StopLatched && Rejected(()=>robot.Task(new("sort","Sort",[new("red","left","red-left")]))),"Stop cancels the adapter and latches motion until explicit rearming");
            var restarted=new RoboticsRuntime(root,Path.Combine(root,"reservation"),new Tools(),clock);restarted.Start("robot");
            check(restarted.Status().StopLatched && restarted.Status().ArmedUntil==null,"App restart never resumes a motor session");
            check(Rejected(()=>robot.Configure(c with{LeftPort="/dev/mem"})),"Robotics setup cannot pass arbitrary host device nodes into its container");
            await robot.Shutdown();
            check(robot.Status().WorkloadId==null && Rejected(()=>robot.Task(new("inspect-table"))),"App shutdown releases robotics and blocks new operations");
            await Endpoints(root,check);
            await FreshDetection(root,check);
            await EmergencyStops(root,check);
            await Container(root,check);
            await ChildCleanup(root,check);

        }
        finally{Directory.Delete(root,true);}
    }
    static async Task FreshDetection(string root,Action<bool,string> check)
    {
        var directory=Path.Combine(root,"fresh-detection");var ports=new[]{Path.Combine(root,"fresh-left"),Path.Combine(root,"fresh-right")};
        foreach(var port in ports)File.WriteAllText(port,"");
        var inventories=new[]{8,9}.Select(count=>Enumerable.Range(1,count).ToDictionary(id=>id,_=>777)).ToArray();
        var container=new RoboticsContainer(directory,(_,args,_,_)=>Task.FromResult(new ProcessResult(0,args.First().EndsWith("discover_buses.py",StringComparison.Ordinal)?JsonSerializer.Serialize(inventories):"")));
        var robot=new RoboticsRuntime(directory,Path.Combine(root,"fresh-reservation"),toolContainer:container,
            deviceInventory:()=>new(ports.Select(p=>new RobotDevice(p,p)).ToArray(),[],[]));
        robot.Start("robot");
        check(!Directory.Exists(directory),"First-time bus discovery starts before any robotics state directory or configuration exists");
        robot.DetectBuses();await Finished(robot);
        check(robot.Status().Job?.State=="completed"&&robot.Detection() is {LeftPort:var left,RightPort:var right}
            &&left==ports[0]&&right==ports[1]&&!robot.Status().Configured&&robot.Status().StopLatched,
            "Fresh bus discovery persists the detected roles without requiring guessed configuration or arming motion");
    }
    static async Task EmergencyStops(string root,Action<bool,string> check)
    {
        var directory=Path.Combine(root,"estop");var tools=new Tools();var c=Configuration(true);
        var robot=new RoboticsRuntime(directory,Path.Combine(directory,"reservation"),tools);
        robot.Start("robot");robot.Configure(c);Calibrate(directory,c);
        tools.Block=true;var job=robot.StartController(new(60));await tools.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopped=robot.EmergencyStop();
        check(stopped is {EmergencyStopLatched:true,StopLatched:true,ArmedUntil:null,Mode:"emergency-stop"}
            &&File.Exists(Path.Combine(directory,"estop-latched")),"E-stop immediately disarms, cancels active control and persists its latch");
        await robot.Stop();
        check(robot.Job(job.Id)?.State=="stopped"&&Rejected(()=>robot.Arm(new()))&&Rejected(()=>robot.StartController(new(60))),
            "E-stop blocks arming and controller starts even with valid calibration");
        var restarted=new RoboticsRuntime(directory,Path.Combine(directory,"restart-reservation"),new Tools());restarted.Start("robot");
        check(restarted.Status().EmergencyStopLatched&&Rejected(()=>restarted.Arm(new())),"E-stop remains latched after app restart");
        tools.Block=false;tools.Dashboard=MotorFeedback(1);robot.ResetEmergencyStop();await Finished(robot);
        check(robot.Status().EmergencyStopLatched&&robot.Status().Job?.State=="failed","Reset cannot clear E-stop while any motor reports torque enabled");
        tools.Dashboard=MotorFeedback(count:16);robot.ResetEmergencyStop();await Finished(robot);
        check(robot.Status().EmergencyStopLatched&&robot.Status().Job?.State=="failed","Reset requires fresh feedback from all seventeen motors");
        tools.Dashboard=null;robot.ResetEmergencyStop();await Finished(robot);
        check(!robot.Status().EmergencyStopLatched&&robot.Status().StopLatched&&robot.Status().ArmedUntil==null
            &&!File.Exists(Path.Combine(directory,"estop-latched"))&&Rejected(()=>robot.Controller(new())),"Successful E-stop reset removes the latch but never resumes or arms motion");
        robot.EmergencyStop();await robot.Stop();tools.Block=true;tools.Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        robot.ResetEmergencyStop();await tools.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));robot.EmergencyStop();await robot.Stop();
        check(robot.Status().EmergencyStopLatched&&robot.Status().Job?.State=="stopped","Pressing E-stop during reset cancels reset and retains the latch");
        var failureDirectory=Path.Combine(root,"stop-failure");Directory.CreateDirectory(Path.Combine(failureDirectory,".build"));
        File.WriteAllText(Path.Combine(failureDirectory,".build","devices.json"),"{}");var engineFailure=true;
        var container=new RoboticsContainer(failureDirectory,(_,_,_,_)=>Task.FromResult(new ProcessResult(engineFailure?125:0,"Unavailable engine")));
        var failedStop=new RoboticsRuntime(failureDirectory,Path.Combine(failureDirectory,"reservation"),toolContainer:container);
        failedStop.Start("robot");failedStop.Configure(c);Calibrate(failureDirectory,c);failedStop.EmergencyStop();
        try{await failedStop.Stop();}catch(InvalidOperationException){}
        check(failedStop.Status().EmergencyStopLatched&&failedStop.Status().Problems.Any(p=>p.Contains("cleanup failed"))
            &&Rejected(()=>failedStop.ResetEmergencyStop()),"Local tool stop errors stay visible and prevent E-stop reset");
        engineFailure=false;await failedStop.Stop();
        check(!failedStop.Status().Problems.Any(p=>p.Contains("cleanup failed"))&&failedStop.Status().EmergencyStopLatched,
            "Retrying a successful stop clears cleanup failure without clearing E-stop");
    }
    static void MarkerValidation(Action<bool,string> check)
    {
        var fixture=MarkerResult();
        var head=fixture.Cameras[0];var first=head.Frames[0];var duplicate=first.Detections[1];
        var duplicateFrame=first with{Detections=[..first.Detections,duplicate]};
        var duplicated=fixture with{Cameras=[head with{Frames=[duplicateFrame,..head.Frames.Skip(1)]},fixture.Cameras[1]]};
        var observed=RobotMarkerSurvey.Read(MarkerJson(duplicated)).Report;
        check(observed.Cameras[0].Frames[0].AmbiguousDuplicateIds.SequenceEqual([1])
            && observed.Cameras[0].Markers.Single(m=>m.Id==1).DetectedFrames==2 && observed.SharedIds.Length==0,
            "Duplicate tag copies are marked ambiguous and excluded from shared camera references");
        foreach(var bad in new[]{duplicate with{Hamming=1},duplicate with{Id=2115},duplicate with{Center=[-1,10]},
            duplicate with{Corners=[[10,10],[10,10],[10,10],[10,10]]}})
        {
            var frame=first with{Detections=[bad]};var invalid=fixture with{Cameras=[head with{Frames=[frame,..head.Frames.Skip(1)]},fixture.Cameras[1]]};
            check(Rejected(()=>RobotMarkerSurvey.Read(MarkerJson(invalid))),"Invalid or corrected marker detections cannot become survey evidence");
        }
        var stale=fixture with{Cameras=[head with{Frames=[first,first,first]},fixture.Cameras[1]]};
        check(Rejected(()=>RobotMarkerSurvey.Read(MarkerJson(stale))),"Repeated timestamps cannot masquerade as separate marker observations");
    }
    public static async Task LiveMarkers(string input,string output,Action<bool,string> check)
    {
        output=Path.GetFullPath(output);
        if(!output.Split(Path.DirectorySeparatorChar).Contains(".build"))throw new InvalidOperationException("Live camera evidence must stay under .build.");
        Directory.CreateDirectory(output);
        using var data=JsonDocument.Parse(await File.ReadAllTextAsync(input));
        var tools=new Tools{MarkerObservations=data.RootElement.Clone()};
        var robot=new RoboticsRuntime(output,Path.Combine(output,"reservation"),tools);
        robot.Start("robot");robot.Configure(Configuration() with{ControllerDevice=""});
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app=builder.Build();Xur.Robot.RoboticsEndpoints.MapRobotics(app,robot);await app.StartAsync();
        try
        {
            using var http=new HttpClient{BaseAddress=new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
            using var accepted=await http.PostAsJsonAsync("/api/tasks",new{kind="inspect-markers"});
            var job=await accepted.Content.ReadFromJsonAsync<RobotJob>();check(accepted.StatusCode==System.Net.HttpStatusCode.Accepted && job!=null,"Live adapter observations accepted through the task API");
            await Finished(robot);
            check(robot.Job(job!.Id)?.State=="completed","Live adapter geometry validated and persisted by the .NET runtime: "+robot.Job(job.Id)?.Detail);
            var report=await http.GetFromJsonAsync<RobotMarkerReport>("/api/jobs/"+job.Id+"/markers");
            check(report?.Cameras.Length==2 && report.Cameras.All(c=>c.Frames.Length==3)
                && !report.MetricPoseAvailable && !report.JointCalibrationApproved && robot.Status().StopLatched,
                "Live report preserves both camera frame sets and keeps calibration and motion unapproved");
            var captures=await http.GetFromJsonAsync<string[]>("/api/jobs/"+job.Id+"/captures");
            foreach(var name in captures!)
            {
                var jpeg=await http.GetByteArrayAsync("/api/jobs/"+job.Id+"/captures/"+name);
                check(jpeg.SequenceEqual(robot.Capture(job.Id,name)),"Live camera JPEG served through its job capture endpoint: "+name);
                await File.WriteAllBytesAsync(Path.Combine(output,name),jpeg);
            }
            await File.WriteAllTextAsync(Path.Combine(output,"live-report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true}));
        }
        finally{await app.StopAsync();}
    }
    static async Task Endpoints(string root,Action<bool,string> check)
    {
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app=builder.Build();var tools=new Tools();var robot=new RoboticsRuntime(Path.Combine(root,"api"),Path.Combine(root,"api-reservation"),tools);
        robot.Start("robot");robot.Configure(Configuration());
        Xur.Robot.RoboticsEndpoints.MapRobotics(app,robot);await app.StartAsync();
        using var http=new HttpClient{BaseAddress=new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
        using var invalid=await http.PostAsJsonAsync("/api/tasks",new{kind="inspect-table",motors=new{shoulder=50}});
        check(invalid.StatusCode==System.Net.HttpStatusCode.BadRequest && tools.Calls.Count==0,"Public robotics requests reject motor fields before adapter invocation");
        using var invalidCalibration=await http.PostAsJsonAsync("/api/auto-calibrate",new{motors=new{shoulder=50}});
        using var invalidController=await http.PostAsJsonAsync("/api/start-controller",new{seconds=60,motors=new{shoulder=50}});
        check(invalidCalibration.StatusCode==System.Net.HttpStatusCode.BadRequest&&invalidController.StatusCode==System.Net.HttpStatusCode.BadRequest&&tools.Calls.Count==0,
            "Dashboard calibration and controller APIs reject injected motor instructions");
        using var accepted=await http.PostAsJsonAsync("/api/tasks",new{kind="inspect-table"});var body=await accepted.Content.ReadFromJsonAsync<RobotJob>();
        check(accepted.StatusCode==System.Net.HttpStatusCode.Accepted && body?.Id.Length==32,"Minimal API returns an inspectable accepted job instead of dropping its response");
        await Finished(robot);
        using var markerAccepted=await http.PostAsJsonAsync("/api/tasks",new{kind="inspect-markers"});
        var markerJob=await markerAccepted.Content.ReadFromJsonAsync<RobotJob>();await Finished(robot);
        using var report=await http.GetAsync("/api/jobs/"+markerJob!.Id+"/markers");
        var observations=await report.Content.ReadFromJsonAsync<RobotMarkerReport>();
        check(markerAccepted.StatusCode==System.Net.HttpStatusCode.Accepted && report.IsSuccessStatusCode
            && observations?.SharedIds.SequenceEqual([1])==true && !observations.JointCalibrationApproved,
            "Minimal API exposes persisted marker observations while calibration remains unapproved");
        using var absent=await http.GetAsync("/api/jobs/"+body!.Id+"/markers");
        check(absent.StatusCode==System.Net.HttpStatusCode.NotFound,"Non-marker jobs cannot expose marker reports");
        using var invalidReset=await http.PostAsJsonAsync("/api/reset-estop",new{force=true});
        using var stop=await http.PostAsJsonAsync("/api/estop",new{});
        var status=await stop.Content.ReadFromJsonAsync<RobotStatus>();await robot.Stop();
        check(invalidReset.StatusCode==System.Net.HttpStatusCode.BadRequest&&stop.StatusCode==System.Net.HttpStatusCode.Accepted
            &&status?.EmergencyStopLatched==true,"Minimal API acknowledges E-stop and rejects reset bypass fields");
        using var reset=await http.PostAsJsonAsync("/api/reset-estop",new{});await Finished(robot);
        check(reset.StatusCode==System.Net.HttpStatusCode.Accepted&&!robot.Status().EmergencyStopLatched&&robot.Status().StopLatched,
            "Minimal API resets E-stop through a checked job while leaving motion disarmed");
        await app.StopAsync();
    }
    static async Task ChildCleanup(string root,Action<bool,string> check)
    {
        var directory=Path.Combine(root,"cleanup-tools");Directory.CreateDirectory(directory);var pidFile=Path.Combine(directory,"child.pid");
        await File.WriteAllTextAsync(Path.Combine(directory,"bridge.py"),
            "import os,sys,time\nfrom pathlib import Path\nif len(sys.argv)>1: time.sleep(30); raise SystemExit(1)\nsys.stdin.readline()\nPath("+JsonSerializer.Serialize(pidFile)+").write_text(str(os.getpid()))\ntime.sleep(30)\n");
        var previousTools=Environment.GetEnvironmentVariable("XUR_ROBOT_TOOLS");var previousPython=Environment.GetEnvironmentVariable("XUR_ROBOT_PYTHON");
        Environment.SetEnvironmentVariable("XUR_ROBOT_TOOLS",directory);Environment.SetEnvironmentVariable("XUR_ROBOT_PYTHON","python3");
        System.Diagnostics.Process? child=null;using var cancellation=new CancellationTokenSource();
        try
        {
            var running=new LeRobotTools().Run(Configuration(),"camera",new RobotCameraRequest("head"),60,cancellation.Token);
            for(var i=0;i<100&&!File.Exists(pidFile);i++)await Task.Delay(20);
            if(!File.Exists(pidFile))throw new Exception("Fake bridge failed to start");
            child=System.Diagnostics.Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidFile)));cancellation.Cancel();
            var failed=false;try{await running.WaitAsync(TimeSpan.FromSeconds(20));}catch(OperationCanceledException){failed=true;}
            check(failed&&child.HasExited,"Cancellation still kills the original bridge when its software-stop helper times out");
        }
        finally
        {
            if(child is {HasExited:false}){child.Kill(entireProcessTree:true);await child.WaitForExitAsync();}child?.Dispose();
            Environment.SetEnvironmentVariable("XUR_ROBOT_TOOLS",previousTools);Environment.SetEnvironmentVariable("XUR_ROBOT_PYTHON",previousPython);
        }
    }
    static async Task Container(string root,Action<bool,string> check)
    {
        var files=Enumerable.Range(0,5).Select(i=>Path.Combine(root,"device-"+i)).ToArray();foreach(var file in files)File.WriteAllText(file,"");
        var c=new RoboticsConfiguration("fixture",files[0],files[1],files[2],files[3],files[4],[]);
        List<string[]> calls=[];RoboticsConfiguration? aliases=null;
        var container=new RoboticsContainer(Path.Combine(root,"container"),(executable,args,seconds,token)=>
        {check(executable==RoboticsContainer.Python,"Tools run through the local Python interpreter");calls.Add(args.ToArray());return Task.FromResult(new ProcessResult(0,""));},prepareAliases:selected=>aliases=selected);
        await container.Prepare(c,CancellationToken.None);
        check(calls.Single().SequenceEqual([Path.Combine(RoboticsContainer.ToolsDirectory,"bridge.py"),"--idle"])
            &&aliases==c&&File.Exists(Path.Combine(root,"container/.build/devices.json")),
            "Preparation checks local operation ownership and persists selected device aliases without a container engine");
        calls.Clear();await container.Prepare(c with{ControllerDevice=""},CancellationToken.None);
        check(aliases?.ControllerDevice=="","Camera inspection does not require an unpaired controller");
        calls.Clear();
        var discovery=new RoboticsContainer(root,(_,args,_,_)=>
        {
            calls.Add(args.ToArray());
            return Task.FromResult(new ProcessResult(0,JsonSerializer.Serialize(new[]{Enumerable.Range(1,9).ToDictionary(id=>id,_=>777),Enumerable.Range(1,8).ToDictionary(id=>id,_=>777)})+"\nTool warning on stderr"));
        });
        var detected=await discovery.DetectBuses(files.Take(2).ToArray(),CancellationToken.None);
        check(detected.LeftPort==files[1]&&detected.RightPort==files[0]
            &&calls.Single().SequenceEqual([Path.Combine(RoboticsContainer.ToolsDirectory,"discover_buses.py"),files[0],files[1]]),
            "Read-only discovery invokes upstream tools locally, preserves adapter identities and tolerates trailing diagnostics");
        calls.Clear();var busy=new RoboticsContainer(root,(_,args,_,_)=>
        {calls.Add(args.ToArray());return Task.FromResult(new ProcessResult(1,"busy"));},prepareAliases:_=>throw new Exception("Aliases changed while busy"));
        bool rejected=false;try{await busy.Prepare(c,CancellationToken.None);}catch(InvalidOperationException){rejected=true;}
        check(rejected&&calls.Count==1,"Preparation cannot change aliases during an active interactive operation");
        calls.Clear();File.WriteAllText(Path.Combine(root,"container","reservation"),"old controller");
        var recovered=new RoboticsRuntime(Path.Combine(root,"container"),Path.Combine(root,"container","reservation"),toolContainer:new RoboticsContainer(root,(_,args,_,_)=>
        {calls.Add(args.ToArray());return Task.FromResult(new ProcessResult(0,""));}));
        await recovered.Recover();
        check(calls.Any(a=>a.Last()=="--stop")&&!File.Exists(Path.Combine(root,"container","reservation"))&&recovered.Status().StopLatched,
            "App recovery stops surviving local tools and releases stale controller ownership without resuming motion");
    }
}
