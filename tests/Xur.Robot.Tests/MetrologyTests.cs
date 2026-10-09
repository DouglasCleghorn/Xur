using System.Text.Json;
using Xur.Robot;
static class MetrologyTests
{
    static readonly double[] Identity=[1,0,0,0,1,0,0,0,1];
    static RobotCameraMetrology Camera()=>new("head","/dev/v4l/by-id/test-head-video-index0",640,480,800,810,320,240,"none",[],"Synthetic offline fixture; not robot calibration");
    static RobotMetrologySettings Settings(RobotCameraMetrology camera)=>new(1,[camera],[new(0,0.05,"Synthetic measured-reference fixture")]);
    static RoboticsConfiguration Configuration()=>new("fixture","/dev/serial/by-id/left","/dev/serial/by-id/right","",
        "/dev/v4l/by-id/test-head-video-index0","/dev/v4l/by-id/test-hand-video-index0",[]);
    static RobotMarkerReport Report(RobotCameraMetrology camera,double[][] corners,int id=0)
    {
        var time=DateTimeOffset.UtcNow;
        RobotMarkerCamera View(string name)=>new(name,Enumerable.Range(0,3).Select(i=>new RobotMarkerFrame(time.AddMilliseconds(i),camera.Width,camera.Height,
            [new(id,0,100,[corners.Average(p=>p[0]),corners.Average(p=>p[1])],corners)],[])).ToArray(),[]);
        return new("tagStandard41h12",time,new string('a',64),[View("head"),View("hand")],[]);
    }
    static bool Reject(Action action){try{action();return false;}catch(InvalidOperationException){return true;}}
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(".build/evidence/metrology-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var camera=Camera();var settings=Settings(camera);var config=Configuration();var calls=0;
            Task<NativeTagPose[]> Estimate(RobotCameraMetrology _,double __,double[][] ___,CancellationToken token)
            {calls++;return Task.FromResult(new[]{new NativeTagPose(Identity,[0,0,0.5],0)});}
            var store=new RobotMetrology(root,Estimate);
            var corners=RobotPoseMath.ObjectCorners(0.05).Select(p=>RobotPoseMath.Project(camera,RobotPoseMath.Transform(Identity,[0,0,0.5],p))).ToArray();
            var report=Report(camera,corners);
            check((await store.Apply(report,config,CancellationToken.None)).Metric==null&&calls==0,
                "Without supplied metrology, marker surveys remain pixel-only and never invoke pose estimation");
            store.Save(settings);check(new RobotMetrology(root).Read()?.Cameras.Single().Fx==800,
                "Measured camera and tag settings persist in the container without motor configuration changes");
            check(Reject(()=>store.Save(settings with{Cameras=[camera with{Fx=double.NaN}]}))
                &&Reject(()=>store.Save(settings with{Cameras=[camera with{DistortionModel="fisheye",Distortion=[0,0,0,0]}]}))
                &&Reject(()=>store.Save(settings with{Cameras=[camera with{DistortionModel="brown-conrady-5",Distortion=[0,0,0,0]}]})),
                "Nonfinite intrinsics and unsupported distortion models or coefficient lengths are rejected");
            check(Reject(()=>store.Save(settings with{Tags=[new(0,0,"Missing measurement")]}))
                &&Reject(()=>store.Save(settings with{Tags=[settings.Tags[0],settings.Tags[0]]}))
                &&Reject(()=>store.Save(settings with{Cameras=[camera with{MeasurementSource=""}]})),
                "Missing physical reference size, duplicate tag identities and absent provenance are rejected");
            try{RobotJson.Deserialize<RobotMetrologySettings>("{\"version\":1,\"cameras\":[],\"tags\":[],\"motorGoal\":90}");check(false,"Unknown metrology fields rejected");}
            catch(JsonException){check(true,"Source-generated metrology requests reject attempted motor fields");}
            var estimated=await store.Apply(report,config,CancellationToken.None);
            check(estimated.MetricPoseAvailable&&estimated.Metric!.Cameras[0].Tags.All(t=>t.State=="estimated"&&t.Candidates[0].ReprojectionRmsPixels<1e-9)
                &&!estimated.JointCalibrationApproved&&!estimated.MotorCommandsIssued&&!estimated.Metric.JointCalibrationApproved,
                "Valid visual pose candidates preserve raw observations and never approve joint calibration or motor motion");
            check(RobotJson.Serialize(estimated.Metric!.MeasurementSettings)==RobotJson.Serialize(settings)&&estimated.Metric.SettingsSha256.Length==64,
                "Metric evidence retains the supplied measurement snapshot and hash for later reproduction");
            var before=calls;store.Save(settings with{Cameras=[camera with{Width=1280}]});
            check((await store.Apply(report,config,CancellationToken.None)).Metric!.Cameras[0].State=="unavailable"&&calls==before,
                "Capture resolution must exactly match measured intrinsics; no silent scaling or estimation occurs");
            store.Save(settings);before=calls;
            check((await store.Apply(report,config with{HeadCamera=config.HandCamera},CancellationToken.None)).Metric!.Cameras[0].State=="unavailable"&&calls==before,
                "Changing the selected camera invalidates metrology associated with a different device");
            var byPath="/dev/v4l/by-path/pci-0000:67:00.4-usb-0:1.2:1.0-video-index0";
            check((await store.Apply(report,config with{HeadCamera=byPath},CancellationToken.None)).Metric!.Cameras[0].State=="unavailable"&&calls==before,
                "Switching alias types cannot borrow old by-id measurements for a selected by-path interface");
            store.Save(Settings(camera with{Device=byPath}));
            check((await store.Apply(report,config with{HeadCamera=byPath},CancellationToken.None)).Metric!.Cameras[0].State=="estimated",
                "Supplied by-path metrology is accepted only for the exact selected interface");
            store.Save(settings);before=calls;
            check((await store.Apply(Report(camera,corners,1),config,CancellationToken.None)).Metric!.Cameras[0].Tags.All(t=>t.State=="tag-size-missing")&&calls==before,
                "An unmeasured tag cannot inherit another tag's size");
            var ambiguousStore=new RobotMetrology(root,(_,_,_,_)=>Task.FromResult(new[]{new NativeTagPose(Identity,[0,0,0.5],0),new NativeTagPose(Identity,[0.0001,0,0.5],0)}));
            var ambiguous=await ambiguousStore.Apply(report,config,CancellationToken.None);
            check(ambiguous.Metric!.Cameras[0].Tags.All(t=>t.State=="ambiguous"&&t.PreferredCandidate==null&&t.Candidates.Length==2),
                "Two planar fits within the pixel-error separation remain explicit ambiguous candidates with no selected pose");
            var behind=RobotPoseMath.Evaluate(camera,0.05,corners,new(Identity,[0,0,-0.5],0),2);
            check(!behind.Valid&&!behind.PositiveDepth&&behind.ReprojectionRmsPixels==null,"Behind-camera pose candidates are rejected before projection");
            var badRotation=RobotPoseMath.Evaluate(camera,0.05,corners,new([-1,0,0,0,1,0,0,0,1],[0,0,0.5],0),2);
            check(!badRotation.Valid,"Improper rotation matrices cannot become visual pose candidates");
            var wrong=RobotPoseMath.Evaluate(camera,0.05,corners,new(Identity,[0.05,0,0.5],0),2);
            check(!wrong.Valid&&wrong.ReprojectionRmsPixels>2,"Raw-pixel reprojection rejects an inconsistent metric pose");
            var distorted=camera with{DistortionModel="brown-conrady-5",Distortion=[-0.15,0.03,0.001,-0.002,0.005]};
            var raw=RobotPoseMath.Project(distorted,[0.12,-0.04,0.5]);var recovered=RobotPoseMath.Undistort(distorted,raw);
            check(Math.Abs(recovered[0]-(800*0.24+320))<1e-6&&Math.Abs(recovered[1]-(810*-0.08+240))<1e-6,
                "Supplied Brown-Conrady distortion inverts to the known pinhole point with a bounded residual");
            var singular=camera with{DistortionModel="brown-conrady-5",Distortion=[-1,0,0,0,0]};
            check(Reject(()=>RobotPoseMath.Undistort(singular,[camera.Cx+camera.Fx,camera.Cy])),
                "Noninvertible distortion fails convergence instead of returning guessed metric coordinates");
            var folded=camera with{DistortionModel="brown-conrady-5",Distortion=[1,-1,0,0,0]};
            check(Reject(()=>RobotPoseMath.Undistort(folded,[camera.Cx+camera.Fx,camera.Cy])),
                "A zero-residual folded distortion root is rejected before returning metric coordinates");
            var timedOut=new RobotMetrology(root,(_,_,_,_)=>throw new OperationCanceledException("Internal helper deadline"));
            var incomplete=await timedOut.Apply(report,config,CancellationToken.None);
            check(incomplete.Cameras==report.Cameras&&incomplete.Metric!.Cameras[0].Tags.All(t=>t.State=="unavailable"),
                "Optional helper timeout preserves otherwise-valid raw pixel observations");
            using(var canceled=new CancellationTokenSource())
            {
                canceled.Cancel();
                try{await timedOut.Apply(report,config,canceled.Token);check(false,"Operator cancellation propagates");}
                catch(OperationCanceledException){check(true,"Real operator cancellation propagates instead of being hidden as a helper timeout");}
            }
            File.WriteAllText(Path.Combine(root,"metrology.json"),"{\"fx\":800}");
            check((await store.Apply(report,config,CancellationToken.None)).Metric!.Cameras.All(c=>c.State=="configuration-invalid"),
                "Corrupt persisted metrology keeps pixel surveys available and reports an explicit pose configuration error");
            store.Save(settings);
            var runtime=new RoboticsRuntime(root,Path.Combine(root,"reservation"),metrology:store);runtime.Start("robot");
            runtime.ConfigureMetrology(settings);
            check(runtime.Status().StopLatched&&!File.Exists(Path.Combine(root,"calibration/receipt.json")),
                "Saving camera metrology neither creates a calibration receipt nor arms the robot");
        }
        finally{Directory.Delete(root,true);}
    }
}
