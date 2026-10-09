using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
namespace Xur.Robot;
public record RobotHealth(string State,string Compilation);
public record RobotError(string Error);
public record RobotObservation(JsonElement? Observation,bool Paused,string? Operation,string? Problem);
public record RobotSkillRequest(RobotSkill Skill);
public record RobotCameraRequest(string Camera);
public record RobotToolRequest(RoboticsConfiguration Configuration,string Operation,JsonElement? Request);
[JsonSerializable(typeof(RobotHealth))]
[JsonSerializable(typeof(RobotError))]
[JsonSerializable(typeof(RobotObservation))]
[JsonSerializable(typeof(RobotToolRequest))]
[JsonSerializable(typeof(RobotSkillRequest))]
[JsonSerializable(typeof(RobotCameraRequest))]
[JsonSerializable(typeof(RobotStatus))]
[JsonSerializable(typeof(RoboticsConfiguration))]
[JsonSerializable(typeof(RobotCalibrationAssessment))]
[JsonSerializable(typeof(RobotDevices))]
[JsonSerializable(typeof(RobotBusDetection))]
[JsonSerializable(typeof(RobotJob))]
[JsonSerializable(typeof(RobotJob[]))]
[JsonSerializable(typeof(RobotMarkerReport))]
[JsonSerializable(typeof(RobotMetrologySettings))]
[JsonSerializable(typeof(RobotMetricReport))]
[JsonSerializable(typeof(NativeTagPoses))]
[JsonSerializable(typeof(RobotMarkerSurvey.AdapterResult))]
[JsonSerializable(typeof(RobotResetStopRequest))]
[JsonSerializable(typeof(RobotCalibrationRequest))]
[JsonSerializable(typeof(RobotArmRequest))]
[JsonSerializable(typeof(RobotControllerRequest))]
[JsonSerializable(typeof(RobotEmoteRequest))]
[JsonSerializable(typeof(RobotTaskRequest))]
[JsonSerializable(typeof(RobotRecordRequest))]
[JsonSerializable(typeof(RobotTrainRequest))]
[JsonSerializable(typeof(RobotSkillReview))]
[JsonSerializable(typeof(Dictionary<int,int>[]))]
[JsonSerializable(typeof(Dictionary<string,string>))]
[JsonSerializable(typeof(string[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy=JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow)]
public partial class RobotJson:JsonSerializerContext
{
    public static JsonSerializerOptions SerializerOptions=>Default.Options;
    public static JsonTypeInfo<T> Type<T>()=>(JsonTypeInfo<T>)Default.GetTypeInfo(typeof(T))!;
    public static string Serialize<T>(T value)=>JsonSerializer.Serialize(value,Type<T>());
    public static T? Deserialize<T>(string value)=>JsonSerializer.Deserialize(value,Type<T>());
    public static JsonElement Element(object value)=>JsonSerializer.SerializeToElement(value,Default.GetTypeInfo(value.GetType())
        ??throw new InvalidOperationException("Unreviewed robot adapter request type."));
}
