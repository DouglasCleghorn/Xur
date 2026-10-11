using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xur.Robot;

static class CalibrationObservationTests
{
    const string Originals=".build/evidence/calibration-observations";
    static readonly JsonSerializerOptions Web=new(JsonSerializerDefaults.Web);
    internal static readonly byte[] Png=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAEElEQVR4nGNgGAWjYBTAAAADEAABPywr7AAAAABJRU5ErkJggg==");
    static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    static object Stamp(int value)=>new{utc=DateTimeOffset.Parse("2026-10-10T20:00:00Z").AddTicks(value),monotonicNanoseconds=1_000_000L+100L*value};
    internal static RoboticsConfiguration Configuration()=>new("fixture","/dev/serial/by-id/left","/dev/serial/by-id/right","",
        "/dev/v4l/by-id/head-video-index0","/dev/v4l/by-id/hand-video-index0",[]);
    static byte[] RawArray()
    {
        var text="{'descr': '|u1', 'fortran_order': False, 'shape': (16, 16, 3), }";
        var padding=64-(10+text.Length+1)%64;var header=Encoding.ASCII.GetBytes(text+new string(' ',padding)+"\n");
        using var output=new MemoryStream();using var writer=new BinaryWriter(output);
        writer.Write(new byte[]{0x93,0x4e,0x55,0x4d,0x50,0x59,1,0});writer.Write((ushort)header.Length);writer.Write(header);writer.Write(new byte[16*16*3]);
        return output.ToArray();
    }
    internal static JsonElement Capture(string directory,RoboticsConfiguration c,int samples=1,bool complete=true)
    {
        var id=Guid.NewGuid().ToString("N");var original=Path.Combine(directory,Originals,id);
        var backup=Path.Combine(directory,"backups/calibration-observations",id);Directory.CreateDirectory(original);Directory.CreateDirectory(backup);
        var files=new Dictionary<string,byte[]>();
        // Upstream register names are case-sensitive and must stay unchanged.
        void Add(string name,object value)=>files.Add(name,JsonSerializer.SerializeToUtf8Bytes(value));
        Add("session.json",new{version=1,id,startedAt=Stamp(0),requestedSamples=samples,robotId=c.RobotId,
            selectedDevices=new{leftPort=c.LeftPort,rightPort=c.RightPort,headCamera=c.HeadCamera,handCamera=c.HandCamera},
            cameras=new[]{"head","hand"},motorCommandsIssued=false,jointCalibrationApproved=false,synchronizedStereoFrames=false,
            exposureTimestampsAvailable=false,registerValuesNormalized=false,timestampMeaning="Synthetic host read brackets; no simultaneous exposure.",
            imageFormat="lossless PNG plus unmodified NumPy uint8 BGR arrays"});
        var count=complete?samples:0;
        for(var index=0;index<count;index++)
        {
            var stem=$"sample-{index:00}";var start=index*20;
            var before=stem+"-motors-before-cameras.json";var after=stem+"-motors-after-cameras.json";
            object Motors(string phase,int first,int last)=>new{phase,readStartedAt=Stamp(first),readCompletedAt=Stamp(last),
                buses=new[]{(Role:"left/head",Port:c.LeftPort,Count:8),(Role:"right/wheels",Port:c.RightPort,Count:9)}.Select(bus=>new
                {role=bus.Role,port=bus.Port,motors=Enumerable.Range(1,bus.Count).Select(number=>new{id=number,name=bus.Role.Replace('/', '-')+"-joint-"+number,
                    model="STS3215",observedAt=DateTimeOffset.Parse("2026-10-10T20:00:00Z").AddTicks(last),registers=new{Present_Position=1024+number,Torque_Enable=0,Moving=0,Status=0}}).ToArray()}).ToArray()};
            Add(before,Motors("before-cameras",start+1,start+2));Add(after,Motors("after-cameras",start+7,start+8));
            object Camera(string name,int first,int last)
            {
                files.Add(stem+"-"+name+".png",Png);files.Add(stem+"-"+name+".npy",RawArray());
                return new{name,requestedFullResolution=true,readStartedAt=Stamp(first),readCompletedAt=Stamp(last),
                    width=16,height=16,channels=3,dtype="uint8",colorOrder="BGR",rawArrayFile=stem+"-"+name+".npy",losslessImageFile=stem+"-"+name+".png"};
            }
            Add(stem+".json",new{index,cameras=new[]{Camera("head",start+3,start+4),Camera("hand",start+5,start+6)},
                motorSamples=new[]{new{phase="before-cameras",file=before,readStartedAt=Stamp(start+1),readCompletedAt=Stamp(start+2)},
                    new{phase="after-cameras",file=after,readStartedAt=Stamp(start+7),readCompletedAt=Stamp(start+8)}},
                maxObservedEncoderDeltaRaw=0,stationaryAcrossReadBrackets=true});
        }
        var problems=complete?Array.Empty<string>():["Synthetic interrupted capture; no complete samples."];
        var manifest=JsonSerializer.SerializeToUtf8Bytes(new{version=1,id,startedAt=Stamp(0),completedAt=Stamp(samples*20+10),sampleCount=count,
            requestedSamples=samples,complete,motorCommandsIssued=false,jointCalibrationApproved=false,synchronizedStereoFrames=false,problems,
            files=files.OrderBy(f=>f.Key,StringComparer.Ordinal).Select(f=>new{name=f.Key,bytes=f.Value.Length,sha256=Hash(f.Value)}).ToArray()},Web);
        files.Add("manifest.json",manifest);
        foreach(var file in files)
            foreach(var folder in new[]{original,backup})
            {var path=Path.Combine(folder,file.Key);File.WriteAllBytes(path,file.Value);if(OperatingSystem.IsLinux())File.SetUnixFileMode(path,UnixFileMode.UserRead);}
        if(OperatingSystem.IsLinux())foreach(var folder in new[]{original,backup})File.SetUnixFileMode(folder,UnixFileMode.UserRead|UnixFileMode.UserExecute);
        return RobotJson.Element(new RobotCalibrationObservationCapture(1,id,DateTimeOffset.Parse("2026-10-10T20:00:00Z"),count,complete,
            $"{Originals}/{id}/manifest.json",Hash(manifest),$"backups/calibration-observations/{id}",true,false,false,false,problems));
    }
    internal static void Unseal(string directory)
    {
        if(!Directory.Exists(directory)||!OperatingSystem.IsLinux())return;
        File.SetUnixFileMode(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        foreach(var folder in Directory.EnumerateDirectories(directory,"*",SearchOption.AllDirectories))
            if(new DirectoryInfo(folder).LinkTarget==null)File.SetUnixFileMode(folder,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
    }
    static bool Reject(Action action)
    {try{action();return false;}catch(Exception e) when(e is InvalidOperationException or JsonException or IOException){return true;}}
    static RobotCalibrationObservationCapture Report(JsonElement result)=>RobotJson.Deserialize<RobotCalibrationObservationCapture>(result.GetRawText())!;
    static void Rewrite(string path,byte[] bytes)
    {if(OperatingSystem.IsLinux())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);File.WriteAllBytes(path,bytes);if(OperatingSystem.IsLinux())File.SetUnixFileMode(path,UnixFileMode.UserRead);}
    static JsonElement ChangeManifest(string directory,JsonElement result,Action<JsonObject> change)
    {
        var report=Report(result);var path=Path.Combine(directory,report.ManifestRelativePath);var manifest=JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        change(manifest);var bytes=Encoding.UTF8.GetBytes(manifest.ToJsonString());Rewrite(path,bytes);
        Rewrite(Path.Combine(directory,report.BackupRelativePath!,"manifest.json"),bytes);
        return RobotJson.Element(report with{ManifestSha256=Hash(bytes)});
    }
    static JsonElement ChangeArtifact(string directory,JsonElement result,string name,Action<JsonObject> change)
    {
        var report=Report(result);var path=Path.Combine(directory,Originals,report.Id,name);var data=JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        change(data);var bytes=Encoding.UTF8.GetBytes(data.ToJsonString());Rewrite(path,bytes);Rewrite(Path.Combine(directory,report.BackupRelativePath!,name),bytes);
        return ChangeManifest(directory,result,manifest=>
        {var file=manifest["files"]!.AsArray().Single(f=>f!["name"]!.GetValue<string>()==name)!;file["bytes"]=bytes.Length;file["sha256"]=Hash(bytes);});
    }
    sealed class Tools(string directory):IRobotTools
    {
        public readonly List<string> Calls=[];
        public Task<JsonElement> Run(RoboticsConfiguration c,string operation,object? request,int seconds,CancellationToken token)
        {
            Calls.Add(operation);
            if(operation=="capture-calibration-observation")return Task.FromResult(Capture(directory,c,((RobotCalibrationObservationRequest)request!).Samples));
            if(operation=="camera")return Task.FromResult(JsonSerializer.SerializeToElement(new{jpeg="/9j/2Q=="}));
            throw new Exception("Unexpected observation tool operation: "+operation);
        }
    }
    static async Task Finished(RoboticsRuntime runtime,string id)
    {for(var i=0;i<300&&runtime.Job(id)?.State=="running";i++)await Task.Delay(5);if(runtime.Job(id)?.State=="running")throw new Exception("Observation job did not finish.");}
    public static async Task Run(Action<bool,string> check)
    {
        var directory=Path.GetFullPath(".build/evidence/calibration-observations-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var c=Configuration();var store=new RobotCalibrationObservations(directory);var result=Capture(directory,c,3);var saved=store.Save(c,result,3);
            check(saved is{Complete:true,SampleCount:3,BackupVerified:true,MotorCommandsIssued:false,JointCalibrationApproved:false,SynchronizedStereoFrames:false}
                &&store.Manifest(saved.Id).SequenceEqual(File.ReadAllBytes(Path.Combine(directory,saved.ManifestRelativePath)))
                &&store.ReadArtifact(saved.Id,"sample-02-head.png").SequenceEqual(Png),
                "Verified stationary evidence retains exact camera/motor artifacts and independent backup without approving calibration");
            check(new RobotCalibrationObservations(directory).Latest(c)?.Id==saved.Id
                &&new RobotCalibrationObservations(directory).Latest(c with{HeadCamera=c.HandCamera})==null,
                "Restart retains latest stationary evidence while changed device selection cannot borrow its provenance");
            check(Reject(()=>store.ReadArtifact(saved.Id,"../../config.json"))&&Reject(()=>store.ReadArtifact(saved.Id,"unlisted.json"))
                &&Reject(()=>store.Manifest("../../config")),"Observation downloads reject traversal, unknown artifacts and invalid identities");
            var original=Path.Combine(directory,Originals,saved.Id,"sample-02-head.png");Rewrite(original,[1,2,3]);
            check(Reject(()=>store.ReadArtifact(saved.Id,"sample-02-head.png"))&&Reject(()=>store.Save(c,result,3)),
                "Corrupt preserved camera bytes cannot be downloaded or accepted as verified evidence");
            result=Capture(directory,c);saved=store.Save(c,result,1);Rewrite(Path.Combine(directory,saved.ManifestRelativePath),[1,2,3]);
            check(Reject(()=>store.Manifest(saved.Id)),"Changed preserved manifest bytes fail their original receipt hash");
            result=Capture(directory,c);var report=Report(result);
            Rewrite(Path.Combine(directory,report.BackupRelativePath!,"sample-00-head.png"),[1,2,3]);
            check(Reject(()=>store.Save(c,result,1)),"A corrupt independent copy cannot be marked as a verified local backup");
            var unsafeReceipts=new[]{report with{MotorCommandsIssued=true},report with{JointCalibrationApproved=true},report with{SynchronizedStereoFrames=true},
                report with{ManifestRelativePath="../../config.json"},report with{SampleCount=4}};
            for(var index=0;index<unsafeReceipts.Length;index++)
                check(Reject(()=>store.Save(c,RobotJson.Element(unsafeReceipts[index]),1)),"Evidence receipts reject unsafe scope, identity or sample count: case "+index);
            result=Capture(directory,c);result=ChangeManifest(directory,result,m=>m["files"]!.AsArray().RemoveAt(0));
            check(Reject(()=>store.Save(c,result,1)),"Complete evidence requires its exact complete camera/motor artifact inventory");
            result=Capture(directory,c);result=ChangeArtifact(directory,result,"session.json",s=>s["robotId"]="different-robot");
            check(Reject(()=>store.Save(c,result,1)),"Even hash-consistent evidence cannot claim a different robot identity");
            result=Capture(directory,c);result=ChangeArtifact(directory,result,"session.json",s=>s["selectedDevices"]!["headCamera"]=c.HandCamera);
            check(Reject(()=>store.Save(c,result,1)),"Even hash-consistent evidence must bind each selected camera and motor bus");
            result=Capture(directory,c);result=ChangeArtifact(directory,result,"sample-00.json",s=>s["stationaryAcrossReadBrackets"]=false);
            check(Reject(()=>store.Save(c,result,1)),"A complete capture cannot falsely claim stationary camera/encoder correspondence");
            foreach(var register in new[]{"Torque_Enable","Moving"})
            {
                result=Capture(directory,c);result=ChangeArtifact(directory,result,"sample-00-motors-before-cameras.json",s=>s["buses"]![0]!["motors"]![0]!["registers"]![register]=1);
                check(Reject(()=>store.Save(c,result,1)),"Measured motor activity invalidates stationary evidence despite forged completion flags: "+register);
            }
            result=Capture(directory,c);result=ChangeArtifact(directory,result,"sample-00-motors-after-cameras.json",s=>s["buses"]![0]!["motors"]![0]!["registers"]!["Present_Position"]=2048);
            check(Reject(()=>store.Save(c,result,1)),"Position drift is recomputed from raw measurements instead of trusting a claimed zero delta");
            result=Capture(directory,c);result=ChangeArtifact(directory,result,"sample-00.json",s=>s["cameras"]![0]!["readStartedAt"]!["monotonicNanoseconds"]=0);
            check(Reject(()=>store.Save(c,result,1)),"Camera timestamps cannot imply capture before their independently sampled motor bracket");
            result=Capture(directory,c);result=ChangeManifest(directory,result,m=>m["files"]![0]!["name"]="../../config.json");
            check(Reject(()=>store.Save(c,result,1)),"Manifest artifact paths cannot traverse outside preserved observations");
            result=Capture(directory,c);report=Report(result);var linked=Path.Combine(directory,Originals,report.Id,"sample-00-head.png");
            Unseal(Path.GetDirectoryName(linked)!);File.Delete(linked);File.CreateSymbolicLink(linked,Path.Combine(directory,report.BackupRelativePath!,"sample-00-head.png"));
            check(Reject(()=>store.Save(c,result,1)),"An original artifact symlink cannot borrow identical bytes from another location");
            var partial=store.Save(c,Capture(directory,c,complete:false),1);
            check(partial is{Complete:false,SampleCount:0,BackupVerified:true,JointCalibrationApproved:false},
                "Interrupted zero-sample capture retains sealed provenance and verified local backup without fabricating calibration");
            foreach(var json in new[]{"{\"samples\":1,\"motors\":{\"goal\":180}}","{\"samples\":1,\"device\":\"/dev/video0\"}"})
                check(Reject(()=>RobotJson.Deserialize<RobotCalibrationObservationRequest>(json)),"Source-generated observation requests reject hardware targets or device overrides");
            var recoveryPath=Path.Combine(directory,"sealed-recovery");var interruptedResult=Capture(recoveryPath,c);var interruptedReport=Report(interruptedResult);
            var recovery=new RobotCalibrationObservations(recoveryPath);recovery.Recover(c);
            check(recovery.Latest(c)?.Id==interruptedReport.Id&&recovery.Manifest(interruptedReport.Id).Length>0,
                "A sealed capture survives cancellation before .NET receives its response and is indexed on restart");
            var partialPath=Path.Combine(directory,"partial-recovery");var partialReport=Report(Capture(partialPath,c,complete:false));
            var partialRecovery=new RobotCalibrationObservations(partialPath);partialRecovery.Recover(c);
            check(partialRecovery.Latest(c) is{Complete:false,SampleCount:0,BackupVerified:true}&&partialRecovery.Manifest(partialReport.Id).Length>0,
                "Restart can recover sealed interrupted provenance without complete camera samples or calibration approval");
            var writablePath=Path.Combine(directory,"writable-recovery");var writable=Report(Capture(writablePath,c));Unseal(Path.Combine(writablePath,Originals,writable.Id));
            var writableRecovery=new RobotCalibrationObservations(writablePath);writableRecovery.Recover(c);
            check(writableRecovery.Latest(c)==null,"A session still writable at process interruption cannot be recovered as sealed evidence");
            await RuntimeAndEndpoints(Path.Combine(directory,"runtime"),check);
        }
        finally{Unseal(directory);Directory.Delete(directory,true);}
    }
    static async Task RuntimeAndEndpoints(string directory,Action<bool,string> check)
    {
        var c=Configuration();var tools=new Tools(directory);var runtime=new RoboticsRuntime(directory,Path.Combine(directory,"reservation"),tools);
        runtime.Start("fixture");runtime.Configure(c);var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app=builder.Build();app.MapRobotics(runtime);await app.StartAsync();
        using var http=new HttpClient{BaseAddress=new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
        try
        {
            using var injected=await http.PostAsJsonAsync("/api/calibration-observations",new{samples=1,motors=new{goal=180}});
            check(injected.StatusCode==HttpStatusCode.BadRequest&&tools.Calls.Count==0,"Observation API rejects motor commands before invoking tools");
            foreach(var samples in new[]{0,4})
            {using var bad=await http.PostAsJsonAsync("/api/calibration-observations",new{samples});check(bad.StatusCode==HttpStatusCode.Conflict&&tools.Calls.Count==0,"Observation API bounds sample count before acquisition: "+samples);}
            using var accepted=await http.PostAsJsonAsync("/api/calibration-observations",new{samples=1});var job=await accepted.Content.ReadFromJsonAsync<RobotJob>();
            await Finished(runtime,job!.Id);var capture=runtime.CalibrationObservation()!;
            check(accepted.StatusCode==HttpStatusCode.Accepted&&runtime.Job(job.Id)?.State=="completed"&&capture.Complete
                &&tools.Calls.SequenceEqual(["capture-calibration-observation"])&&runtime.Status().StopLatched&&!runtime.Status().MotionEnabled
                &&!Directory.Exists(Path.Combine(directory,"calibration")),"Stationary capture succeeds without a controller, calibration or motion enablement");
            var png=await http.GetByteArrayAsync($"/api/calibration-observations/{capture.Id}/files/sample-00-head.png");
            check(png.SequenceEqual(Png),"Observation API serves the exact validated lossless image bytes");
            for(var index=0;index<65;index++)
            {var inspection=runtime.Task(new("inspect-table"));await Finished(runtime,inspection.Id);}
            check(runtime.Job(job.Id)==null&&runtime.CalibrationObservationManifest(capture.Id).Length>0
                &&runtime.CalibrationObservationArtifact(capture.Id,"sample-00-head.png").SequenceEqual(Png),
                "Ordinary job pruning cannot delete or detach preserved calibration originals");
            await runtime.Shutdown();var restarted=new RoboticsRuntime(directory,Path.Combine(directory,"reservation"),new Tools(directory));restarted.Start("fixture");
            check(restarted.CalibrationObservation()?.Id==capture.Id&&restarted.Status().StopLatched&&restarted.Status().ArmedUntil==null,
                "Runtime restart retains validated observation access and cannot resume motor control");
            await restarted.Shutdown();
        }
        finally{await app.StopAsync();await runtime.Shutdown();}
    }
}
