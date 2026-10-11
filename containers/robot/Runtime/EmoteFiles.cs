using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Xur.Robot;

public record EmoteFileSource(string Repository,string Revision,string Simulator,bool HardwareApproved,string? GeneratorSha256=null);
public record EmoteFilePreview(string File,int RenderFps,int EncodedFps,double DurationSeconds,string Source);
public record EmoteFileManifest(int SchemaVersion,string Kind,string Id,string Name,string Arm,string Task,string CoordinateSpace,
    int Fps,double DurationSeconds,string[] JointNames,EmoteFileSource Source,bool HardwareApproved,string? PhysicalCalibrationHash,Dictionary<string,string> Files,
    EmoteFilePreview? Preview=null,string? Transfer=null);
public record EmoteRecordingFrame(int Frame,double Timestamp,double SimulationTime,double[] Action,
    [property:JsonPropertyName("observation.state")] double[] ObservationState,
    [property:JsonPropertyName("observation.velocity")] double[] ObservationVelocity,int Contacts);
public record EmoteFileReceipt(string PackageSha256,string ManifestSha256,int Frames,DateTimeOffset Imported);
public record EmoteFileItem(string Key,EmoteFileManifest Manifest,int Frames,DateTimeOffset Imported,string State,string Detail);

// Imported files are preview candidates. They never enter robot configuration,
// calibration, reviewed skills or the motor adapter's physical datasets.
public sealed class EmoteFiles(string stateDirectory)
{
    public const int MaxArchiveBytes=16*1024*1024;
    readonly string directory=Path.Combine(stateDirectory,"emotes","imports");
    readonly object sync=new();
    static readonly string[] Required=["recording.jsonl","trajectory.npz","preview.mp4","XLeRobot-LICENSE","LICENSE","licensing.md"];
    static readonly string[] Allowed=["manifest.json",..Required,"metadata.json"];
    static readonly string[] Joints=["Rotation","Pitch","Elbow","Wrist_Pitch","Wrist_Roll","Jaw"];
    static bool Hash(string? value,int length=64)=>value?.Length==length&&value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    static string Digest(byte[] data)=>Convert.ToHexStringLower(SHA256.HashData(data));
    static bool SameHash(string path,string expected)
    {using var stream=File.OpenRead(path);return Convert.ToHexStringLower(SHA256.HashData(stream))==expected;}
    static void Validate(EmoteFileManifest m)
    {
        if(m.SchemaVersion!=1||m.Kind!="xur-emote-candidate"||!RobotIdentifiers.Valid(m.Id)||string.IsNullOrWhiteSpace(m.Name)||m.Name.Length>80
            ||string.IsNullOrWhiteSpace(m.Task)||m.Task.Length>500||m.Arm is not ("left" or "right")||m.CoordinateSpace!="xlerobot-model-radians"
            ||m.Fps!=10||!double.IsFinite(m.DurationSeconds)||m.DurationSeconds is < 1 or > 60||Math.Abs(m.DurationSeconds*10-Math.Round(m.DurationSeconds*10))>1e-7
            ||m.HardwareApproved||m.PhysicalCalibrationHash!=null||m.Source==null||m.Source.HardwareApproved||m.Source.Simulator!="MuJoCo"
            ||!Hash(m.Source.Revision,40)||(m.Source.GeneratorSha256!=null&&!Hash(m.Source.GeneratorSha256))
            ||!Uri.TryCreate(m.Source.Repository,UriKind.Absolute,out var uri)||uri.Scheme!="https"
            ||(m.Preview!=null&&(m.Preview.File!="preview.mp4"||m.Preview.RenderFps is < 1 or > 60||m.Preview.EncodedFps is < 1 or > 60
                ||!double.IsFinite(m.Preview.DurationSeconds)||Math.Abs(m.Preview.DurationSeconds-m.DurationSeconds)>1e-6||string.IsNullOrWhiteSpace(m.Preview.Source)||m.Preview.Source.Length>500))
            ||m.Transfer?.Length>1000
            ||m.JointNames==null||!m.JointNames.SequenceEqual(Joints.Select(n=>n+(m.Arm=="left"?"_L":"_R")))
            ||m.Files==null||Required.Any(name=>!m.Files.ContainsKey(name))||m.Files.Any(p=>!Allowed.Contains(p.Key)||p.Key=="manifest.json"||!Hash(p.Value)))
            throw new InvalidOperationException("Use a version 1 XLeRobot simulation candidate with a video, checksums and license notices. Physical approval cannot be imported.");
    }
    static int ValidateFrames(byte[] data,EmoteFileManifest manifest)
    {
        using var reader=new StringReader(new UTF8Encoding(false,true).GetString(data));
        var count=0;double[]? first=null,previous=null,firstObserved=null,lastObserved=null;
        string? line;
        while((line=reader.ReadLine())!=null)
        {
            if(line.Length is 0 or > 4096||count>600)throw new InvalidOperationException("Recording frame count or line length exceeds its bounds.");
            var frame=RobotJson.Deserialize<EmoteRecordingFrame>(line)??throw new InvalidOperationException("Recording frame is missing.");
            var arrays=new[]{frame.Action,frame.ObservationState,frame.ObservationVelocity};
            if(frame.Frame!=count||!double.IsFinite(frame.Timestamp)||!double.IsFinite(frame.SimulationTime)
                ||Math.Abs(frame.Timestamp-count/10d)>1e-6||Math.Abs(frame.SimulationTime-frame.Timestamp)>1e-5||frame.Contacts!=0
                ||arrays.Any(a=>a==null||a.Length!=6||a.Any(v=>!double.IsFinite(v)))
                ||frame.Action.Any(v=>Math.Abs(v)>4)||frame.ObservationState.Any(v=>Math.Abs(v)>4)||frame.ObservationVelocity.Any(v=>Math.Abs(v)>3)
                ||frame.Action.Where((v,i)=>Math.Abs(v-frame.ObservationState[i])>.2).Any()
                ||(previous!=null&&frame.Action.Where((v,i)=>Math.Abs(v-previous[i])>Math.PI/180+1e-7).Any()))
                throw new InvalidOperationException("Recording timestamps, joint values, contacts or command steps failed validation.");
            first??=frame.Action;firstObserved??=frame.ObservationState;previous=frame.Action;lastObserved=frame.ObservationState;count++;
        }
        if(count!=Math.Round(manifest.DurationSeconds*10)+1||first==null||previous==null||firstObserved==null||lastObserved==null
            ||first.Where((v,i)=>Math.Abs(v-previous[i])>1e-6).Any()||firstObserved.Where((v,i)=>Math.Abs(v-lastObserved[i])>.01).Any())
            throw new InvalidOperationException("Recording duration or return to its starting pose differs from the manifest.");
        return count;
    }
    public EmoteFileItem Import(byte[] archive)
    {
        if(archive.Length is 0 or > MaxArchiveBytes)throw new InvalidOperationException("Choose an emote file no larger than 16 MiB.");
        using var input=new MemoryStream(archive,false);using var zip=new ZipArchive(input,ZipArchiveMode.Read);
        if(zip.Entries.Count is < 7 or > 8||zip.Entries.Select(e=>e.FullName).Distinct(StringComparer.Ordinal).Count()!=zip.Entries.Count
            ||zip.Entries.Any(e=>!Allowed.Contains(e.FullName)||e.Length<1||e.Length>8*1024*1024)||zip.Entries.Sum(e=>e.Length)>24*1024*1024)
            throw new InvalidOperationException("Emote files must contain only the documented flat candidate, video and license files.");
        var files=new Dictionary<string,byte[]>();
        foreach(var entry in zip.Entries)
        {
            var limit=entry.FullName=="preview.mp4"?8*1024*1024:entry.FullName is "recording.jsonl" or "trajectory.npz"?2*1024*1024:128*1024;
            if(entry.Length>limit)throw new InvalidOperationException("An emote payload exceeds its size limit.");
            using var source=entry.Open();using var output=new MemoryStream();var buffer=new byte[8192];int read;
            while((read=source.Read(buffer))!=0)
            {if(output.Length+read>limit)throw new InvalidOperationException("Emote decompression exceeds its limit.");output.Write(buffer,0,read);}
            if(output.Length!=entry.Length)throw new InvalidOperationException("Emote archive entry length differs.");
            files.Add(entry.FullName,output.ToArray());
        }
        if(!files.TryGetValue("manifest.json",out var manifestBytes))throw new InvalidOperationException("Emote manifest is missing.");
        var manifest=RobotJson.Deserialize<EmoteFileManifest>(Encoding.UTF8.GetString(manifestBytes))??throw new InvalidOperationException("Emote manifest is missing.");
        Validate(manifest);
        if(files.Count!=manifest.Files.Count+1||manifest.Files.Any(p=>!files.TryGetValue(p.Key,out var bytes)||Digest(bytes)!=p.Value))
            throw new InvalidOperationException("Emote payload checksums differ from the manifest.");
        var video=files["preview.mp4"];
        if(video.Length<32||!video.AsSpan(4,4).SequenceEqual("ftyp"u8))throw new InvalidOperationException("Provide an MP4 preview video.");
        var frames=ValidateFrames(files["recording.jsonl"],manifest);
        var key=Digest(archive);
        lock(sync)
        {
            Directory.CreateDirectory(directory);
            if(Directory.Exists(Path.Combine(directory,key)))return Read(key);
            if(Directory.EnumerateDirectories(directory).Count(p=>Hash(Path.GetFileName(p)))>=64)throw new InvalidOperationException("The local candidate library is full.");
            var temporary=Path.Combine(directory,".build",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temporary);
            try
            {
                foreach(var file in files)File.WriteAllBytes(Path.Combine(temporary,file.Key),file.Value);
                File.WriteAllBytes(Path.Combine(temporary,"original.xuremot"),archive);
                var receipt=new EmoteFileReceipt(key,Digest(manifestBytes),frames,DateTimeOffset.UtcNow);
                File.WriteAllText(Path.Combine(temporary,"receipt.json"),RobotJson.Serialize(receipt));
                Directory.Move(temporary,Path.Combine(directory,key));
            }
            finally{if(Directory.Exists(temporary))Directory.Delete(temporary,true);}
            return Read(key);
        }
    }
    public EmoteFileItem Read(string key)
    {
        if(!Hash(key))throw new InvalidOperationException("Choose an imported candidate.");
        var root=Path.Combine(directory,key);
        var receipt=RobotJson.Deserialize<EmoteFileReceipt>(File.ReadAllText(Path.Combine(root,"receipt.json")))??throw new InvalidOperationException("Candidate receipt is missing.");
        var manifestBytes=File.ReadAllBytes(Path.Combine(root,"manifest.json"));
        if(receipt.PackageSha256!=key||!Hash(receipt.ManifestSha256)||Digest(manifestBytes)!=receipt.ManifestSha256)throw new InvalidOperationException("Candidate manifest failed verification.");
        var manifest=RobotJson.Deserialize<EmoteFileManifest>(Encoding.UTF8.GetString(manifestBytes))??throw new InvalidOperationException("Candidate manifest is missing.");Validate(manifest);
        return new(key,manifest,receipt.Frames,receipt.Imported,"preview-only","Review the video. Robot-specific joint mapping, calibration and supervised motion review are still required.");
    }
    public EmoteFileItem[] List()
    {
        lock(sync)
        {
            if(!Directory.Exists(directory))return [];
            var items=new List<EmoteFileItem>();
            foreach(var path in Directory.EnumerateDirectories(directory).Where(p=>Hash(Path.GetFileName(p))).Take(64))
                try{items.Add(Read(Path.GetFileName(path)));}catch(Exception e) when(e is IOException or InvalidOperationException or System.Text.Json.JsonException){}
            return items.OrderByDescending(item=>item.Imported).ToArray();
        }
    }
    public string FilePath(string key,string name)
    {
        var item=Read(key);var path=Path.Combine(directory,key,name);
        var hash=name=="original.xuremot"?key:name=="preview.mp4"?item.Manifest.Files[name]:throw new InvalidOperationException("Choose a preview video or original emote file.");
        if(!SameHash(path,hash))throw new InvalidOperationException("Candidate file failed checksum verification.");return path;
    }
}
