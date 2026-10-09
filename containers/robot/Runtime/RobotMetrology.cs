using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
namespace Xur.Robot;

public record RobotCameraMetrology(string Name,string Device,int Width,int Height,double Fx,double Fy,double Cx,double Cy,
    string DistortionModel,double[] Distortion,string MeasurementSource);
public record RobotTagMetrology(int Id,double ReferenceEdgeMeters,string MeasurementSource);
public record RobotMetrologySettings(int Version,RobotCameraMetrology[] Cameras,RobotTagMetrology[] Tags,
    double MaximumReprojectionRmsPixels=2,double MinimumCandidateSeparationPixels=0.5);
public record RobotPoseCandidate(double[] Rotation,double[] TranslationMeters,double ObjectSpaceError,
    double? ReprojectionRmsPixels,bool PositiveDepth,bool Valid,string[] Problems);
public record RobotMetricTag(int Id,DateTimeOffset ObservedAt,string State,RobotPoseCandidate[] Candidates,int? PreferredCandidate,string[] Problems);
public record RobotMetricCamera(string Name,int Width,int Height,string State,RobotMetricTag[] Tags,string[] Problems);
public record RobotMetricReport(string SettingsSha256,string EstimatorRevision,string FrameConvention,RobotMetricCamera[] Cameras,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] RobotMetrologySettings? MeasurementSettings=null)
{
    public bool JointCalibrationApproved=>false;
    public bool MotorCommandsIssued=>false;
    public string Scope=>"Visual tag-pose estimates from supplied metrology; measurement provenance is informational. No link transforms, joint offsets or travel limits are established.";
}
public record NativeTagPose(double[] Rotation,double[] Translation,double ObjectSpaceError);
public record NativeTagPoses(NativeTagPose[] Poses);

