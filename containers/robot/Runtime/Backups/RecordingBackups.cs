using System.Net.Http.Headers;
using System.Security.Cryptography;
using Xur.Robot.Backups;
namespace Xur.Robot;

// Originals and sealed snapshots have no deletion or retention API. Uploads run
// independently of motor jobs; only this container owns connection credentials.
public sealed class RecordingBackups
{
    readonly object sync=new();readonly SemaphoreSlim uploading=new(1,1);
    readonly string state;readonly HttpClient http;readonly TimeProvider time;readonly TimeSpan deadline;
    public RecordingBackups(string state,HttpClient? client=null,TimeProvider? clock=null,TimeSpan? attemptTimeout=null)
    {
        this.state=state;http=client??new(new SocketsHttpHandler{AllowAutoRedirect=false}){Timeout=Timeout.InfiniteTimeSpan};
        time=clock??TimeProvider.System;deadline=attemptTimeout??TimeSpan.FromMinutes(30);
        try{Recover();}
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {Console.Error.WriteLine("Recording backup recovery needs inspection: "+Xur.Domain.Redaction.Logs(error.Message));}
    }
    string Root=>Path.Combine(state,"backups");
    string Configuration=>Path.Combine(Root,"connection.json");
    string Record(string id){if(!BackupProtocol.RecordingId(id))throw new InvalidOperationException("Invalid recording identity.");return Path.Combine(Root,"records",id+".json");}
    string Snapshot(string id){if(!BackupProtocol.RecordingId(id))throw new InvalidOperationException("Invalid recording identity.");return Path.Combine(Root,"snapshots",id);}
    BackupPrivateSettings? Private()
    {lock(sync)return File.Exists(Configuration)?BackupJson.Read<BackupPrivateSettings>(File.ReadAllBytes(Configuration)):null;}
    public BackupSettings Settings(){var saved=Private();return new(saved?.Url??"",BackupProtocol.Token(saved?.Token));}
    public BackupSettings Configure(BackupSettingsRequest request)
    {
        lock(sync)
        {
            var saved=Private();
            if(request.Url==""){BackupProtocol.Write(Configuration,new BackupPrivateSettings("",""),secret:true);return new("",false);}
            if(!Uri.TryCreate(request.Url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo!=""||uri.Query!=""||uri.Fragment!="")
                throw new InvalidOperationException("Use an HTTPS backup receiver URL without embedded credentials, query or fragment.");
            var url=uri.AbsoluteUri.TrimEnd('/')+"/";var token=request.Token;
            if(token==""&&saved?.Url==url)token=saved.Token;
            if(!BackupProtocol.Token(token))throw new InvalidOperationException("Provide the receiver's private backup token (32 to 512 non-whitespace characters).");
            BackupProtocol.Write(Configuration,new BackupPrivateSettings(url,token),secret:true);
            foreach(var record in Recordings().Where(r=>r.SnapshotId!=""&&(r.State!="verified"||r.Destination!=url)))
                Save(record with{State="pending",Error=null,NextAttemptAt=time.GetUtcNow(),UpdatedAt=time.GetUtcNow()});
            return new(url,true);
        }
    }
    public RecordingBackupStatus[] Recordings()
    {
        lock(sync)
        {
            var path=Path.Combine(Root,"records");if(!Directory.Exists(path))return [];
            return Directory.GetFiles(path,"*.json").Select(file=>BackupJson.Read<RecordingBackupStatus>(File.ReadAllBytes(file)))
                .OrderByDescending(r=>r.CreatedAt).ToArray();
        }
    }
    void Save(RecordingBackupStatus value){lock(sync)BackupProtocol.Write(Record(value.RecordingId),value);}
    public RecordingBackupStatus Retry(string id)
    {
        lock(sync)
        {
            var record=BackupJson.Read<RecordingBackupStatus>(File.ReadAllBytes(Record(id)));
            if(record.SnapshotId=="")throw new InvalidOperationException("The local snapshot did not complete. Originals remain in datasets; inspect the reported failure first.");
            if(Private() is not {Url.Length:>0})throw new InvalidOperationException("Configure the backup receiver before retrying.");
            var next=record with{State="pending",Error=null,NextAttemptAt=time.GetUtcNow(),UpdatedAt=time.GetUtcNow()};Save(next);return next;
        }
    }
    public RecordingBackupStatus BeginRecording(string dataset)
    {
        if(!BackupProtocol.Name(dataset))throw new InvalidOperationException("Invalid recording dataset.");
        var now=time.GetUtcNow();var status=new RecordingBackupStatus(Guid.NewGuid().ToString("N"),dataset,"","recording",now,now,Outcome:"interrupted");Save(status);return status;
    }
    void Recover()
    {
        foreach(var status in Recordings().Where(r=>r.State is "recording" or "snapshotting"))
        {
            var next=status with{State="incomplete",Outcome="interrupted",UpdatedAt=time.GetUtcNow(),
                Error="The app stopped before this recording or snapshot completed. Available originals and raw journals remain in datasets; inspect before training."};
            try
            {
                var folder=Snapshot(status.RecordingId);var stage=Path.Combine(Root,".build","snapshots",status.RecordingId);
                var sealedFolder=Directory.Exists(folder)?folder:stage;
                var manifestPath=Path.Combine(sealedFolder,"manifest.json");
                if(File.Exists(manifestPath))
                {
                    BackupProtocol.Plain(sealedFolder);BackupProtocol.Plain(manifestPath);
                    var manifest=BackupJson.Read<BackupManifest>(File.ReadAllBytes(manifestPath));BackupProtocol.Validate(manifest);
                    if(manifest.RecordingId!=status.RecordingId||manifest.Dataset!=status.Dataset)
                        throw new InvalidOperationException("Recovered snapshot identity does not match its durable recording index.");
                    foreach(var file in manifest.Files)
                        if(!BackupProtocol.VerifyFile(BackupProtocol.FilePath(Path.Combine(sealedFolder,"dataset"),file.Path),file,CancellationToken.None).GetAwaiter().GetResult())
                            throw new InvalidOperationException("Recovered snapshot files do not match their sealed manifest.");
                    if(sealedFolder==stage){BackupDurability.EnsureDirectory(Path.GetDirectoryName(folder)!);BackupDurability.MoveDirectory(stage,folder);}
                    next=status with{SnapshotId=BackupProtocol.SnapshotId(manifest),State=Settings().TokenStored?"pending":"unconfigured",
                        Outcome=manifest.Outcome,Error=null,UpdatedAt=time.GetUtcNow(),NextAttemptAt=time.GetUtcNow()};
                }
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
            {next=next with{State="snapshot-failed",Error=Xur.Domain.Redaction.Logs(error.Message)};}
            Save(next);
        }
    }
    public async Task<RecordingBackupStatus> CaptureCompleted(string dataset,string robotId,string arm,string task,BackupProvenance? provenance=null,string outcome="completed",string? recordingId=null)
    {
        if(!BackupProtocol.Name(dataset)||!BackupProtocol.Name(robotId)||arm is not ("left" or "right"))throw new InvalidOperationException("Invalid completed recording identity.");
        var began=recordingId==null?BeginRecording(dataset):BackupJson.Read<RecordingBackupStatus>(File.ReadAllBytes(Record(recordingId)));
        if(began.Dataset!=dataset||began.State!="recording")throw new InvalidOperationException("Recording snapshot does not match an active durable recording index.");
        var id=began.RecordingId;var now=time.GetUtcNow();var status=began with{State="snapshotting",UpdatedAt=now,Outcome=outcome};Save(status);
        try
        {
            var datasets=Path.Combine(state,"datasets");var source=Path.Combine(datasets,dataset);BackupProtocol.Plain(datasets);BackupProtocol.Plain(source);
            var stage=Path.Combine(Root,".build","snapshots",id);BackupDurability.EnsureDirectory(stage);var files=new List<BackupFile>();
            async Task CopyDirectory(string directory)
            {
                BackupProtocol.Plain(directory);
                foreach(var entry in Directory.GetFileSystemEntries(directory).Order(StringComparer.Ordinal))
                {
                    BackupProtocol.Plain(entry);if(Directory.Exists(entry)){await CopyDirectory(entry);continue;}
                    if(files.Count>=10000)throw new InvalidOperationException("Dataset file count exceeds its snapshot manifest limit.");
                    var relative=Path.GetRelativePath(source,entry).Replace(Path.DirectorySeparatorChar,'/');
                    var target=BackupProtocol.FilePath(Path.Combine(stage,"dataset"),relative);BackupDurability.EnsureDirectory(Path.GetDirectoryName(target)!);
                    await using(var input=new FileStream(entry,FileMode.Open,FileAccess.Read,FileShare.Read,128*1024,true))
                    await using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None,128*1024,true))
                    {if(input.Length>BackupProtocol.MaxFileBytes)throw new InvalidOperationException("A dataset file exceeds its backup limit.");await input.CopyToAsync(output);await output.FlushAsync();output.Flush(flushToDisk:true);}
                    await using var copied=new FileStream(target,FileMode.Open,FileAccess.Read,FileShare.Read,128*1024,true);
                    var file=new BackupFile(relative,copied.Length,Convert.ToHexStringLower(await SHA256.HashDataAsync(copied)));
                    if(!await BackupProtocol.VerifyFile(entry,file,CancellationToken.None))throw new InvalidOperationException("A dataset file changed while its immutable snapshot was copied.");
                    files.Add(file);if(OperatingSystem.IsLinux())File.SetUnixFileMode(target,UnixFileMode.UserRead);
                }
            }
            await CopyDirectory(source);var manifest=new BackupManifest(1,id,dataset,robotId,arm,task,now,files.OrderBy(f=>f.Path,StringComparer.Ordinal).ToArray(),provenance,outcome);
            BackupProtocol.Validate(manifest);var snapshotId=BackupProtocol.SnapshotId(manifest);BackupProtocol.Write(Path.Combine(stage,"manifest.json"),manifest);
            BackupDurability.EnsureDirectory(Path.GetDirectoryName(Snapshot(id))!);BackupDurability.MoveDirectory(stage,Snapshot(id));
            status=status with{SnapshotId=snapshotId,State=Settings().TokenStored?"pending":"unconfigured",UpdatedAt=time.GetUtcNow(),NextAttemptAt=time.GetUtcNow()};
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {status=status with{State="snapshot-failed",Error=Xur.Domain.Redaction.Logs(error.Message),UpdatedAt=time.GetUtcNow()};}
        Save(status);return status;
    }
    public async Task<bool> ProcessNext(CancellationToken token=default)
    {
        if(!await uploading.WaitAsync(0,token))return false;
        try
        {
            var configuration=Private();if(configuration is not {Url.Length:>0}||!BackupProtocol.Token(configuration.Token))return false;
            var status=Recordings().LastOrDefault(r=>r.SnapshotId!=""&&r.State!="verified"&&r.NextAttemptAt<=time.GetUtcNow());if(status==null)return false;
            status=status with{State="uploading",Attempts=status.Attempts+1,Error=null,UpdatedAt=time.GetUtcNow(),Destination=configuration.Url};Save(status);
            using var attempt=CancellationTokenSource.CreateLinkedTokenSource(token);attempt.CancelAfter(deadline);
            var attemptToken=attempt.Token;
            try
            {
                var folder=Snapshot(status.RecordingId);var manifest=BackupJson.Read<BackupManifest>(File.ReadAllBytes(Path.Combine(folder,"manifest.json")));BackupProtocol.Validate(manifest);
                if(BackupProtocol.SnapshotId(manifest)!=status.SnapshotId)throw new InvalidOperationException("The sealed local manifest changed; originals remain preserved.");
                foreach(var file in manifest.Files)
                    if(!await BackupProtocol.VerifyFile(BackupProtocol.FilePath(Path.Combine(folder,"dataset"),file.Path),file,attemptToken))
                        throw new InvalidOperationException("A sealed local sample differs from its manifest; originals remain preserved.");
                var prefix="api/snapshots/"+status.SnapshotId;
                using var prepared=await Send(configuration,HttpMethod.Post,prefix+"/manifest",new ByteArrayContent(BackupJson.Bytes(manifest)),true,attemptToken);
                var remote=await ReadStatus(prepared,attemptToken);
                if(remote.MissingBlobs==null||remote.SnapshotId!=status.SnapshotId||remote.MissingBlobs.Any(hash=>!manifest.Files.Any(f=>f.Sha256==hash)))
                    throw new InvalidOperationException("Receiver returned a different snapshot or undeclared content checksum.");
                foreach(var hash in remote.MissingBlobs.Distinct(StringComparer.Ordinal))
                {
                    var file=manifest.Files.First(f=>f.Sha256==hash);await using var input=new FileStream(BackupProtocol.FilePath(Path.Combine(folder,"dataset"),file.Path),FileMode.Open,FileAccess.Read,FileShare.Read,128*1024,true);
                    using var uploaded=await Send(configuration,HttpMethod.Put,prefix+"/blobs/"+hash,new StreamContent(input),false,attemptToken);
                }
                using var committed=await Send(configuration,HttpMethod.Post,prefix+"/commit",new ByteArrayContent(BackupJson.Bytes(new BackupEmptyRequest())),true,attemptToken);
                remote=await ReadStatus(committed,attemptToken);
                if(remote.MissingBlobs==null||remote.State!="verified"||remote.SnapshotId!=status.SnapshotId||remote.MissingBlobs.Length!=0||remote.VerifiedAt==null)
                    throw new InvalidOperationException("Receiver did not verify the complete recording snapshot.");
                var receipt=new BackupReceipt(status.SnapshotId,configuration.Url,remote.VerifiedAt.Value);
                BackupProtocol.Write(Path.Combine(Root,"receipts",status.RecordingId,BackupProtocol.Digest(System.Text.Encoding.UTF8.GetBytes(configuration.Url))+".json"),receipt);
                status=status with{State="verified",VerifiedAt=remote.VerifiedAt,NextAttemptAt=null,Error=null,UpdatedAt=time.GetUtcNow()};
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
            {
                status=status with{State="failed",Error=error is OperationCanceledException?"Backup attempt timed out or stopped; originals and snapshot remain local.":error is HttpRequestException?"Backup receiver request failed; originals and snapshot remain local.":Xur.Domain.Redaction.Logs(error.Message),
                    NextAttemptAt=time.GetUtcNow().AddSeconds(Math.Min(3600,10*Math.Pow(2,Math.Min(status.Attempts-1,9)))),UpdatedAt=time.GetUtcNow()};
            }
            if(status.State=="verified"&&Private()?.Url is {Length:>0} currentUrl&&currentUrl!=configuration.Url)
                status=status with{State="pending",NextAttemptAt=time.GetUtcNow(),UpdatedAt=time.GetUtcNow()};
            Save(status);return true;
        }
        finally{uploading.Release();}
    }
    static async Task<BackupRemoteStatus> ReadStatus(HttpResponseMessage response,CancellationToken token)
    {
        await using var source=await response.Content.ReadAsStreamAsync(token);using var body=new MemoryStream();var buffer=new byte[8192];int count;
        while((count=await source.ReadAsync(buffer,token))!=0)
        {if(body.Length+count>BackupProtocol.MaxManifestBytes)throw new InvalidOperationException("Receiver response exceeds its metadata limit.");await body.WriteAsync(buffer.AsMemory(0,count),token);}
        return BackupJson.Read<BackupRemoteStatus>(body.ToArray());
    }
    async Task<HttpResponseMessage> Send(BackupPrivateSettings configuration,HttpMethod method,string path,HttpContent body,bool json,CancellationToken token)
    {
        using var request=new HttpRequestMessage(method,new Uri(new Uri(configuration.Url),path)){Content=body};
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",configuration.Token);
        request.Content.Headers.ContentType=new(json?"application/json":"application/octet-stream");
        var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
        if(!response.IsSuccessStatusCode){var code=(int)response.StatusCode;response.Dispose();throw new InvalidOperationException("Backup receiver returned HTTP "+code+". Originals and sealed samples remain local.");}
        return response;
    }
}
public sealed class RecordingBackupWorker(RecordingBackups backups):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Restart resumes indexed snapshots, never a robot motion/recording job.
        while(!stoppingToken.IsCancellationRequested)
        {
            try{await backups.ProcessNext(stoppingToken);}
            catch(Exception error) when(error is IOException or System.Text.Json.JsonException or InvalidOperationException)
            {Console.Error.WriteLine("Recording backup state needs inspection: "+Xur.Domain.Redaction.Logs(error.Message));}
            await Task.Delay(TimeSpan.FromSeconds(5),stoppingToken);
        }
    }
}
