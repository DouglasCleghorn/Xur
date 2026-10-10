using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xur.Robot;

static class CameraCapabilityTests
{
    const string Controls = """
        User Controls
           brightness 0x00980900 (int) : min=-64 max=64 step=1 default=0 value=-1 flags=has-min-max
           power_line_frequency 0x00980918 (menu) : min=0 max=2 default=1 value=2 (60 Hz)
              0: Disabled
              1: 50 Hz
              2: 60 Hz
           region_of_interest_rectangle 0x00981ae1 (rect) : value=(0,0)/964x724 flags=has-payload, has-min-max
           region_of_interest_auto_ctrls 0x00981ae2 (bitmask): max=0x00000001 default=0x00000001 value=0x00000001 flags=has-min-max

        Camera Controls
           exposure_time_absolute 0x009a0902 (int) : min=3 max=2047 step=1 default=166 value=166 flags=inactive, has-min-max
           synthetic_future_control 0x009a0999 (mystery) : value=unknown elems=4 flags=read-only
        """;
    const string Formats = """
        ioctl: VIDIOC_ENUM_FMT
           Type: Video Capture
           [0]: 'MJPG' (Motion-JPEG, compressed)
              Size: Discrete 1920x1080
                 Interval: Discrete 0.033s (30.000 fps)
                 Interval: Discrete 0.067s (15.000 fps)
              Size: Discrete 640x480
                 Interval: Discrete 0.033s (30.000 fps)
           [1]: 'YUYV' (YUYV 4:2:2)
              Size: Discrete 1920x1080
                 Interval: Discrete 0.200s (5.000 fps)
           [2]: 'GREY' (8-bit Greyscale)
              Size: Stepwise 320x240 - 1920x1080 with step 16/8
                 Interval: Continuous 0.033s - 1.000s
        """;
    const string Driver = """
        Driver Info:
            Driver name      : uvcvideo
            Card type        : Synthetic capture fixture
            Bus info         : usb-fixture
            Driver version   : 7.2.7
        Media Driver Info:
            Driver name      : different-media-driver
        Format Video Capture:
            Width/Height     : 640/480
            Pixel Format     : 'YUYV' (YUYV 4:2:2)
        Crop Capability Video Capture:
            Bounds           : Left 0, Top 0, Width 640, Height 480
        Streaming Parameters Video Capture:
            Frames per second: 30.000 (30/1)
        """;
    static readonly RobotDevice Camera = new("/dev/v4l/by-path/pci-fixture-video-index0","Camera fixture");
    static RoboticsConfiguration Configuration()=>new("fixture","/dev/serial/by-id/left","/dev/serial/by-id/right","",Camera.Path,"/dev/v4l/by-id/hand-video-index0",[]);
    static bool Rejected(Action action){try{action();return false;}catch(InvalidOperationException){return true;}}
    static Task<ProcessResult> Query(string _,IEnumerable<string> args,int __,CancellationToken ___)=>Task.FromResult(new ProcessResult(0,args.Last() switch{"--list-ctrls-menus"=>Controls,"--list-formats-ext"=>Formats,_=>Driver}));
    public static async Task Run(Action<bool,string> check)
    {
        var controls=RobotCameraCapabilities.ParseControls(Controls.Replace("\n","\r\n"));
        check(controls.Length==6&&controls[0].Name=="brightness"&&controls[0].Id=="0x00980900"&&controls[0].Type=="int"
            &&controls[0].Minimum==-64&&controls[0].Maximum==64&&controls[0].Step==1&&controls[0].Default==0&&controls[0].Current=="-1",
            "Camera controls preserve actual names, IDs, scalar type, signed ranges, step, default and current value");
        check(controls[1].Menu.Length==3&&controls[1].Menu[2]==new RobotCameraMenuEntry(2,"60 Hz")&&controls[1].Current=="2 (60 Hz)",
            "Camera control menu labels remain associated with their control without discarding the reported current label");
        check(controls[2].PayloadUninterpreted&&controls[2].Current=="(0,0)/964x724"&&controls[2].Raw.Contains("has-payload")&&controls[5].PayloadUninterpreted&&controls[5].Attributes["elems"]=="4",
            "ROI payloads and unknown driver control types retain raw data without invented crop semantics");
        check(controls[3].Maximum==1&&controls[3].Default==1&&controls[3].Current=="0x00000001"&&controls[4].Flags.Contains("inactive"),
            "Hex bitmasks and inactive driver flags survive structured parsing");
        var formats=RobotCameraCapabilities.ParseFormats(Formats);
        check(formats.Length==3&&formats[0].Index==0&&formats[0].PixelFormat=="MJPG"&&formats[0].Sizes[0].Width==1920&&formats[0].Sizes[0].Height==1080,
            "Camera format index zero, pixel format and discrete resolution are preserved");
        check(formats[0].Sizes[0].Intervals.Length==2&&formats[0].Sizes[0].Intervals[0].FramesPerSecond==30&&formats[1].Sizes[0].Intervals[0].FramesPerSecond==5,
            "Camera frame rates remain specific to each format and resolution");
        check(formats[2].Sizes[0].Kind=="Stepwise"&&formats[2].Sizes[0].Width==null&&formats[2].Sizes[0].Raw.Contains("step 16/8")
            &&formats[2].Sizes[0].Intervals[0].FramesPerSecond==null&&formats[2].Sizes[0].Intervals[0].Raw.Contains("1.000s"),
            "Non-discrete sizes and frame intervals remain raw instead of fabricated enumerations");
        var driver=RobotCameraCapabilities.ParseDriver(Driver);
        check(driver.Name=="uvcvideo"&&driver.Card=="Synthetic capture fixture"&&driver.Version=="7.2.7"&&driver.CurrentMode==new RobotCameraCurrentMode(640,480,"YUYV",30),
            "Camera driver identity and reported current capture mode are parsed separately from media/crop sections");
        check(RobotCameraCapabilities.ParseControls("not understood").Length==0&&RobotCameraCapabilities.ParseFormats("not understood").Length==0
            &&RobotCameraCapabilities.ParseDriver("not understood").CurrentMode==null,
            "Unknown driver output does not manufacture controls or capture modes");
        var calls=new List<string[]>();
        var inspector=new RobotCameraCapabilities((exe,args,seconds,token)=>
        {calls.Add([exe,..args]);check(seconds==5,"Camera driver interrogation has a fixed per-query deadline");return Query(exe,args,seconds,token);});
        var report=await inspector.Read([Camera,Camera],Configuration(),CancellationToken.None);
        check(report.Devices.Length==1&&report.Devices[0].SelectedRoles.SequenceEqual(["head"])&&!report.CameraSettingsChanged&&!report.MotorCommandsIssued,
            "Capability inventory deduplicates paths and derives roles only from saved setup without changes or motor commands");
        check(calls.Count==3&&calls.All(c=>c.Length==4&&c[0]=="/usr/bin/v4l2-ctl"&&c[1]=="--device"&&c[2]==Camera.Path)
            &&calls.Select(c=>c[3]).SequenceEqual(["--list-ctrls-menus","--list-formats-ext","--all"]),
            "Only the fixed executable, discovered capture alias and three read-only v4l2-ctl queries are invoked");
        var copy=RobotJson.Deserialize<RobotCameraCapabilityReport>(RobotJson.Serialize(report))!;
        check(copy.Devices[0].Controls[2].Current=="(0,0)/964x724"&&copy.Devices[0].Queries[0].Raw==Controls,
            "Source-generated JSON roundtrips structured capabilities and copyable raw driver output");
        var missing=new RobotCameraCapabilities((_,_,_,_)=>throw new Win32Exception("v4l2-ctl missing"));
        var missingReport=await missing.Read([Camera],null,CancellationToken.None);
        check(missingReport.Devices[0].State=="unavailable"&&missingReport.Devices[0].Queries.All(q=>q.ExitCode==null&&q.Problem!.Contains("missing")),
            "Missing v4l2-ctl remains an explicit per-device and per-query unavailable report");
        var errors=new RobotCameraCapabilities((_,args,_,_)=>Task.FromResult(new ProcessResult(args.Last()=="--list-formats-ext"?1:0,args.Last()=="--list-formats-ext"?"Permission denied":Controls)));
        var errorReport=await errors.Read([Camera],null,CancellationToken.None);
        check(errorReport.Devices[0].State=="partial"&&errorReport.Devices[0].Queries[1].State=="failed"&&errorReport.Devices[0].Queries[1].ExitCode==1&&errorReport.Devices[0].Queries[1].Raw=="Permission denied",
            "One failed camera query remains visible without discarding other controls or its raw error");
        var timeout=new RobotCameraCapabilities((_,_,_,_)=>throw new OperationCanceledException("Internal timeout"));
        check((await timeout.Read([Camera],null,CancellationToken.None)).Devices[0].Queries.All(q=>q.Problem!.Contains("timed out")),
            "Camera helper deadline failures become unavailable reports");
        using(var canceled=new CancellationTokenSource())
        {
            canceled.Cancel();var propagated=false;
            try{await timeout.Read([Camera],null,canceled.Token);}catch(OperationCanceledException){propagated=true;}
            check(propagated,"Actual operator cancellation propagates instead of being hidden as a camera helper timeout");
        }
        var large=new RobotCameraCapabilities((_,_,_,_)=>Task.FromResult(new ProcessResult(0,new string('x',300000))));
        var largeReport=await large.Read([Camera],null,CancellationToken.None);
        check(largeReport.Devices[0].Queries.All(q=>q.OutputTruncated&&q.Raw.Length==256*1024&&q.State=="partial"),
            "Large driver reports are marked truncated rather than presented as complete evidence");
        var noDevices=await inspector.Read([],null,CancellationToken.None);
        check(noDevices.Devices.Length==0&&noDevices.Problems.Single().Contains("No capture"),"No discovered cameras produce an explicit inventory problem");
        var denied=false;
        try{await inspector.Read([new("/dev/serial/by-id/motor","bad")],null,CancellationToken.None);}catch(InvalidOperationException){denied=true;}
        check(denied&&calls.Count==3,"Camera capability helper rejects non-camera inventory paths before invoking a process");
        await RuntimeAndEndpoints(check);
    }

