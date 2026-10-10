using System.Security.Cryptography;
using System.Text.Json;

namespace Xur.Robot;

public record RobotMotorRange(string State,string Detail,int? MinimumCounts=null,int? MaximumCounts=null,
    string? SourceFile=null,string? ReceiptHash=null,string Convention="Present_Position encoder counts after the drive's homing offset");
public record RobotTemperatureWindow(int WindowSeconds,int? MaximumCelsius,int SampleCount,
    DateTimeOffset? FirstSampleAt,DateTimeOffset? LastSampleAt,double ObservedSpanSeconds,
    double? LatestAgeSeconds,string Coverage="Sampled observations; gaps and unseen peaks are possible");
public record RobotMotorDiagnostic(string Port,int Id,string Name,RobotMotorRange CalibratedRange,RobotTemperatureWindow Temperature);

// Owned by RoboticsRuntime's lock. This stores only fresh dashboard observations;
// requesting diagnostics never reads hardware or extends a cached sample's life.
public sealed class RobotMotorDiagnostics
{
    static readonly string[] Joints=["shoulder_pan","shoulder_lift","elbow_flex","wrist_flex","wrist_roll","gripper"];
    readonly Dictionary<(string Port,int Id),List<(DateTimeOffset At,int Celsius)>> samples=[];
    readonly Dictionary<(string Port,int Id),DateTimeOffset> latest=[];
    public const int WindowSeconds=300;
    public void Clear(){samples.Clear();latest.Clear();}

    public void Observe(RoboticsConfiguration configuration,JsonElement observation,DateTimeOffset now)
    {
        foreach(var (port,id,_,motor) in Motors(configuration,observation))
        {
            var key=(port,id);
            if(!motor.TryGetProperty("observedAt",out var timestamp)||timestamp.ValueKind!=JsonValueKind.String
                ||!timestamp.TryGetDateTimeOffset(out var at)||at>now||at<=now.AddSeconds(-60)
                ||latest.TryGetValue(key,out var prior)&&at<=prior
                ||!motor.TryGetProperty("registers",out var registers)||registers.ValueKind!=JsonValueKind.Object
                ||!registers.TryGetProperty("Present_Temperature",out var temperature)
                ||temperature.ValueKind!=JsonValueKind.Number||!temperature.TryGetInt32(out var celsius)||celsius is <0 or >255)continue;
            latest[key]=at;
            if(!samples.TryGetValue(key,out var values))samples[key]=values=[];
            values.RemoveAll(value=>value.At<=now.AddSeconds(-WindowSeconds));
            // Bound memory even if a tool unexpectedly reports at excessive rates.
            if(values.Count>=2048)values.RemoveAt(0);
            values.Add((at,celsius));
        }
    }

    public RobotMotorDiagnostic[] Report(string directory,RoboticsConfiguration configuration,JsonElement? observation,DateTimeOffset now)
    {
        if(observation is not {} data)return [];
        var ranges=ReadRanges(directory,configuration);
        return Motors(configuration,data).Select(item=>
        {
            var (port,id,name,motor)=item;
            samples.TryGetValue((port,id),out var values);
            values?.RemoveAll(value=>value.At<=now.AddSeconds(-WindowSeconds)||value.At>now);
            var first=values is {Count:>0}?values[0].At:(DateTimeOffset?)null;
            var last=values is {Count:>0}?values[^1].At:(DateTimeOffset?)null;
            var temperature=new RobotTemperatureWindow(WindowSeconds,values is {Count:>0}?values.Max(value=>value.Celsius):null,
                values?.Count??0,first,last,first is {} start&&last is {} end?(end-start).TotalSeconds:0,
                last is {} recent?(now-recent).TotalSeconds:null);
            RobotMotorRange range;
            if(port==configuration.RightPort&&id>=7)
                range=new("not-applicable","Wheel velocity control has no calibrated angular travel range; wheels remain disabled.");
            else if(ranges.Values.TryGetValue((port,id),out var saved))
            {
                if(motor.TryGetProperty("registers",out var registers)&&registers.ValueKind==JsonValueKind.Object
                    &&registers.TryGetProperty("Homing_Offset",out var offset)&&offset.ValueKind==JsonValueKind.Number
                    &&offset.TryGetInt32(out var currentOffset)&&currentOffset==saved.Offset)
                    range=new("verified","Range from calibration files matched to this robot's receipt and the observed homing offset.",
                        saved.Minimum,saved.Maximum,saved.File,ranges.Hash);
                else range=new("not-calibrated","Stored calibration exists, but the observed homing offset is missing or differs. Its range is not verified for this reading.");
            }
            else range=new("not-calibrated",ranges.Problem);
            return new RobotMotorDiagnostic(port,id,name,range,temperature);
        }).ToArray();
    }

