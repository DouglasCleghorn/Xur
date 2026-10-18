using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Xur.Robot;

static class EmoteFileTests
{
    static string Hash(byte[] data)=>Convert.ToHexStringLower(SHA256.HashData(data));
    static byte[] Package(Action<Dictionary<string,byte[]>>? mutate=null,Func<EmoteFileManifest,EmoteFileManifest>? change=null,Func<EmoteRecordingFrame,EmoteRecordingFrame>? changeFrame=null,int fps=10)
    {
        var frames=Enumerable.Range(0,fps+1).Select(i=>new EmoteRecordingFrame(i,i/(double)fps,i/(double)fps,new double[6],new double[6],new double[6],0)).Select(f=>changeFrame?.Invoke(f)??f);
        var video=new byte[40];"ftyp"u8.CopyTo(video.AsSpan(4));
        var files=new Dictionary<string,byte[]>{["recording.jsonl"]=Encoding.UTF8.GetBytes(string.Join('\n',frames.Select(RobotJson.Serialize))+"\n"),
            ["trajectory.npz"]=[1,2,3],["preview.mp4"]=video,["XLeRobot-LICENSE"]="Apache-2.0"u8.ToArray(),["LICENSE"]="MIT"u8.ToArray(),["licensing.md"]="Notices"u8.ToArray()};
        var m=new EmoteFileManifest(1,"xur-emote-candidate","wave-right","Wave","right","wave","xlerobot-model-radians",fps,1,
            ["Rotation_R","Pitch_R","Elbow_R","Wrist_Pitch_R","Wrist_Roll_R","Jaw_R"],new("https://github.com/Vector-Wangel/XLeRobot",new string('a',40),"MuJoCo",false),false,null,files.ToDictionary(p=>p.Key,p=>Hash(p.Value)));
        files.Add("manifest.json",Encoding.UTF8.GetBytes(RobotJson.Serialize(change?.Invoke(m)??m)));mutate?.Invoke(files);
        using var output=new MemoryStream();
        using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true))
            foreach(var file in files){using var stream=zip.CreateEntry(file.Key).Open();stream.Write(file.Value);}
        return output.ToArray();
    }
    static bool Rejected(Action run)
    {try{run();return false;}catch(Exception e) when(e is InvalidOperationException or System.Text.Json.JsonException or InvalidDataException){return true;}}
    public static void Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(".build/emote-files-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var files=new EmoteFiles(root);var package=Package();var imported=files.Import(package);
            check(imported.State=="preview-only"&&!imported.Manifest.HardwareApproved&&imported.Manifest.PhysicalCalibrationHash==null&&imported.Frames==11,"Imported emote stays a preview candidate without physical approval");
            check(new EmoteFiles(root).List().Single().Key==imported.Key,"Imported emote persists across application restarts");
            check(File.ReadAllBytes(files.FilePath(imported.Key,"original.xuremot")).SequenceEqual(package),"Original emote archive is preserved byte for byte");
            check(files.Import(package).Key==imported.Key&&files.List().Length==1,"Identical imports deduplicate without overwriting originals");
            check(files.Import(Package(fps:100)).Frames==101,"Imports faster 100 Hz simulation recordings without granting physical approval");
            check(Rejected(()=>files.Import(Package(fps:100,change:m=>m with{Fps=10}))),"Recording timestamps and frame counts must match their declared cadence");
            check(Rejected(()=>files.Import(Package(fps:100,changeFrame:f=>f.Frame==5?f with{Action=[.02,0,0,0,0,0]}:f))),"Fast recordings retain the one-degree per-sample target bound");
            foreach(var change in new Func<EmoteFileManifest,EmoteFileManifest>[] {
                m=>m with{HardwareApproved=true},m=>m with{PhysicalCalibrationHash=new string('a',64)},m=>m with{CoordinateSpace="robot-degrees"},
                m=>m with{Source=m.Source with{HardwareApproved=true}},m=>m with{Arm="both"},m=>m with{Fps=1000},m=>m with{DurationSeconds=61},
                m=>m with{JointNames=["wheel1"]},m=>m with{Source=m.Source with{Repository="javascript:alert(1)"}},m=>m with{Files=[]}})
                check(Rejected(()=>files.Import(Package(change:change))),"Rejects incompatible or falsely approved candidate manifest");
            check(Rejected(()=>files.Import(Package(f=>f["preview.mp4"][20]++))),"Rejects altered payload checksums");
            check(Rejected(()=>files.Import(Package(f=>f.Add("../outside",[1])))),"Rejects archive traversal and unexpected files before extraction");
            check(Rejected(()=>files.Import(Package(f=>f.Remove("LICENSE")))),"Emote bundles must retain license notices");
            foreach(var change in new Func<EmoteRecordingFrame,EmoteRecordingFrame>[] {
                f=>f.Frame==5?f with{Contacts=1}:f,f=>f.Frame==5?f with{Timestamp=.6}:f,
                f=>f.Frame==5?f with{Action=[.02,0,0,0,0,0]}:f,f=>f.Frame==5?f with{ObservationVelocity=[4,0,0,0,0,0]}:f,
                f=>f.Frame==5?f with{ObservationState=[1,0,0,0,0,0]}:f,f=>f.Frame==5?f with{Action=[0]}:f,
                f=>f.Frame==10?f with{Action=[.01,0,0,0,0,0]}:f})
                check(Rejected(()=>files.Import(Package(changeFrame:change))),"Rejects rehashed recordings with unsafe or inconsistent frames");
            check(Rejected(()=>files.Import(new byte[EmoteFiles.MaxArchiveBytes+1])),"Oversized emote archives are rejected before decompression");
            check(Rejected(()=>files.FilePath("../outside","preview.mp4"))&&Rejected(()=>files.FilePath(imported.Key,"receipt.json")),"Candidate downloads cannot expose arbitrary files");
            var video=files.FilePath(imported.Key,"preview.mp4");File.AppendAllText(video,"changed");
            check(Rejected(()=>files.FilePath(imported.Key,"preview.mp4")),"Modified stored video cannot pass checksum verification");
            check(!File.Exists(Path.Combine(root,"config.json"))&&!Directory.Exists(Path.Combine(root,"calibration"))&&!Directory.Exists(Path.Combine(root,"datasets")),"Import never creates robot configuration, calibration or physical replay datasets");
        }
        finally{Directory.Delete(root,true);}
    }
}