    sealed class NoTools:IRobotTools
    {
        public int Calls;
        public Task<JsonElement> Run(RoboticsConfiguration c,string operation,object? request,int seconds,CancellationToken token)
        {Calls++;throw new InvalidOperationException("Motor/camera tool execution is outside capability inspection.");}
    }
    static async Task RuntimeAndEndpoints(Action<bool,string> check)
    {
        var root=Path.GetFullPath(".build/evidence/camera-capabilities-tests-"+Guid.NewGuid().ToString("N"));
        var tools=new NoTools();var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var queries=0;
        var inspector=new RobotCameraCapabilities(async(exe,args,seconds,token)=>
        {queries++;started.TrySetResult();await release.Task.WaitAsync(token);return await Query(exe,args,seconds,token);});
        var robot=new RoboticsRuntime(root,root+"/reservation",tools,deviceInventory:()=>new([],[],[Camera]),cameraCapabilities:inspector);
        robot.Start("robot");
        var pending=robot.CameraCapabilities(CancellationToken.None);await started.Task;
        check(Rejected(()=>robot.Configure(Configuration())),"Camera capability inspection shares operation serialization with setup changes");
        release.TrySetResult();var report=await pending;
        check(report.SelectedHeadCamera==null&&report.Devices[0].SelectedRoles.Length==0&&robot.Status().StopLatched&&tools.Calls==0&&robot.Status().Job?.State=="completed",
            "Camera capabilities work before setup or Xbox pairing and never invoke motor/capture tools");
        robot.Configure(Configuration());
        var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(o=>o.SerializerOptions.TypeInfoResolverChain.Insert(0,RobotJson.Default));
        await using var app=builder.Build();app.MapRobotics(robot);await app.StartAsync();
        var url=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client=new HttpClient{BaseAddress=new Uri(url)};
        try
        {
            var response=await client.GetAsync("/api/camera-capabilities");var json=await response.Content.ReadAsStringAsync();
            check(response.IsSuccessStatusCode&&RobotJson.Deserialize<RobotCameraCapabilityReport>(json)?.Devices[0].SelectedRoles.SequenceEqual(["head"])==true,
                "Minimal API returns source-generated camera capabilities with saved roles and raw copyable output");
            var before=queries;
            var injection=await client.GetAsync("/api/camera-capabilities?device=/dev/serial/by-id/motor&args=--set-ctrl");
            var bodyRequest=new HttpRequestMessage(HttpMethod.Get,"/api/camera-capabilities"){Content=new StringContent("{\"pan\":1}")};
            var bodyResponse=await client.SendAsync(bodyRequest);
            var write=await client.PostAsync("/api/camera-capabilities",new StringContent("{}"));
            check((int)injection.StatusCode==400&&(int)bodyResponse.StatusCode==400&&(int)write.StatusCode==405&&queries==before&&tools.Calls==0,
                "Camera capability route rejects supplied device/options/body and has no control-setting POST path");
            check(!File.Exists(root+"/calibration/receipt.json")&&!robot.Status().MotionEnabled,
                "Capability queries cannot create approval receipts or enable motion");
        }
        finally{await app.StopAsync();await robot.Shutdown();Directory.Delete(root,true);}
    }
}
