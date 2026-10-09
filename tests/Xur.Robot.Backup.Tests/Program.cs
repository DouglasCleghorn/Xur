using System.Net;
using System.Text;
using System.Text.Json;
using Xur.Robot;
using Xur.Robot.Backup;
using Xur.Robot.Backups;

var passed=new List<string>();void Check(bool value,string message){if(!value)throw new Exception(message);passed.Add(message);}
bool Invalid(Action action){try{action();return false;}catch(InvalidOperationException){return true;}}
async Task<bool> Failed(Func<Task> action){try{await action();return false;}catch(InvalidOperationException){return true;}}
var root=Path.GetFullPath(".build/evidence/recording-backups-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
try
{
    foreach(var path in new[]{"../secret","/etc/passwd","x/../../secret","x\\secret","C:/secret","x//file","x/./file","x\nfile"})
        Check(!BackupProtocol.RelativePath(path),"Rejects unsafe sample path: "+path);
    Check(BackupProtocol.RelativePath("meta/sidecars/raw-xr.jsonl")&&BackupProtocol.RelativePath(".metadata/source.json"),"Approved sidecars and metadata are preserved without assuming video/parquet only");
    var bytes=Encoding.UTF8.GetBytes("original-sample");var file=new BackupFile("data/chunk-000/sample.parquet",bytes.Length,BackupProtocol.Digest(bytes));
    var manifest=new BackupManifest(1,new string('a',32),"demonstration","robot","right","Sort the red block",DateTimeOffset.UtcNow,[file]);var id=BackupProtocol.SnapshotId(manifest);
    var storage=new BackupStorage(Path.Combine(root,"receiver"));var pending=await storage.Prepare(id,manifest);
    Check(pending.State=="pending"&&pending.MissingBlobs.SequenceEqual([file.Sha256]),"A manifest alone is never a verified backup");
    Check(await Failed(()=>storage.Commit(id)),"Incomplete snapshots cannot be committed");
    Check(await Failed(()=>storage.Upload(id,file.Sha256,new MemoryStream(Encoding.UTF8.GetBytes("corrupt-content")))),"Receiver rejects mismatched file checksum or length");
    Check((await storage.Status(id)).MissingBlobs.Length==1,"A corrupt upload cannot appear in completed content");
    Check(await Failed(()=>storage.Prepare(new string('f',64),manifest)),"Receiver rejects a manifest bound to the wrong snapshot identity");
    Check(Invalid(()=>BackupProtocol.Validate(manifest with{Files=[file,file]})),"Duplicate sample paths cannot overwrite one another");
    Check(Invalid(()=>BackupProtocol.Validate(manifest with{Files=[file with{Path="data"},file]})),"A declared file cannot also be another file's directory");
    Check(Invalid(()=>BackupProtocol.Validate(manifest with{Files=[file with{Path="a"},file with{Path="a-z"},file with{Path="a/b"}]})),
        "File-directory conflicts are rejected even when a different name sorts between the ancestor and child");
    await storage.Upload(id,file.Sha256,new MemoryStream(bytes));var committed=await storage.Commit(id);var repeated=await storage.Commit(id);
    Check(committed.State=="verified"&&committed.VerifiedAt!=null&&repeated.VerifiedAt==committed.VerifiedAt,"Only complete checksum-verified atomic snapshots receive an idempotent durable receipt");
    Check(File.ReadAllBytes(Path.Combine(root,"receiver/snapshots",id,"dataset",file.Path)).SequenceEqual(bytes),"Committed training samples match original bytes exactly");
    var restartedStore=new BackupStorage(Path.Combine(root,"receiver"));Check((await restartedStore.Status(id)).State=="verified","Receiver restart observes durable manifests, samples and receipt");
    var committedFile=Path.Combine(root,"receiver/snapshots",id,"dataset",file.Path);File.SetUnixFileMode(committedFile,UnixFileMode.UserRead|UnixFileMode.UserWrite);File.WriteAllText(committedFile,"tampered");
    Check(await Failed(()=>storage.Status(id)),"Stored sample corruption is detected instead of reporting a false verified backup");

    var state=Path.Combine(root,"robot");var dataset=Path.Combine(state,"datasets/demo");Directory.CreateDirectory(Path.Combine(dataset,"meta"));Directory.CreateDirectory(Path.Combine(dataset,"raw-xr"));
    var original=Path.Combine(dataset,"meta/info.json");File.WriteAllText(original,"{\"sample\":1}");File.WriteAllText(Path.Combine(dataset,"raw-xr/sequence.jsonl"),"{\"trackingValid\":true}");
    var service=new RecordingBackups(state);var captured=await service.CaptureCompleted("demo","robot","left","Demonstrate sorting",new("xbox",CalibrationHash:new string('c',64)));
    Check(captured.State=="unconfigured"&&captured.SnapshotId!=""&&!await service.ProcessNext(),"Unconfigured remote backups preserve a local snapshot without claiming verification");
    Check(File.ReadAllText(original)=="{\"sample\":1}","Capturing a snapshot never moves or deletes the original demonstration");
    var snapshot=Path.Combine(state,"backups/snapshots",captured.RecordingId);var sealedManifest=BackupJson.Read<BackupManifest>(File.ReadAllBytes(Path.Combine(snapshot,"manifest.json")));
    Check(sealedManifest.Files.Length==2&&sealedManifest.Files.Any(f=>f.Path=="raw-xr/sequence.jsonl")&&sealedManifest.Provenance?.InputSource=="xbox",
        "Immutable snapshots hash every dataset file and preserve honest input provenance and optional XR sidecars");
    File.WriteAllText(original,"{\"sample\":2}");Check(File.ReadAllText(Path.Combine(snapshot,"dataset/meta/info.json"))=="{\"sample\":1}","Appending or changing a live dataset cannot mutate earlier recording snapshots");
    foreach(var url in new[]{"http://xur-epyc/","https://user:secret@xur-epyc/","https://xur-epyc/?token=secret","https://xur-epyc/#secret"})
        Check(Invalid(()=>service.Configure(new(url,new string('t',64)))),"Connection settings reject insecure or credential-bearing URLs");
    Check(Invalid(()=>service.Configure(new("https://xur-epyc/","weak"))),"Weak backup credentials cannot be saved");
    var remoteStore=new BackupStorage(Path.Combine(root,"retry-receiver"));var transport=new ReceiverTransport(remoteStore);using var http=new HttpClient(transport);
    service=new RecordingBackups(state,http);service.Configure(new("https://xur-epyc/",new string('t',64)));
    Check(!JsonSerializer.Serialize(service.Settings()).Contains(new string('t',64))&&service.Settings().TokenStored,"Public settings never expose the saved receiver token");
    Check(File.GetUnixFileMode(Path.Combine(state,"backups/connection.json"))==(UnixFileMode.UserRead|UnixFileMode.UserWrite),"Private receiver credentials are persisted with owner-only file permissions");
    transport.FailUploadNumber=2;Check(await service.ProcessNext(),"A pending recording starts its container-owned backup attempt");var failure=service.Recordings().Single();
    Check(failure.State=="failed"&&failure.NextAttemptAt!=null&&failure.Attempts==1&&File.Exists(original),"Remote upload failures persist retry status while keeping originals and sealed samples");
    Check(!await service.ProcessNext(),"Automatic retries honor their recorded backoff instead of hammering an unavailable receiver");
    transport.FailUploadNumber=0;service.Retry(captured.RecordingId);await service.ProcessNext();var verified=service.Recordings().Single();
    Check(verified.State=="verified"&&verified.VerifiedAt!=null&&verified.Attempts==2,"Explicit retry resumes and verifies the original immutable recording snapshot");
    Check(transport.Uploads==3,"Resumed backups upload only missing content rather than retransferring already verified blobs");
    Check(File.Exists(original)&&File.Exists(Path.Combine(snapshot,"manifest.json")),"Successful remote verification also preserves local originals and immutable snapshot");
    var restarted=new RecordingBackups(state,http);Check(restarted.Recordings().Single().State=="verified"&&!await restarted.ProcessNext(),"App restart preserves remote verification without restarting recording or duplicating uploads");
    Check(Invalid(()=>restarted.Retry("../../config")),"Retry identities cannot traverse into settings or original datasets");
    Check(Invalid(()=>restarted.Configure(new("https://another-server/",""))),"Changing destinations requires a new explicit token instead of forwarding the old secret");
    var broken=Path.Combine(snapshot,"dataset/meta/info.json");File.SetUnixFileMode(broken,UnixFileMode.UserRead|UnixFileMode.UserWrite);File.WriteAllText(broken,"changed");
    restarted.Retry(captured.RecordingId);await restarted.ProcessNext();Check(restarted.Recordings().Single().State=="failed"&&File.Exists(original),"Changed sealed samples cannot be silently uploaded or lose the original dataset");
    var links=Path.Combine(state,"datasets/linked");Directory.CreateDirectory(links);var outside=Path.Combine(root,"outside.json");File.WriteAllText(outside,"keep");File.CreateSymbolicLink(Path.Combine(links,"link.json"),outside);
    var linked=await restarted.CaptureCompleted("linked","robot","right","Test link handling");Check(linked.State=="snapshot-failed"&&File.ReadAllText(outside)=="keep","Snapshot collection rejects symbolic links without modifying the referenced data");
    {
        var recoveryRoot=Path.Combine(root,"recover-recording");var path=Path.Combine(recoveryRoot,"datasets/pending/raw-recordings/session");Directory.CreateDirectory(path);
        var raw=Path.Combine(path,"frames.jsonl");File.WriteAllText(raw,"{\"frameIndex\":0}\n");
        var first=new RecordingBackups(recoveryRoot);var collecting=first.BeginRecording("pending");
        Check(first.Recordings().Single().State=="recording","Recording identity is durable before the upstream collector begins");
        var recovered=new RecordingBackups(recoveryRoot);var unfinished=recovered.Recordings().Single();
        Check(unfinished.RecordingId==collecting.RecordingId&&unfinished.State=="incomplete"&&unfinished.Outcome=="interrupted"&&unfinished.Error!=null&&File.Exists(raw),
            "Restart exposes interrupted collection without omitting its history, deleting raw samples or resuming motion");
        var complete=await recovered.CaptureCompleted("pending","robot","right","Test recovery");
        BackupProtocol.Write(Path.Combine(recoveryRoot,"backups/records",complete.RecordingId+".json"),complete with{State="snapshotting",SnapshotId=""});
        var afterRename=new RecordingBackups(recoveryRoot);var sealedRecord=afterRename.Recordings().Single(r=>r.RecordingId==complete.RecordingId);
        Check(sealedRecord.State=="unconfigured"&&sealedRecord.SnapshotId==complete.SnapshotId,
            "Restart recovers a verified local sealed manifest after rename but before the final index update");
        var final=Path.Combine(recoveryRoot,"backups/snapshots",complete.RecordingId);var stage=Path.Combine(recoveryRoot,"backups/.build/snapshots",complete.RecordingId);
        Directory.CreateDirectory(Path.GetDirectoryName(stage)!);Directory.Move(final,stage);
        BackupProtocol.Write(Path.Combine(recoveryRoot,"backups/records",complete.RecordingId+".json"),complete with{State="snapshotting",SnapshotId=""});
        var stagedRecovery=new RecordingBackups(recoveryRoot);Check(Directory.Exists(final)&&!Directory.Exists(stage)
            &&stagedRecovery.Recordings().Single(r=>r.RecordingId==complete.RecordingId).SnapshotId==complete.SnapshotId&&File.Exists(raw),
            "A fully sealed interrupted staging directory is verified and atomically recovered without deleting originals");
    }
    {
        var timeoutRoot=Path.Combine(root,"timeout-recording");var path=Path.Combine(timeoutRoot,"datasets/slow");Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"sample.json"),"retain");
        var destination=new BackupStorage(Path.Combine(root,"timeout-receiver"));var stalled=new StallingTransport(destination);using var stalledHttp=new HttpClient(stalled);
        var timed=new RecordingBackups(timeoutRoot,stalledHttp,attemptTimeout:TimeSpan.FromMilliseconds(200));timed.Configure(new("https://xur-epyc/",new string('t',64)));
        var recording=await timed.CaptureCompleted("slow","robot","left","Test stalled response");
        Check(await timed.ProcessNext().WaitAsync(TimeSpan.FromSeconds(3))&&timed.Recordings().Single().State=="failed"
            &&timed.Recordings().Single().Error!.Contains("timed out")&&File.Exists(Path.Combine(path,"sample.json")),
            "A receiver that sends headers then stalls its body is bounded by the whole-attempt deadline and preserves originals");
        stalled.Stall=false;timed.Retry(recording.RecordingId);await timed.ProcessNext().WaitAsync(TimeSpan.FromSeconds(3));
        Check(timed.Recordings().Single().State=="verified","Timed-out body reads release upload ownership so explicit retry can succeed");
    }
    foreach(var interrupted in new[]{false,true})
    {
        var recordingRoot=Path.Combine(root,interrupted?"interrupted-recording":"completed-recording");Directory.CreateDirectory(recordingRoot);
        var configuration=new RoboticsConfiguration("fixture","/dev/serial/by-id/left","/dev/serial/by-id/right","/dev/input/event77","/dev/v4l/by-id/head-video-index0","/dev/v4l/by-id/hand-video-index0",[],true,new(100,100,2));
        var recordings=new RecordingBackups(recordingRoot);
        var robot=new RoboticsRuntime(recordingRoot,Path.Combine(recordingRoot,"reservation"),new Recorder(recordingRoot,interrupted),backups:recordings);robot.Start("robot");robot.Configure(configuration);Calibration(recordingRoot,configuration);robot.Arm(new());
        var job=robot.Record(new("demonstration","right","Sort the red block",5));
        for(var retry=0;retry<200&&robot.Job(job.Id)?.State=="running";retry++)await Task.Delay(10);
        var preserved=recordings.Recordings().Single();
        Check(robot.Job(job.Id)?.State==(interrupted?"failed":"completed")&&robot.Status().StopLatched
            &&preserved.SnapshotId!=""&&preserved.Outcome==(interrupted?"interrupted":"completed")
            &&File.Exists(Path.Combine(recordingRoot,"datasets/demonstration/meta/info.json")),
            interrupted?"Failed recording hooks preserve interrupted samples after tool release without claiming a complete episode or resuming motion"
                :"Successful recording automatically creates a durable immutable snapshot through the actual runtime hook");
        var preservedManifest=BackupJson.Read<BackupManifest>(File.ReadAllBytes(Path.Combine(recordingRoot,"backups/snapshots",preserved.RecordingId,"manifest.json")));
        Check(preservedManifest.Outcome==preserved.Outcome&&preservedManifest.Provenance?.CalibrationHash!=null,
            "Backup manifests retain recording outcome and calibration provenance independently of byte verification");
    }

    {
        var recordingRoot=Path.Combine(root,"estop-recording");Directory.CreateDirectory(recordingRoot);
        var configuration=new RoboticsConfiguration("fixture","/dev/serial/by-id/left","/dev/serial/by-id/right","/dev/input/event77","/dev/v4l/by-id/head-video-index0","/dev/v4l/by-id/hand-video-index0",[],true,new(100,100,2));
        var recordings=new RecordingBackups(recordingRoot);var recorder=new CancellableRecorder(recordingRoot);
        var robot=new RoboticsRuntime(recordingRoot,Path.Combine(recordingRoot,"reservation"),recorder,backups:recordings);robot.Start("robot");robot.Configure(configuration);Calibration(recordingRoot,configuration);robot.Arm(new());
        var job=robot.Record(new("demonstration","right","Sort the red block",5));await recorder.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(recordings.Recordings().Single().State=="recording","The runtime persists backup history before its recording adapter captures hardware samples");robot.EmergencyStop();await robot.Stop();
        var preserved=recordings.Recordings().Single();
        Check(robot.Job(job.Id)?.State=="stopped"&&robot.Status().EmergencyStopLatched&&preserved.Outcome=="interrupted"&&preserved.SnapshotId!="",
            "E-stop cancellation preserves available interrupted recording samples after releasing the adapter and keeps motion latched off");
    }

}
finally
{
    foreach(var file in Directory.GetFiles(root,"*",SearchOption.AllDirectories))if(new FileInfo(file).LinkTarget==null)File.SetUnixFileMode(file,UnixFileMode.UserRead|UnixFileMode.UserWrite);
    Directory.Delete(root,true);
}
Console.WriteLine(JsonSerializer.Serialize(new{suite="RecordingBackups",passed}));