    static IEnumerable<(string Port,int Id,string Name,JsonElement Motor)> Motors(RoboticsConfiguration c,JsonElement data)
    {
        if(data.ValueKind!=JsonValueKind.Object||!data.TryGetProperty("buses",out var buses)||buses.ValueKind!=JsonValueKind.Array)yield break;
        var seen=new HashSet<(string,int)>();
        foreach(var bus in buses.EnumerateArray())
        {
            if(bus.ValueKind!=JsonValueKind.Object||!bus.TryGetProperty("port",out var selected)||selected.ValueKind!=JsonValueKind.String)continue;
            var port=selected.GetString()!;
            if(port!=c.LeftPort&&port!=c.RightPort||!bus.TryGetProperty("motors",out var motors)||motors.ValueKind!=JsonValueKind.Array)continue;
            foreach(var motor in motors.EnumerateArray())
            {
                if(motor.ValueKind!=JsonValueKind.Object||!motor.TryGetProperty("id",out var identity)||identity.ValueKind!=JsonValueKind.Number
                    ||!identity.TryGetInt32(out var id)||id<1||id>(port==c.LeftPort?8:9)||!seen.Add((port,id)))continue;
                var name=id<=6?(port==c.LeftPort?"left_":"right_")+Joints[id-1]:port==c.LeftPort?
                    id==7?"head_pan":"head_tilt":id switch{7=>"left_wheel",8=>"back_wheel",_=>"right_wheel"};
                yield return (port,id,name,motor);
            }
        }
    }

    record StoredRange(int Minimum,int Maximum,int Offset,string File);
    record CalibrationRanges(Dictionary<(string,int),StoredRange> Values,string? Hash,string Problem);
    static CalibrationRanges ReadRanges(string directory,RoboticsConfiguration c)
    {
        try
        {
            var path=Path.Combine(directory,"calibration");var receiptBytes=File.ReadAllBytes(Path.Combine(path,"receipt.json"));
            using var receipt=JsonDocument.Parse(receiptBytes);var root=receipt.RootElement;
            if(root.GetProperty("robotId").GetString()!=c.RobotId||root.GetProperty("leftPort").GetString()!=c.LeftPort
                ||root.GetProperty("rightPort").GetString()!=c.RightPort)throw new InvalidOperationException();
            var files=root.GetProperty("files");
            var names=new[]{c.RobotId+".json",c.RobotId+"-left.json",c.RobotId+"-right.json"};
            if(files.ValueKind!=JsonValueKind.Object||files.EnumerateObject().Count()!=3
                ||!names.ToHashSet(StringComparer.Ordinal).SetEquals(files.EnumerateObject().Select(value=>value.Name)))throw new InvalidOperationException();
            var ranges=new Dictionary<(string,int),StoredRange>();
            foreach(var name in names)
            {
                var bytes=File.ReadAllBytes(Path.Combine(path,name));
                if(Convert.ToHexStringLower(SHA256.HashData(bytes))!=files.GetProperty(name).GetString())throw new InvalidOperationException();
                using var document=JsonDocument.Parse(bytes);var values=document.RootElement;
                var whole=name==names[0];var left=name==names[1];
                var expected=whole?Joints.SelectMany(joint=>new[]{"left_arm_"+joint,"right_arm_"+joint})
                    .Concat(["head_motor_1","head_motor_2","base_left_wheel","base_back_wheel","base_right_wheel"]):Joints;
                if(values.ValueKind!=JsonValueKind.Object||!expected.ToHashSet(StringComparer.Ordinal).SetEquals(values.EnumerateObject().Select(value=>value.Name))
                    ||values.EnumerateObject().Count()!=(whole?17:6))throw new InvalidOperationException();
                foreach(var entry in values.EnumerateObject())
                {
                    var motor=entry.Value;var minimum=motor.GetProperty("range_min").GetInt32();var maximum=motor.GetProperty("range_max").GetInt32();
                    var offset=motor.GetProperty("homing_offset").GetInt32();
                    var joint=entry.Name.Replace("left_arm_","",StringComparison.Ordinal).Replace("right_arm_","",StringComparison.Ordinal);
                    var expectedId=entry.Name switch{"head_motor_1"=>7,"head_motor_2"=>8,"base_left_wheel"=>7,"base_back_wheel"=>8,"base_right_wheel"=>9,_=>Array.IndexOf(Joints,joint)+1};
                    if(minimum is <0 or >4095||maximum is <0 or >4095||minimum>=maximum||maximum-minimum<32||offset is <-4095 or >4095
                        ||motor.GetProperty("id").GetInt32()!=expectedId||motor.GetProperty("drive_mode").GetInt32()!=0)throw new InvalidOperationException();
                    if(whole)
                    {
                        var port=entry.Name.StartsWith("left_arm_",StringComparison.Ordinal)||entry.Name.StartsWith("head_motor_",StringComparison.Ordinal)?c.LeftPort:c.RightPort;
                        ranges[(port,expectedId)]=new(minimum,maximum,offset,name);
                    }
                    else
                    {
                        var saved=ranges[(left?c.LeftPort:c.RightPort,expectedId)];
                        if(saved.Minimum!=minimum||saved.Maximum!=maximum||saved.Offset!=offset)throw new InvalidOperationException();
                    }
                }
            }
            return new(ranges,Convert.ToHexStringLower(SHA256.HashData(receiptBytes)),"");
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {return new([],null,"Not calibrated: the calibration receipt or range files are missing, invalid or differ from this robot's setup. EEPROM position limits are stored drive settings, not verified calibration.");}
    }
}
