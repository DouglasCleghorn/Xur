using System.Security.Cryptography;
using Xur.Robot.Backups;
namespace Xur.Robot.Backup;

// There are no delete endpoints or retention/pruning jobs. Only verified content
// can enter committed snapshots; interrupted uploads never become training data.
public sealed class BackupStorage(string root)
{
    readonly SemaphoreSlim gate=new(1,1);
    string Pending(string id)=>Path.Combine(root,"manifests",id+".json");
    string Completed(string id)=>Path.Combine(root,"snapshots",id);
    string Blob(string hash)=>Path.Combine(root,"blobs",hash[..2],hash);
    static void Id(string id){if(!BackupProtocol.Hash(id))throw new InvalidOperationException("Invalid snapshot identity.");}
    BackupManifest Manifest(string id)
    {
        Id(id);var path=Pending(id);if(!File.Exists(path))throw new InvalidOperationException("Upload the recording manifest first.");
        BackupProtocol.Plain(path);var manifest=BackupJson.Read<BackupManifest>(File.ReadAllBytes(path));BackupProtocol.Validate(manifest);
        if(BackupProtocol.SnapshotId(manifest)!=id)throw new InvalidOperationException("Stored manifest differs from its identity.");return manifest;
    }
    public async Task<BackupRemoteStatus> Prepare(string id,BackupManifest manifest,CancellationToken token=default)
    {
        Id(id);BackupProtocol.Validate(manifest);
        if(BackupProtocol.SnapshotId(manifest)!=id)throw new InvalidOperationException("Manifest checksum does not match the snapshot identity.");
        await gate.WaitAsync(token);
        try{if(!File.Exists(Pending(id)))BackupProtocol.Write(Pending(id),manifest);return await StatusCore(id,token);}
        finally{gate.Release();}
    }
    public async Task<BackupRemoteStatus> Status(string id,CancellationToken token=default)
    {await gate.WaitAsync(token);try{return await StatusCore(id,token);}finally{gate.Release();}}
    async Task<BackupRemoteStatus> StatusCore(string id,CancellationToken token)
    {
        var manifest=Manifest(id);var missing=new List<string>();
        foreach(var file in manifest.Files.DistinctBy(f=>f.Sha256))if(!await BackupProtocol.VerifyFile(Blob(file.Sha256),file,token))missing.Add(file.Sha256);
        DateTimeOffset? verified=null;var receipt=Path.Combine(Completed(id),"receipt.json");
        if(missing.Count==0&&File.Exists(receipt))
        {
            foreach(var file in manifest.Files)
                if(!await BackupProtocol.VerifyFile(BackupProtocol.FilePath(Path.Combine(Completed(id),"dataset"),file.Path),file,token))
                    throw new InvalidOperationException("The committed dataset differs from its verified snapshot. Preserve it and investigate storage.");
            verified=BackupJson.Read<BackupReceipt>(File.ReadAllBytes(receipt)).VerifiedAt;
        }
        return new(id,verified!=null?"verified":"pending",missing.ToArray(),verified);
    }
    public async Task Upload(string id,string hash,Stream input,CancellationToken token=default)
    {
        Id(id);if(!BackupProtocol.Hash(hash))throw new InvalidOperationException("Invalid file checksum.");
        await gate.WaitAsync(token);string? temporary=null;
        try
        {
            var manifest=Manifest(id);var expected=manifest.Files.FirstOrDefault(f=>f.Sha256==hash)
                ??throw new InvalidOperationException("File checksum is not declared in this manifest.");
            var staging=Path.Combine(root,".build","uploads");BackupDurability.EnsureDirectory(staging);
            temporary=Path.Combine(staging,Guid.NewGuid().ToString("N")+".part");
            using var checksum=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);long length=0;var buffer=new byte[128*1024];
            await using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,buffer.Length,true))
            {
                int count;while((count=await input.ReadAsync(buffer,token))!=0)
                {
                    length=checked(length+count);if(length>expected.Size)throw new InvalidOperationException("Uploaded file is larger than its manifest.");
                    checksum.AppendData(buffer,0,count);await file.WriteAsync(buffer.AsMemory(0,count),token);
                }
                await file.FlushAsync(token);file.Flush(flushToDisk:true);
            }
            if(length!=expected.Size||Convert.ToHexStringLower(checksum.GetHashAndReset())!=hash)
                throw new InvalidOperationException("Uploaded file checksum or length differs from its manifest.");
            var target=Blob(hash);BackupDurability.EnsureDirectory(Path.GetDirectoryName(target)!);
            // Atomic repair of the exact declared content is permitted; no
            // request can replace a blob with different bytes or remove one.
            File.Move(temporary,target,true);temporary=null;BackupDurability.Directory(Path.GetDirectoryName(target)!);
        }
        finally{if(temporary!=null)File.Delete(temporary);gate.Release();}
    }
    public async Task<BackupRemoteStatus> Commit(string id,CancellationToken token=default)
    {
        await gate.WaitAsync(token);
        try
        {
            var status=await StatusCore(id,token);
            if(status.MissingBlobs.Length!=0)throw new InvalidOperationException("Snapshot is incomplete; upload every declared file before committing.");
            if(status.State=="verified")return status;
            var manifest=Manifest(id);var stage=Path.Combine(root,".build","commits",Guid.NewGuid().ToString("N"));BackupDurability.EnsureDirectory(stage);
            foreach(var file in manifest.Files)
            {
                var target=BackupProtocol.FilePath(Path.Combine(stage,"dataset"),file.Path);BackupDurability.EnsureDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Blob(file.Sha256),target,false);
                if(!await BackupProtocol.VerifyFile(target,file,token))throw new InvalidOperationException("Snapshot copy failed checksum verification.");
                using(var durable=new FileStream(target,FileMode.Open,FileAccess.ReadWrite,FileShare.None))durable.Flush(flushToDisk:true);
                if(OperatingSystem.IsLinux())File.SetUnixFileMode(target,UnixFileMode.UserRead);
            }
            BackupProtocol.Write(Path.Combine(stage,"manifest.json"),manifest);
            var verifiedAt=DateTimeOffset.UtcNow;BackupProtocol.Write(Path.Combine(stage,"receipt.json"),new BackupReceipt(id,"receiver-local",verifiedAt));
            var completed=Completed(id);BackupDurability.EnsureDirectory(Path.GetDirectoryName(completed)!);BackupDurability.MoveDirectory(stage,completed);
            return new(id,"verified",[],verifiedAt);
        }
        finally{gate.Release();}
    }
}