// Settings and validation belong to this app. The native helper receives only
// supplied metrology and image coordinates, and has no robot/device interfaces.
public sealed class RobotMetrology(string directory,
    Func<RobotCameraMetrology,double,double[][],CancellationToken,Task<NativeTagPose[]>>? estimator=null)
{
    public const string AprilTagRevision="b7c0ebe9aa20f82ec7a828579004f9e706bfecd9";
    readonly Func<RobotCameraMetrology,double,double[][],CancellationToken,Task<NativeTagPose[]>> estimate=estimator??NativeEstimate;
    string Pathname=>Path.Combine(directory,"metrology.json");
    public RobotMetrologySettings? Read()
    {
        if(!File.Exists(Pathname))return null;
        try
        {
            var settings=RobotJson.Deserialize<RobotMetrologySettings>(File.ReadAllText(Pathname))??throw new InvalidOperationException("Empty camera metrology.");
            Validate(settings);return settings;
        }
        catch(JsonException){throw new InvalidOperationException("Stored camera metrology is invalid; replace it with measured settings.");}
    }
    public RobotMetrologySettings Save(RobotMetrologySettings settings)
    {
        Validate(settings);Directory.CreateDirectory(directory);var temporary=Pathname+"."+Guid.NewGuid().ToString("N")+".tmp";
        var options=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None};
        if(OperatingSystem.IsLinux())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
        try
        {
            using(var stream=new FileStream(temporary,options))
            {stream.Write(System.Text.Encoding.UTF8.GetBytes(RobotJson.Serialize(settings)));stream.Flush(flushToDisk:true);}
            File.Move(temporary,Pathname,true);return settings;
        }
        finally{File.Delete(temporary);}
    }
    static bool Source(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=500&&!value.Any(char.IsControl);
    public static void Validate(RobotMetrologySettings settings)
    {
        static bool Finite(double value)=>double.IsFinite(value);
        if(settings.Version!=1||settings.Cameras is not {Length:<=2}||settings.Tags is not {Length:<=128}
            ||settings.Cameras.Any(c=>c==null)||settings.Tags.Any(t=>t==null)
            ||settings.Cameras.Select(c=>c.Name).Distinct().Count()!=settings.Cameras.Length
            ||settings.Tags.Select(t=>t.Id).Distinct().Count()!=settings.Tags.Length
            ||!Finite(settings.MaximumReprojectionRmsPixels)||settings.MaximumReprojectionRmsPixels is <0.1 or >5
            ||!Finite(settings.MinimumCandidateSeparationPixels)||settings.MinimumCandidateSeparationPixels is <0.05 or >2)
            throw new InvalidOperationException("Invalid camera metrology schema, duplicate identities or pose-quality thresholds.");
        foreach(var camera in settings.Cameras)
        {
            if(camera.Name is not ("head" or "hand")||camera.Device==null||!Regex.IsMatch(camera.Device,@"\A/dev/v4l/by-id/[A-Za-z0-9._:+-]+-video-index0\z")
                ||camera.Width is <16 or >3840||camera.Height is <16 or >2160
                ||!Finite(camera.Fx)||!Finite(camera.Fy)||camera.Fx is <=0 or >100000||camera.Fy is <=0 or >100000
                ||!Finite(camera.Cx)||!Finite(camera.Cy)||camera.Cx<0||camera.Cx>=camera.Width||camera.Cy<0||camera.Cy>=camera.Height
                ||!Source(camera.MeasurementSource)||camera.Distortion==null||camera.Distortion.Any(v=>!Finite(v)||Math.Abs(v)>10)
                ||!(camera.DistortionModel=="none"&&camera.Distortion.Length==0||camera.DistortionModel=="brown-conrady-5"&&camera.Distortion.Length==5))
                throw new InvalidOperationException("Provide measured camera identity, exact resolution, finite intrinsics and explicit distortion: none with 0 coefficients, or brown-conrady-5 with k1,k2,p1,p2,k3.");
        }
        foreach(var tag in settings.Tags)
            if(tag.Id is <0 or >=2115||!Finite(tag.ReferenceEdgeMeters)||tag.ReferenceEdgeMeters is <0.001 or >2||!Source(tag.MeasurementSource))
                throw new InvalidOperationException("Provide each tag's measured reference edge in meters and measurement source. Artwork width is not its reference edge.");
    }
    public async Task<RobotMarkerReport> Apply(RobotMarkerReport report,RoboticsConfiguration configuration,CancellationToken token)
    {
        RobotMetrologySettings? settings;
        try{settings=Read();}
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return report with{Metric=new("",AprilTagRevision,"tag-to-camera; optical x right, y down, z forward",
                report.Cameras.Select(c=>new RobotMetricCamera(c.Name,c.Frames[0].Width,c.Frames[0].Height,"configuration-invalid",[],[error.Message])).ToArray())};
        }
        if(settings==null||settings.Cameras.Length==0)return report;
        var cameras=new List<RobotMetricCamera>();
        foreach(var camera in report.Cameras)
        {
            token.ThrowIfCancellationRequested();var first=camera.Frames[0];
            var measured=settings.Cameras.SingleOrDefault(c=>c.Name==camera.Name);
            var device=camera.Name=="head"?configuration.HeadCamera:configuration.HandCamera;
            string? problem=measured==null?"No measured intrinsics supplied for this camera."
                :measured.Device!=device?"Measured intrinsics belong to a different camera device."
                :measured.Width!=first.Width||measured.Height!=first.Height?"Capture resolution differs from measured intrinsics; silent scaling is refused.":null;
            if(problem!=null){cameras.Add(new(camera.Name,first.Width,first.Height,"unavailable",[],[problem]));continue;}
            var tags=new List<RobotMetricTag>();
            foreach(var frame in camera.Frames)foreach(var marker in frame.Detections)
            {
                var size=settings.Tags.SingleOrDefault(t=>t.Id==marker.Id);
                if(size==null){tags.Add(new(marker.Id,frame.CapturedAt,"tag-size-missing",[],null,["No measured reference edge supplied for this tag."]));continue;}
                if(frame.AmbiguousDuplicateIds.Contains(marker.Id)){tags.Add(new(marker.Id,frame.CapturedAt,"duplicate-id",[],null,["Duplicate tag ID makes this reference ambiguous."]));continue;}
                try
                {
                    var corners=marker.Corners.Select(p=>RobotPoseMath.Undistort(measured!,p)).ToArray();
                    var raw=await estimate(measured!,size.ReferenceEdgeMeters,corners,token);
                    var candidates=raw.Select(p=>RobotPoseMath.Evaluate(measured!,size.ReferenceEdgeMeters,marker.Corners,p,settings.MaximumReprojectionRmsPixels)).ToArray();
                    var good=candidates.Select((value,index)=>(value,index)).Where(c=>c.value.Valid).OrderBy(c=>c.value.ReprojectionRmsPixels).ToArray();
                    var ambiguous=good.Length>1&&good[1].value.ReprojectionRmsPixels-good[0].value.ReprojectionRmsPixels<=settings.MinimumCandidateSeparationPixels;
                    tags.Add(new(marker.Id,frame.CapturedAt,good.Length==0?"rejected":ambiguous?"ambiguous":"estimated",candidates,
                        good.Length>0&&!ambiguous?good[0].index:null,good.Length==0?["No positive-depth candidate meets the reprojection limit."]
                            :ambiguous?["Both planar candidates fit within the supplied pixel-error separation; no pose was selected."]:[]));
                }
                catch(OperationCanceledException) when(!token.IsCancellationRequested)
                {tags.Add(new(marker.Id,frame.CapturedAt,"unavailable",[],null,["The optional pose estimator timed out; raw pixel observations remain available."]));}
                catch(Exception error) when(error is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception)
                {tags.Add(new(marker.Id,frame.CapturedAt,"unavailable",[],null,[Xur.Domain.Redaction.Logs(error.Message)]));}
            }
            cameras.Add(new(camera.Name,first.Width,first.Height,tags.Any(t=>t.State=="estimated")?"estimated":"unresolved",tags.ToArray(),[]));
        }
        var hash=Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(RobotJson.Serialize(settings))));
        return report with{Metric=new(hash,AprilTagRevision,"tag-to-camera; optical x right, y down, z forward; rotation row-major, translation meters",cameras.ToArray(),settings)};
    }
    static async Task<NativeTagPose[]> NativeEstimate(RobotCameraMetrology camera,double edge,double[][] corners,CancellationToken token)
    {
        var values=new[]{edge,camera.Fx,camera.Fy,camera.Cx,camera.Cy}.Concat(corners.SelectMany(p=>p));
        var result=await Processes.Run(Path.Combine(RoboticsContainer.ToolsDirectory,"estimate-marker-pose"),
            values.Select(value=>value.ToString("R",CultureInfo.InvariantCulture)),5,token);
        if(result.ExitCode!=0)throw new InvalidOperationException("The native AprilTag pose estimator rejected these image coordinates.");
        return RobotJson.Deserialize<NativeTagPoses>(result.Output)?.Poses??throw new InvalidOperationException("The pose estimator returned no candidate document.");
    }
}
