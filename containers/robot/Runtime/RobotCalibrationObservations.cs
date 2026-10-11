using System.Security.Cryptography;
using System.Text.Json;

namespace Xur.Robot;

public record RobotCalibrationObservationReceipt(string ConfigurationSha256,RobotCalibrationObservationCapture Capture);

// The adapter owns acquisition. This app validates its sealed evidence and
// serves only named artifacts; neither side approves calibration here.
public sealed class RobotCalibrationObservations(string directory)
{
    readonly object sync=new();
    readonly Dictionary<string,long> rejected=new(StringComparer.Ordinal);
    const string Originals=".build/evidence/calibration-observations";
    const string Backups="backups/calibration-observations";
    const long MaximumBytes=256L*1024*1024;
    string LatestPath=>Path.Combine(directory,Originals,"latest.json");
    static bool Hex(string? value,int length)=>value?.Length==length&&value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    static bool Name(string value)=>value.Length is >=1 and <=128&&!value.Contains("..",StringComparison.Ordinal)
        &&(value[0] is >= 'a' and <= 'z' or >= '0' and <= '9')
        &&value.All(c=>c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
    static string ConfigurationHash(RoboticsConfiguration c)=>Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(RobotJson.Serialize(c))));
    static string Hash(byte[] value)=>Convert.ToHexStringLower(SHA256.HashData(value));
    static T Schema<T>(Func<T> read)
    {
        try{return read();}
        catch(Exception e) when(e is KeyNotFoundException or FormatException or OverflowException or JsonException or ArgumentOutOfRangeException)
        {throw new InvalidOperationException("Malformed preserved calibration observation metadata.",e);}
    }
    string PlainPath(string relative)
    {
        var current=Path.GetFullPath(directory);
        if(new DirectoryInfo(current).LinkTarget!=null)throw new InvalidOperationException("Observation storage cannot be a symbolic link.");
        foreach(var part in relative.Split('/'))
        {
            if(part is "" or "." or "..")throw new InvalidOperationException("Invalid observation artifact path.");
            current=Path.Combine(current,part);
            if(new FileInfo(current).LinkTarget!=null)throw new InvalidOperationException("Observation artifacts cannot be symbolic links.");
        }
        return current;
    }
    byte[] Read(string relative,long maximum)
    {
        var path=PlainPath(relative);var file=new FileInfo(path);
        if(!file.Exists||file.Length>maximum)throw new InvalidOperationException("The preserved observation artifact is missing or too large.");
        return File.ReadAllBytes(path);
    }
    static void Validate(RobotCalibrationObservationCapture report)
    {
        if(report.Version!=1||!Hex(report.Id,32)||report.SampleCount is <0 or >3||report.Complete&&report.SampleCount==0
            ||report.ManifestRelativePath!=$"{Originals}/{report.Id}/manifest.json"||!Hex(report.ManifestSha256,64)
            ||report.BackupRelativePath!=null&&report.BackupRelativePath!=$"{Backups}/{report.Id}"
            ||report.BackupVerified!=(report.BackupRelativePath!=null)||report.MotorCommandsIssued||report.JointCalibrationApproved||report.SynchronizedStereoFrames
            ||report.Problems==null||report.Problems.Length>128||report.Problems.Any(p=>p==null||p.Length>1024))
            throw new InvalidOperationException("The calibration observation adapter returned an invalid evidence receipt.");
    }
    public RobotCalibrationObservationCapture Save(RoboticsConfiguration c,JsonElement result,int requestedSamples)
    {
        lock(sync)return Schema(()=>
        {
            var capture=RobotJson.Deserialize<RobotCalibrationObservationCapture>(result.GetRawText())??throw new InvalidOperationException("No calibration observation was returned.");
            Validate(capture);
            if(requestedSamples is <1 or >3||capture.SampleCount>requestedSamples||capture.Complete&&capture.SampleCount!=requestedSamples)
                throw new InvalidOperationException("The observation sample count differs from the bounded request.");
            ValidateEvidence(c,capture,requestedSamples);
            Index(c,capture);return capture;
        });
    }
    void Index(RoboticsConfiguration c,RobotCalibrationObservationCapture capture)
    {
        var receipt=new RobotCalibrationObservationReceipt(ConfigurationHash(c),capture);
        WriteReceipt(PlainPath($"{Originals}/{capture.Id}.receipt.json"),receipt);
        RobotCalibrationObservationCapture? latest=null;
        try{latest=Latest(c);}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException){ }
        if(latest==null||capture.CapturedAt>=latest.CapturedAt)WriteReceipt(PlainPath($"{Originals}/latest.json"),receipt);
    }
    static (DateTimeOffset Utc,long Mono) Stamp(JsonElement value)
    {
        var utc=value.GetProperty("utc").GetDateTimeOffset();var mono=value.GetProperty("monotonicNanoseconds").GetInt64();
        if(utc.Offset!=TimeSpan.Zero||mono<0)throw new InvalidOperationException("Invalid observation read timestamp.");
        return(utc,mono);
    }
    static string[] SampleFiles(int index)
    {
        var stem=$"sample-{index:00}";
        return [stem+".json",stem+"-motors-before-cameras.json",stem+"-motors-after-cameras.json",
            stem+"-head.npy",stem+"-head.png",stem+"-hand.npy",stem+"-hand.png"];
    }
    static void ReadOnlyFlags(JsonElement value)
    {
        if(value.GetProperty("motorCommandsIssued").GetBoolean()||value.GetProperty("jointCalibrationApproved").GetBoolean()
            ||value.GetProperty("synchronizedStereoFrames").GetBoolean())throw new InvalidOperationException("Observation metadata does not describe read-only evidence.");
    }
    static void Sealed(string path)
    {
        if(OperatingSystem.IsLinux()&&(File.GetUnixFileMode(path)&(UnixFileMode.UserWrite|UnixFileMode.GroupWrite|UnixFileMode.OtherWrite))!=0)
            throw new InvalidOperationException("Calibration observation originals must be sealed before indexing.");
    }
    void ValidateEvidence(RoboticsConfiguration c,RobotCalibrationObservationCapture capture,int? requestedSamples=null)
    {
        Schema(()=>
        {
            Validate(capture);var bytes=Manifest(capture);
            using var parsed=JsonDocument.Parse(bytes);var manifest=parsed.RootElement;var files=Files(manifest,capture);
            var requested=manifest.GetProperty("requestedSamples").GetInt32();
            if(requested is <1 or >3||requestedSamples!=null&&requested!=requestedSamples||capture.SampleCount>requested
                ||capture.Complete&&capture.SampleCount!=requested)throw new InvalidOperationException("Observation request scope differs from its manifest.");
            var started=Stamp(manifest.GetProperty("startedAt"));var ended=Stamp(manifest.GetProperty("completedAt"));
            if(started.Utc!=capture.CapturedAt||ended.Mono<started.Mono)throw new InvalidOperationException("Observation session timing is inconsistent.");
            var problems=manifest.GetProperty("problems").EnumerateArray().Select(p=>p.GetString()).ToArray();
            if(problems.Length>128||problems.Any(p=>p==null||p.Length>1024)||capture.Complete&&problems.Length!=0
                ||problems.Any(p=>!capture.Problems.Contains(p!,StringComparer.Ordinal)))throw new InvalidOperationException("Observation completion differs from its reported problems.");
            var expected=new HashSet<string>(StringComparer.Ordinal){"session.json"};
            for(var index=0;index<requested;index++)expected.UnionWith(SampleFiles(index));
            var names=files.Select(f=>f.Name).ToHashSet(StringComparer.Ordinal);
            if(!names.Contains("session.json")||names.Any(n=>!expected.Contains(n))||capture.Complete&&!names.SetEquals(expected))
                throw new InvalidOperationException("Observation inventory does not contain the artifacts required by its completion state.");
            Sealed(PlainPath($"{Originals}/{capture.Id}"));Sealed(PlainPath(capture.ManifestRelativePath));
            using var session=JsonDocument.Parse(Read($"{Originals}/{capture.Id}/session.json",1024*1024));var data=session.RootElement;
            var devices=data.GetProperty("selectedDevices");
            if(data.GetProperty("version").GetInt32()!=1||data.GetProperty("id").GetString()!=capture.Id
                ||data.GetProperty("robotId").GetString()!=c.RobotId||data.GetProperty("requestedSamples").GetInt32()!=requested
                ||Stamp(data.GetProperty("startedAt"))!=started||data.GetProperty("exposureTimestampsAvailable").GetBoolean()
                ||data.GetProperty("registerValuesNormalized").GetBoolean()
                ||devices.GetProperty("leftPort").GetString()!=c.LeftPort||devices.GetProperty("rightPort").GetString()!=c.RightPort
                ||devices.GetProperty("headCamera").GetString()!=c.HeadCamera||devices.GetProperty("handCamera").GetString()!=c.HandCamera
                ||!data.GetProperty("cameras").EnumerateArray().Select(v=>v.GetString()).SequenceEqual(["head","hand"]))
                throw new InvalidOperationException("Observation provenance differs from the configured robot and cameras.");
            ReadOnlyFlags(data);
            foreach(var file in files){var relative=$"{Originals}/{capture.Id}/{file.Name}";Sealed(PlainPath(relative));VerifyFile(relative,file);}
            for(var index=0;index<capture.SampleCount;index++)
            {
                var filename=$"sample-{index:00}.json";
                if(!names.Contains(filename))throw new InvalidOperationException("Observation sample metadata is missing.");
                if(capture.Complete)ValidateCompleteSample(c,capture,index,started.Mono,ended.Mono);
            }
            if(capture.BackupVerified)
            {
                Sealed(PlainPath($"{Backups}/{capture.Id}"));Sealed(PlainPath($"{Backups}/{capture.Id}/manifest.json"));
                if(!Read($"{Backups}/{capture.Id}/manifest.json",1024*1024).AsSpan().SequenceEqual(bytes))
                    throw new InvalidOperationException("The local observation backup manifest differs from the originals.");
                foreach(var file in files){var relative=$"{Backups}/{capture.Id}/{file.Name}";Sealed(PlainPath(relative));VerifyFile(relative,file);}
            }
            return true;
        });
    }
    // Acquisition may finish sealing after its caller was cancelled. Recovery
    // indexes only independently verified, fixed-root evidence; it never repairs
    // or changes the original session bytes or a backup.
    public void Recover(RoboticsConfiguration c)
    {
        lock(sync)
        {
            try
            {
                var root=PlainPath(Originals);if(!Directory.Exists(root))return;
                var configurationHash=ConfigurationHash(c);var pending=0;var timer=System.Diagnostics.Stopwatch.StartNew();long verifiedBytes=0;
                RobotCalibrationObservationCapture? latest=null;
                try{latest=Latest(c);}catch(Exception e) when(Recoverable(e)){ }
                foreach(var folder in new DirectoryInfo(root).EnumerateDirectories().OrderByDescending(d=>d.LastWriteTimeUtc))
                {
                    if(timer.Elapsed>TimeSpan.FromSeconds(15))break;
                    if(!Hex(folder.Name,32)||folder.LinkTarget!=null)continue;
                    var id=folder.Name;var key=configurationHash+"/"+id;
                    var stamp=folder.LastWriteTimeUtc.Ticks;
                    var manifestInfo=new FileInfo(Path.Combine(folder.FullName,"manifest.json"));
                    if(manifestInfo.Exists)stamp=Math.Max(stamp,manifestInfo.LastWriteTimeUtc.Ticks);
                    if(rejected.TryGetValue(key,out var seen)&&seen==stamp)continue;
                    try
                    {
                        if(File.Exists(Path.Combine(root,id+".receipt.json")))
                        {
                            var receipt=StoredReceipt($"{Originals}/{id}.receipt.json");
                            if(receipt.Capture.Id!=id)throw new InvalidOperationException("Stored observation identity differs from its directory.");
                            if(receipt.ConfigurationSha256==configurationHash)
                            {
                                // Existing receipts already paid for full hash
                                // verification. Verify again only to restore a
                                // missing or older latest pointer.
                                if(latest==null||receipt.Capture.CapturedAt>latest.CapturedAt)
                                {if(!Reserve(receipt.Capture))break;ValidateEvidence(c,receipt.Capture);Index(c,receipt.Capture);latest=receipt.Capture;}
                                continue;
                            }
                        }
                        if(pending++>=128)break;
                        var manifestBytes=Read($"{Originals}/{id}/manifest.json",1024*1024);
                        using var parsed=JsonDocument.Parse(manifestBytes);var body=parsed.RootElement;
                        var capture=Schema(()=>new RobotCalibrationObservationCapture(body.GetProperty("version").GetInt32(),
                            body.GetProperty("id").GetString()??"",Stamp(body.GetProperty("startedAt")).Utc,
                            body.GetProperty("sampleCount").GetInt32(),body.GetProperty("complete").GetBoolean(),
                            $"{Originals}/{id}/manifest.json",Hash(manifestBytes),null,false,
                            body.GetProperty("motorCommandsIssued").GetBoolean(),body.GetProperty("jointCalibrationApproved").GetBoolean(),
                            body.GetProperty("synchronizedStereoFrames").GetBoolean(),body.GetProperty("problems").EnumerateArray().Select(p=>p.GetString()??throw new InvalidOperationException("Invalid observation problem.")).ToArray()));
                        if(capture.Id!=id)throw new InvalidOperationException("Observation manifest identity differs from its directory.");
                        if(!Reserve(capture))break;
                        ValidateEvidence(c,capture);
                        var backupProblem="No independent local copy was verified while recovering this observation.";
                        try
                        {
                            var backup=$"{Backups}/{id}";
                            if(Directory.Exists(PlainPath(backup)))
                            {
                                var copied=capture with{BackupRelativePath=backup,BackupVerified=true};
                                ValidateEvidence(c,copied);capture=copied;backupProblem="";
                            }
                        }
                        catch(Exception e) when(Recoverable(e))
                        {backupProblem="The preserved local observation copy failed recovery verification.";}
                        if(backupProblem!=""&&capture.Problems.Length<128)capture=capture with{Problems=capture.Problems.Append(backupProblem).ToArray()};
                        Index(c,capture);
                        if(latest==null||capture.CapturedAt>=latest.CapturedAt)latest=capture;
                        rejected.Remove(key);
                    }
                    catch(Exception e) when(Recoverable(e))
                    {
                        // A still-running or corrupt session must not block
                        // startup, cancellation cleanup or other captures.
                        if(rejected.Count>=1024)rejected.Clear();rejected[key]=stamp;
                    }
                }
                bool Reserve(RobotCalibrationObservationCapture capture)
                {
                    using var parsed=JsonDocument.Parse(Manifest(capture));var declared=Files(parsed.RootElement,capture).Sum(f=>f.Bytes);
                    if(verifiedBytes>0&&verifiedBytes+declared>2*MaximumBytes)return false;
                    verifiedBytes+=declared;return true;
                }
            }
            catch(Exception e) when(Recoverable(e)){ }
        }
    }
    static bool Recoverable(Exception e)=>e is IOException or UnauthorizedAccessException or InvalidOperationException
        or JsonException or KeyNotFoundException or FormatException or OverflowException or ArgumentException;
    void ValidateCompleteSample(RoboticsConfiguration c,RobotCalibrationObservationCapture capture,int index,long earliest,long latest)
    {
        var stem=$"sample-{index:00}";using var parsed=JsonDocument.Parse(Read($"{Originals}/{capture.Id}/{stem}.json",1024*1024));var sample=parsed.RootElement;
        if(sample.GetProperty("index").GetInt32()!=index||!sample.GetProperty("stationaryAcrossReadBrackets").GetBoolean())
            throw new InvalidOperationException("A complete observation requires stationary sample metadata.");
        var motors=sample.GetProperty("motorSamples").EnumerateArray().ToArray();var cameras=sample.GetProperty("cameras").EnumerateArray().ToArray();
        if(motors.Length!=2||cameras.Length!=2)throw new InvalidOperationException("A complete sample requires both cameras and motor read brackets.");
        var before=MotorBracket(motors[0],"before-cameras");var after=MotorBracket(motors[1],"after-cameras");
        var previous=before.End.Mono;
        for(var cameraIndex=0;cameraIndex<2;cameraIndex++)
        {
            var camera=cameras[cameraIndex];var name=cameraIndex==0?"head":"hand";
            if(camera.GetProperty("name").GetString()!=name||camera.TryGetProperty("error",out _)
                ||!camera.GetProperty("requestedFullResolution").GetBoolean()
                ||camera.GetProperty("width").GetInt32() is <16 or >3840||camera.GetProperty("height").GetInt32() is <16 or >2160
                ||camera.GetProperty("channels").GetInt32()!=3||camera.GetProperty("dtype").GetString()!="uint8"||camera.GetProperty("colorOrder").GetString()!="BGR"
                ||camera.GetProperty("rawArrayFile").GetString()!=stem+"-"+name+".npy"||camera.GetProperty("losslessImageFile").GetString()!=stem+"-"+name+".png")
                throw new InvalidOperationException("Complete camera metadata is missing or inconsistent.");
            var began=Stamp(camera.GetProperty("readStartedAt"));var ended=Stamp(camera.GetProperty("readCompletedAt"));
            if(began.Mono<previous||ended.Mono<began.Mono)throw new InvalidOperationException("Camera read intervals are inconsistent.");
            previous=ended.Mono;
        }
        if(before.Start.Mono<earliest||after.Start.Mono<previous||after.End.Mono>latest||!before.Positions.Keys.ToHashSet().SetEquals(after.Positions.Keys))
            throw new InvalidOperationException("Motor read brackets are inconsistent.");
        var delta=before.Positions.Max(pair=>Math.Abs((long)pair.Value-after.Positions[pair.Key]));
        if(delta>2||sample.GetProperty("maxObservedEncoderDeltaRaw").GetInt64()!=delta)
            throw new InvalidOperationException("Motor stationarity differs from the measured encoder bracket.");
        ((DateTimeOffset Utc,long Mono) Start,(DateTimeOffset Utc,long Mono) End,Dictionary<(string Port,int Id,string Name),int> Positions) MotorBracket(JsonElement reference,string phase)
        {
            var filename=stem+"-motors-"+phase+".json";
            if(reference.GetProperty("phase").GetString()!=phase||reference.GetProperty("file").GetString()!=filename)
                throw new InvalidOperationException("Motor sample refers to an unexpected artifact.");
            var start=Stamp(reference.GetProperty("readStartedAt"));var end=Stamp(reference.GetProperty("readCompletedAt"));
            using var report=JsonDocument.Parse(Read($"{Originals}/{capture.Id}/{filename}",1024*1024));var body=report.RootElement;
            if(end.Mono<start.Mono||body.GetProperty("phase").GetString()!=phase||Stamp(body.GetProperty("readStartedAt"))!=start||Stamp(body.GetProperty("readCompletedAt"))!=end)
                throw new InvalidOperationException("Motor sample timing differs from its referenced artifact.");
            var positions=new Dictionary<(string Port,int Id,string Name),int>();var ports=new HashSet<string>(StringComparer.Ordinal);
            foreach(var bus in body.GetProperty("buses").EnumerateArray())
            {
                var port=bus.GetProperty("port").GetString()??"";
                if(bus.TryGetProperty("error",out _)||(port!=c.LeftPort&&port!=c.RightPort)||!ports.Add(port))
                    throw new InvalidOperationException("Motor sample bus identity is missing, duplicate or different.");
                var channels=bus.GetProperty("motors").EnumerateArray().ToArray();var ids=new HashSet<int>();
                if(channels.Length==0)throw new InvalidOperationException("Motor sample has no joint channels.");
                foreach(var motor in channels)
                {
                    var id=motor.GetProperty("id").GetInt32();var name=motor.GetProperty("name").GetString()??"";var registers=motor.GetProperty("registers");
                    if(motor.TryGetProperty("error",out _)||id is <1 or >253||!ids.Add(id)||string.IsNullOrWhiteSpace(name)
                        ||registers.GetProperty("Torque_Enable").GetInt32()!=0||registers.GetProperty("Moving").GetInt32()!=0)
                        throw new InvalidOperationException("Motor sample is not verified stationary with torque off.");
                    positions.Add((port,id,name),registers.GetProperty("Present_Position").GetInt32());
                }
            }
            if(ports.Count!=2)throw new InvalidOperationException("Motor sample is missing a selected bus.");
            return(start,end,positions);
        }
    }
    static void WriteReceipt(string path,RobotCalibrationObservationReceipt receipt)
    {
        var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            var options=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None};
            if(OperatingSystem.IsLinux())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
            using(var file=new FileStream(temporary,options))
            {file.Write(System.Text.Encoding.UTF8.GetBytes(RobotJson.Serialize(receipt)));file.Flush(true);}
            File.Move(temporary,path,true);
        }
        finally{File.Delete(temporary);}
    }
    public RobotCalibrationObservationCapture? Latest(RoboticsConfiguration c)
    {
        lock(sync)return Schema(()=>
        {
            if(!File.Exists(LatestPath))return null;
            var receipt=StoredReceipt($"{Originals}/latest.json");
            if(receipt.ConfigurationSha256!=ConfigurationHash(c))return null;
            using var manifest=JsonDocument.Parse(Manifest(receipt.Capture));var files=Files(manifest.RootElement,receipt.Capture);
            // Polling checks existence, size, sealing and manifest identity.
            // Capture/recovery and every download perform full byte hashes.
            Sealed(PlainPath($"{Originals}/{receipt.Capture.Id}"));
            foreach(var file in files)
            {
                var path=PlainPath($"{Originals}/{receipt.Capture.Id}/{file.Name}");var info=new FileInfo(path);
                if(!info.Exists||info.Length!=file.Bytes)throw new InvalidOperationException("A preserved observation artifact is missing or differs in size.");
                Sealed(path);
            }
            return receipt.Capture;
        });
    }
    RobotCalibrationObservationReceipt StoredReceipt(string relative)
    {
        var receipt=RobotJson.Deserialize<RobotCalibrationObservationReceipt>(System.Text.Encoding.UTF8.GetString(Read(relative,1024*1024)))
            ??throw new InvalidOperationException("Invalid stored observation receipt.");
        if(!Hex(receipt.ConfigurationSha256,64)||receipt.Capture==null)throw new InvalidOperationException("Invalid stored observation provenance.");
        Validate(receipt.Capture);return receipt;
    }
    RobotCalibrationObservationCapture Capture(string id)
    {
        if(!Hex(id,32))throw new InvalidOperationException("Select a preserved calibration observation.");
        var receipt=StoredReceipt($"{Originals}/{id}.receipt.json");
        if(receipt.Capture.Id!=id)throw new InvalidOperationException("Observation receipt identity differs from the selection.");
        return receipt.Capture;
    }
    byte[] Manifest(RobotCalibrationObservationCapture capture)
    {
        var bytes=Read(capture.ManifestRelativePath,1024*1024);
        if(Hash(bytes)!=capture.ManifestSha256)throw new InvalidOperationException("The preserved observation manifest failed its SHA-256 check.");
        return bytes;
    }
    public byte[] Manifest(string id)=>Schema(()=>{var capture=Capture(id);var bytes=Manifest(capture);using var parsed=JsonDocument.Parse(bytes);Files(parsed.RootElement,capture);return bytes;});
    record Artifact(string Name,long Bytes,string Sha256);
    static Artifact[] Files(JsonElement manifest,RobotCalibrationObservationCapture capture)
    {
        if(manifest.GetProperty("version").GetInt32()!=1||manifest.GetProperty("id").GetString()!=capture.Id
            ||manifest.GetProperty("sampleCount").GetInt32()!=capture.SampleCount||manifest.GetProperty("complete").GetBoolean()!=capture.Complete
            ||manifest.GetProperty("motorCommandsIssued").GetBoolean()||manifest.GetProperty("jointCalibrationApproved").GetBoolean()
            ||manifest.GetProperty("synchronizedStereoFrames").GetBoolean())throw new InvalidOperationException("Observation manifest scope differs from its receipt.");
        var files=manifest.GetProperty("files").EnumerateArray().Select(f=>new Artifact(f.GetProperty("name").GetString()??"",f.GetProperty("bytes").GetInt64(),f.GetProperty("sha256").GetString()??"")).ToArray();
        if(files.Length is <1 or >32||files.Any(f=>!Name(f.Name)||f.Name=="manifest.json"||f.Bytes is <0 or >MaximumBytes||!Hex(f.Sha256,64))
            ||files.Select(f=>f.Name).Distinct(StringComparer.Ordinal).Count()!=files.Length||files.Sum(f=>f.Bytes)>MaximumBytes)
            throw new InvalidOperationException("Invalid observation artifact inventory.");
        return files;
    }
    void VerifyFile(string relative,Artifact artifact)
    {
        var path=PlainPath(relative);var file=new FileInfo(path);
        if(!file.Exists||file.Length!=artifact.Bytes)throw new InvalidOperationException("Observation artifact size differs from its manifest.");
        using var input=File.OpenRead(path);
        if(Convert.ToHexStringLower(SHA256.HashData(input))!=artifact.Sha256)throw new InvalidOperationException("Observation artifact failed its SHA-256 check.");
    }
    public byte[] ReadArtifact(string id,string name)
    {
        return Schema(()=>
        {
        if(!Name(name))throw new InvalidOperationException("Select an artifact named in the observation manifest.");
        var capture=Capture(id);
        using var manifest=JsonDocument.Parse(Manifest(capture));
        var artifact=Files(manifest.RootElement,capture).SingleOrDefault(f=>f.Name==name)
            ??throw new InvalidOperationException("This file is not in the observation manifest.");
        var bytes=Read($"{Originals}/{id}/{name}",MaximumBytes);
        if(bytes.LongLength!=artifact.Bytes||Hash(bytes)!=artifact.Sha256)throw new InvalidOperationException("Observation artifact failed its SHA-256 check.");
        return bytes;
        });
    }
}
