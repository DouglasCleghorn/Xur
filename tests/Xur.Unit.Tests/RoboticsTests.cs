using System.Security.Cryptography;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xur.Agent;
using Xur.Control;
using Xur.Domain;

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
    static readonly Recipe Recipe=new("xlerobot","Robot","host:xlerobot",[],0,"","CPU",0,0,"",Kind:"Robotics",Engine:"XLeRobot");
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
            var workload=new Workload("robot","Robot",Recipe,[],"robot");
            ProfilePolicy.Validate(new("robot","Robot",1,[workload]),new("",[],[]));
            var desktop=new Workload("desktop","Desktop",new("gaming-workstation","Desktop","host:plasma",[],0,"","Display",1,0,"",Kind:"Workstation",Engine:"Plasma"),["gpu"],"desktop");
            check(Rejected(()=>ProfilePolicy.Validate(new("robot","Robot",1,[workload,desktop]),new("",[],[]))),"Robotics rejects shared workstation/controller ownership");
            check(Rejected(()=>ProfilePolicy.ValidateRecipe(Recipe with{Command=["drive"]})),"Robotics recipes cannot introduce arbitrary commands");
            var instance=robot.Start(workload);
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
            var restarted=new RoboticsRuntime(root,Path.Combine(root,"reservation"),new Tools(),clock);restarted.Start(workload);
            check(restarted.Status().StopLatched && restarted.Status().ArmedUntil==null,"Agent restart never resumes a motor session");
            check(Rejected(()=>robot.Configure(c with{LeftPort="/dev/mem"})),"Robotics setup cannot pass arbitrary host device nodes into its container");
            await robot.Unload(new(workload.Id,instance.InstanceId,instance.Pid,instance.BootId));
            check(robot.Inspect(workload)==null && Rejected(()=>robot.Task(new("inspect-table"))),"Profile unload releases robotics and blocks new operations");
            await Endpoints(root,check);
            await EmergencyStops(root,check);
            await Container(root,check);
            foreach(var path in new[]{"/api/robotics/arm","/api/robotics/reset-estop","/api/robotics/auto-calibrate","/api/robotics/start-controller","/api/robotics/configure","/api/robotics/prepare","/api/robotics/detect-buses","/api/robotics/train","/api/robotics/record","/api/robotics/skills/evaluate","/api/robotics/skills/review","/api/power/reboot"})
                check(!ApiKeys.Allows("robotics","POST",path),"Robotics keys cannot change operator setup: "+path);
            check(ApiKeys.Allows("robotics","POST","/api/robotics/tasks") && ApiKeys.Allows("robotics","POST","/api/robotics/stop") && ApiKeys.Allows("robotics","GET","/api/robotics/cameras/head"),"Robotics keys can invoke bounded tasks and see camera feedback");
            check(ApiKeys.Allows("robotics","POST","/api/robotics/estop"),"Robotics keys may latch E-stop but cannot reset it");
            check(ApiKeys.Allows("robotics","GET","/api/robotics/jobs/"+new string('a',32)+"/markers")
                && !ApiKeys.Allows("robotics","GET","/api/robotics/jobs/../../config/markers"),"Robotics keys can read known marker reports without arbitrary file access");
            check(!ApiKeys.Allows("diagnostics","POST","/api/robotics/tasks") && !ApiKeys.Allows("testing","POST","/api/robotics/tasks"),"Existing read/test keys do not gain robot motion access");
        }
        finally{Directory.Delete(root,true);}
    }
    static async Task EmergencyStops(string root,Action<bool,string> check)
    {
        var directory=Path.Combine(root,"estop");var tools=new Tools();var c=Configuration(true);
        var robot=new RoboticsRuntime(directory,Path.Combine(directory,"reservation"),tools);
        var workload=new Workload("robot","Robot",Recipe,[],"robot");robot.Start(workload);robot.Configure(c);Calibrate(directory,c);
        tools.Block=true;var job=robot.StartController(new(60));await tools.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopped=robot.EmergencyStop();
        check(stopped is {EmergencyStopLatched:true,StopLatched:true,ArmedUntil:null,Mode:"emergency-stop"}
            &&File.Exists(Path.Combine(directory,"estop-latched")),"E-stop immediately disarms, cancels active control and persists its latch");
        await robot.Stop();
        check(robot.Job(job.Id)?.State=="stopped"&&Rejected(()=>robot.Arm(new()))&&Rejected(()=>robot.StartController(new(60))),
            "E-stop blocks arming and controller starts even with valid calibration");
        var restarted=new RoboticsRuntime(directory,Path.Combine(directory,"restart-reservation"),new Tools());restarted.Start(workload);
        check(restarted.Status().EmergencyStopLatched&&Rejected(()=>restarted.Arm(new())),"E-stop remains latched after agent restart");
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
        robot.Start(new("robot","Robot",Recipe,[],"robot"));robot.Configure(Configuration() with{ControllerDevice=""});
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app=builder.Build();Xur.Agent.RoboticsEndpoints.MapRobotics(app,robot);await app.StartAsync();
        try
        {
            using var http=new HttpClient{BaseAddress=new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
            using var accepted=await http.PostAsJsonAsync("/robotics/tasks",new{kind="inspect-markers"});
            var job=await accepted.Content.ReadFromJsonAsync<RobotJob>();check(accepted.StatusCode==System.Net.HttpStatusCode.Accepted && job!=null,"Live adapter observations accepted through the task API");
            await Finished(robot);
            check(robot.Job(job!.Id)?.State=="completed","Live adapter geometry validated and persisted by the .NET runtime: "+robot.Job(job.Id)?.Detail);
            var report=await http.GetFromJsonAsync<RobotMarkerReport>("/robotics/jobs/"+job.Id+"/markers");
            check(report?.Cameras.Length==2 && report.Cameras.All(c=>c.Frames.Length==3)
                && !report.MetricPoseAvailable && !report.JointCalibrationApproved && robot.Status().StopLatched,
                "Live report preserves both camera frame sets and keeps calibration and motion unapproved");
            var captures=await http.GetFromJsonAsync<string[]>("/robotics/jobs/"+job.Id+"/captures");
            foreach(var name in captures!)
            {
                var jpeg=await http.GetByteArrayAsync("/robotics/jobs/"+job.Id+"/captures/"+name);
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
        robot.Start(new("robot","Robot",Recipe,[],"robot"));robot.Configure(Configuration());
        Xur.Agent.RoboticsEndpoints.MapRobotics(app,robot);await app.StartAsync();
        using var http=new HttpClient{BaseAddress=new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
        using var invalid=await http.PostAsJsonAsync("/robotics/tasks",new{kind="inspect-table",motors=new{shoulder=50}});
        check(invalid.StatusCode==System.Net.HttpStatusCode.BadRequest && tools.Calls.Count==0,"Public robotics requests reject motor fields before adapter invocation");
        using var invalidCalibration=await http.PostAsJsonAsync("/robotics/auto-calibrate",new{motors=new{shoulder=50}});
        using var invalidController=await http.PostAsJsonAsync("/robotics/start-controller",new{seconds=60,motors=new{shoulder=50}});
        check(invalidCalibration.StatusCode==System.Net.HttpStatusCode.BadRequest&&invalidController.StatusCode==System.Net.HttpStatusCode.BadRequest&&tools.Calls.Count==0,
            "Dashboard calibration and controller APIs reject injected motor instructions");
        using var accepted=await http.PostAsJsonAsync("/robotics/tasks",new{kind="inspect-table"});var body=await accepted.Content.ReadFromJsonAsync<RobotJob>();
        check(accepted.StatusCode==System.Net.HttpStatusCode.Accepted && body?.Id.Length==32,"Minimal API returns an inspectable accepted job instead of dropping its response");
        await Finished(robot);
        using var markerAccepted=await http.PostAsJsonAsync("/robotics/tasks",new{kind="inspect-markers"});
        var markerJob=await markerAccepted.Content.ReadFromJsonAsync<RobotJob>();await Finished(robot);
        using var report=await http.GetAsync("/robotics/jobs/"+markerJob!.Id+"/markers");
        var observations=await report.Content.ReadFromJsonAsync<RobotMarkerReport>();
        check(markerAccepted.StatusCode==System.Net.HttpStatusCode.Accepted && report.IsSuccessStatusCode
            && observations?.SharedIds.SequenceEqual([1])==true && !observations.JointCalibrationApproved,
            "Minimal API exposes persisted marker observations while calibration remains unapproved");
        using var absent=await http.GetAsync("/robotics/jobs/"+body!.Id+"/markers");
        check(absent.StatusCode==System.Net.HttpStatusCode.NotFound,"Non-marker jobs cannot expose marker reports");
        using var invalidReset=await http.PostAsJsonAsync("/robotics/reset-estop",new{force=true});
        using var stop=await http.PostAsJsonAsync("/robotics/estop",new{});
        var status=await stop.Content.ReadFromJsonAsync<RobotStatus>();await robot.Stop();
        check(invalidReset.StatusCode==System.Net.HttpStatusCode.BadRequest&&stop.StatusCode==System.Net.HttpStatusCode.Accepted
            &&status?.EmergencyStopLatched==true,"Minimal API acknowledges E-stop and rejects reset bypass fields");
        using var reset=await http.PostAsJsonAsync("/robotics/reset-estop",new{});await Finished(robot);
        check(reset.StatusCode==System.Net.HttpStatusCode.Accepted&&!robot.Status().EmergencyStopLatched&&robot.Status().StopLatched,
            "Minimal API resets E-stop through a checked job while leaving motion disarmed");
        await app.StopAsync();
    }
    static async Task Container(string root,Action<bool,string> check)
    {
        var files=Enumerable.Range(0,5).Select(i=>Path.Combine(root,"device-"+i)).ToArray();foreach(var file in files)File.WriteAllText(file,"");
        var c=new RoboticsConfiguration("fixture",files[0],files[1],files[2],files[3],files[4],[]);
        List<string[]> calls=[];
        var container=new RoboticsContainer(Path.Combine(root,"container"),(executable,args,seconds,token)=>
        {var values=args.ToArray();calls.Add(values);return Task.FromResult(new ProcessResult(values.Take(2).SequenceEqual(["container","exists"])?1:0,""));});
        await container.Prepare(c,CancellationToken.None);
        var create=calls.Single(a=>a[0]=="run");
        check(create.Contains("--network=none") && create.Contains("--cap-drop=ALL") && create.Contains("--restart=no") && !create.Contains("--privileged") && create.Count(a=>a=="--device")==5,"Robotics container uses only the selected five devices, no privileges, no network and no automatic restart");
        calls.Clear();await container.Prepare(c with{ControllerDevice=""},CancellationToken.None);
        check(calls.Single(a=>a[0]=="run").Count(a=>a=="--device")==4
            && !calls.Single(a=>a[0]=="run").Any(a=>a.Contains("/dev/xbox",StringComparison.Ordinal)),"Preparing camera inspection does not require an unpaired controller device");
        calls.Clear();
        var discovery=new RoboticsContainer(root,(_,args,_,_)=>
        {
            var values=args.ToArray();calls.Add(values);
            return Task.FromResult(new ProcessResult(0,values[0]=="run"?
                JsonSerializer.Serialize(new[]{Enumerable.Range(1,9).ToDictionary(id=>id,_=>777),Enumerable.Range(1,8).ToDictionary(id=>id,_=>777)})+"\nPodman warning on stderr":""));
        });
        var detected=await discovery.DetectBuses(files.Take(2).ToArray(),CancellationToken.None);
        var discoverCall=calls.Single(a=>a[0]=="run");
        check(detected.LeftPort==files[1] && detected.RightPort==files[0] && discoverCall.Count(a=>a=="--device")==2
            && discoverCall.Contains("--network=none") && discoverCall.Contains("--cap-drop=ALL") && !discoverCall.Contains("--privileged"),
            "Discovery maps reversed inventories using only two serial devices and tolerates separate stderr warnings");
        check(calls.Last().Take(3).SequenceEqual(["rm","--force","--ignore"]),"Discovery always removes its temporary container");
        var failed=new RoboticsContainer(root,(_,_,_,_)=>Task.FromResult(new ProcessResult(125,"cache failed")));
        bool rejected=false;try{await failed.Prepare(c,CancellationToken.None);}catch(InvalidOperationException){rejected=true;}
        check(rejected,"Container cache inspection failures cannot silently fall back to Docker Hub");
        calls.Clear();var busy=new RoboticsContainer(root,(_,args,_,_)=>
        {
            var values=args.ToArray();calls.Add(values);
            return Task.FromResult(new ProcessResult(values[0]=="exec"?1:0,values[0]=="inspect"?"true":""));
        });
        rejected=false;try{await busy.Prepare(c,CancellationToken.None);}catch(InvalidOperationException){rejected=true;}
        check(rejected && !calls.Any(a=>a[0]=="rm"),"Container preparation cannot remove an active interactive calibration");
        calls.Clear();File.WriteAllText(Path.Combine(root,"container","reservation"),"old controller");
        var recovered=new RoboticsRuntime(Path.Combine(root,"container"),Path.Combine(root,"container","reservation"),toolContainer:new RoboticsContainer(root,(_,args,_,_)=>
        {calls.Add(args.ToArray());return Task.FromResult(new ProcessResult(0,""));}));
        await recovered.Recover();
        check(calls.Any(a=>a[0]=="stop") && !File.Exists(Path.Combine(root,"container","reservation")) && recovered.Status().StopLatched,"Agent recovery stops a surviving tools container and removes stale controller ownership");
        await WebContainer(root,check);
    }
    static async Task WebContainer(string root,Action<bool,string> check)
    {
        var runDirectory=Path.GetFullPath(".build/rw-"+Guid.NewGuid().ToString("N")[..8]);var socket=Path.Combine(runDirectory,"robot-web","app.sock");
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.ListenUnixSocket(socket));
        await using var app=builder.Build();app.MapGet("/robot/health",()=>Microsoft.AspNetCore.Http.Results.Ok());
        List<string[]> calls=[];
        var container=new RoboticsWebContainer(runDirectory,async(_,arguments,_,_)=>
        {
            var args=arguments.ToArray();calls.Add(args);
            if(args[0]=="run")await app.StartAsync();
            return new(args[0]=="container"?1:0,"");
        });
        await container.Start();
        var create=calls.Single(args=>args[0]=="run");
        check(create.Contains("--network=none")&&create.Contains("--read-only")&&create.Contains("--cap-drop=ALL")
            &&!create.Contains("--publish")&&!create.Contains("--device")&&!create.Any(arg=>arg.Contains("podman.sock",StringComparison.Ordinal)),
            "Web container uses only private sockets, with no network, hardware, published ports or container-engine socket");
        check(!await container.Running(),"A missing dashboard container is observed as stopped");
        await app.StopAsync();
        var failed=new RoboticsWebContainer(runDirectory,(_,_,_,_)=>Task.FromResult(new ProcessResult(125,"inspection error")));
        bool rejected=false;try{await failed.Start();}catch(InvalidOperationException){rejected=true;}
        check(rejected,"Web image observation failures do not attempt registry fallbacks");
    }
}