static void Calibration(string directory,RoboticsConfiguration configuration)
{
    var path=Path.Combine(directory,"calibration");Directory.CreateDirectory(path);var joints=new[]{"shoulder_pan","shoulder_lift","elbow_flex","wrist_flex","wrist_roll","gripper"};var files=new Dictionary<string,string>();
    foreach(var name in new[]{configuration.RobotId,configuration.RobotId+"-left",configuration.RobotId+"-right"})
    {
        var names=name==configuration.RobotId?joints.SelectMany(j=>new[]{"left_arm_"+j,"right_arm_"+j}).Concat(new[]{"head_motor_1","head_motor_2","base_left_wheel","base_back_wheel","base_right_wheel"}):joints;
        var values=names.ToDictionary(n=>n,n=>new{id=n switch{"head_motor_1"=>7,"head_motor_2"=>8,"base_left_wheel"=>7,"base_back_wheel"=>8,"base_right_wheel"=>9,_=>Array.IndexOf(joints,n.Replace("left_arm_","").Replace("right_arm_",""))+1},drive_mode=0,homing_offset=0,range_min=100,range_max=3995});
        var file=Path.Combine(path,name+".json");File.WriteAllText(file,JsonSerializer.Serialize(values));files[name+".json"]=BackupProtocol.Digest(File.ReadAllBytes(file));
    }
    File.WriteAllText(Path.Combine(path,"receipt.json"),JsonSerializer.Serialize(new{robotId=configuration.RobotId,leftPort=configuration.LeftPort,rightPort=configuration.RightPort,files}));
}
sealed class Recorder(string root,bool interrupted):IRobotTools
{
    public Task<JsonElement> Run(RoboticsConfiguration configuration,string operation,object? request,int seconds,CancellationToken token)
    {
        if(operation!="record")throw new InvalidOperationException("Unexpected fake operation");var path=Path.Combine(root,"datasets/demonstration/meta");Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"info.json"),"{\"total_episodes\":1}");
        if(interrupted)throw new InvalidOperationException("Controller disconnected after finalized samples were written");
        return Task.FromResult(JsonSerializer.SerializeToElement(new{recorded=true}));
    }
}
sealed class CancellableRecorder(string root):IRobotTools
{
    public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<JsonElement> Run(RoboticsConfiguration configuration,string operation,object? request,int seconds,CancellationToken token)
    {
        var path=Path.Combine(root,"datasets/demonstration/meta");Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"info.json"),"{\"total_episodes\":0}");Started.TrySetResult();
        await Task.Delay(Timeout.Infinite,token);return default;
    }
}
class ReceiverTransport(BackupStorage storage):HttpMessageHandler
{
    public int Uploads,FailUploadNumber;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        if(request.Headers.Authorization?.Parameter!=new string('t',64))return new(HttpStatusCode.Unauthorized);
        var parts=request.RequestUri!.AbsolutePath.Trim('/').Split('/');var id=parts[2];
        try
        {
            if(parts[3]=="manifest")return Json(await storage.Prepare(id,BackupJson.Read<BackupManifest>(await request.Content!.ReadAsByteArrayAsync(token)),token));
            if(parts[3]=="commit")return Json(await storage.Commit(id,token));
            if(parts[3]=="blobs")
            {
                Uploads++;if(Uploads==FailUploadNumber)return new(HttpStatusCode.ServiceUnavailable);
                await storage.Upload(id,parts[4],await request.Content!.ReadAsStreamAsync(token),token);return new(HttpStatusCode.NoContent);
            }
            return new(HttpStatusCode.NotFound);
        }
        catch(InvalidOperationException){return new(HttpStatusCode.Conflict);}
    }
    static HttpResponseMessage Json(BackupRemoteStatus value)=>new(HttpStatusCode.OK){Content=new ByteArrayContent(BackupJson.Bytes(value))};
}

sealed class StallingTransport(BackupStorage storage):ReceiverTransport(storage)
{
    public bool Stall=true;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        =>Stall?Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(new StalledStream())}):base.SendAsync(request,token);
}
sealed class StalledStream:Stream
{
    public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
    public override long Length=>throw new NotSupportedException();public override long Position{get=>0;set=>throw new NotSupportedException();}
    public override void Flush(){}public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default){await Task.Delay(Timeout.Infinite,token);return 0;}
    public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
    public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
}
