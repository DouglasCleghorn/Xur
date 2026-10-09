namespace Xur.Robot;

public record RobotMarkerDetection(int Id,int Hamming,double DecisionMargin,double[] Center,double[][] Corners);
public record RobotMarkerFrame(DateTimeOffset CapturedAt,int Width,int Height,
    RobotMarkerDetection[] Detections,int[] AmbiguousDuplicateIds);
public record RobotMarkerVisibility(int Id,int DetectedFrames,double MinimumReferenceEdgePixels,
    double MaximumReferenceEdgePixels,double CenterSpreadPixels);
public record RobotMarkerCamera(string Name,RobotMarkerFrame[] Frames,RobotMarkerVisibility[] Markers);
public record RobotMarkerReport(string Family,DateTimeOffset ObservedAt,string DetectorSha256,
    RobotMarkerCamera[] Cameras,int[] SharedIds)
{
    public bool MotorCommandsIssued=>false;
    public bool SynchronizedStereoFrames=>false;
    public bool MetricPoseAvailable=>false;
    public bool JointCalibrationApproved=>false;
    public string Scope=>"Stationary marker readability only. Pixel corners do not establish camera calibration, physical link assignments, joint offsets or safe travel limits.";
}
