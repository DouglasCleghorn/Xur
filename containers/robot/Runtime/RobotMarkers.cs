using System.Text.Json.Serialization;
namespace Xur.Robot;

public record RobotMarkerDetection(int Id,int Hamming,double DecisionMargin,double[] Center,double[][] Corners);
public record RobotMarkerFrame(DateTimeOffset CapturedAt,int Width,int Height,
    RobotMarkerDetection[] Detections,int[] AmbiguousDuplicateIds);
public record RobotMarkerVisibility(int Id,int DetectedFrames,double MinimumReferenceEdgePixels,
    double MaximumReferenceEdgePixels,double CenterSpreadPixels);
public record RobotMarkerCamera(string Name,RobotMarkerFrame[] Frames,RobotMarkerVisibility[] Markers);
public record RobotMarkerReport(string Family,DateTimeOffset ObservedAt,string DetectorSha256,
    RobotMarkerCamera[] Cameras,int[] SharedIds,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] RobotMetricReport? Metric=null)
{
    public bool MotorCommandsIssued=>false;
    public bool SynchronizedStereoFrames=>false;
    public bool MetricPoseAvailable=>Metric?.Cameras.Any(c=>c.Tags.Any(t=>t.Candidates.Any(p=>p.Valid)))==true;
    public bool JointCalibrationApproved=>false;
    public string Scope=>Metric==null?"Stationary marker readability only. Pixel corners do not establish camera calibration, physical link assignments, joint offsets or safe travel limits."
        :"Stationary marker readability and visual pose candidates from supplied measured metrology. No physical link assignments, joint offsets or safe travel limits are established.";
}
