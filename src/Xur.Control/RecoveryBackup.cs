using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Xur.Domain;
namespace Xur.Control;

// Deliberately scoped to configuration and identities. Never recurse into workload data.
public sealed class RecoveryBackup(string root="/")
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){WriteIndented=true};
    static readonly SemaphoreSlim Gate=new(1,1);
    public static readonly string[] Sources=[
        "/var/lib/xur/administrator.json","/var/lib/xur/manager-account.json","/var/lib/xur/api-keys.json",
        "/var/lib/xur/session-signing.key","/var/lib/xur/manager-tls.pfx","/var/lib/xur/secrets",
        "/var/lib/xur/gpu-power.json","/var/lib/xur/gpu-labels.json","/var/lib/xur/timezone","/var/lib/xur/timezone-mode",
        "/var/lib/xur/updates/settings.json","/var/lib/xur/catalog-selected","/var/lib/xur/station-users",
        "/var/lib/xur/model-library.json","/var/lib/xur/installed",
        "/etc/xur","/etc/NetworkManager/system-connections","/etc/NetworkManager/conf.d",
        "/etc/machine-id","/var/lib/dbus/machine-id","/etc/hostname","/etc/hosts","/etc/resolv.conf","/etc/localtime","/etc/chrony.conf","/etc/chrony.d",
        "/etc/passwd","/etc/shadow","/etc/group","/etc/gshadow","/etc/subuid","/etc/subgid",
        "/etc/sudoers","/etc/sudoers.d","/etc/pam.d","/etc/polkit-1","/etc/fstab","/etc/crypttab","/etc/ssh","/etc/firewalld","/etc/containers","/etc/systemd/system",
        "/root/.ssh","/root/.config/containers/auth.json","/var/lib/tailscale/tailscaled.state",
        "/var/lib/tailscale/serve-config","/var/lib/xur-streaming/ports"
    ];
    public static readonly string[] Exclusions=[
        "Models, games, home directories and all other user files",
        "Container images, writable layers and volumes (including locally built images)",
        "OS/application binaries, caches, logs, benchmarks, live process receipts and transient device grants",
        "Deleted container build contexts (Xur removes these after builds); preserve Dockerfiles separately",
        "External disks, external services and hardware-bound secrets that cannot be restored on another machine"
    ];
    const long MaxFile=256L*1024*1024,MaxTotal=1024L*1024*1024;
    const int MaxEntries=20000;

    public async Task<RecoveryArchive> Create(string temporaryRoot,Func<string,Task<ConfigurationSnapshot>> snapshot,CancellationToken ct=default)
    {
        if(!await Gate.WaitAsync(0,ct))throw new InvalidOperationException("A backup is already being prepared. Try again after it finishes.");
        var directory=Path.Combine(temporaryRoot,"backup-"+Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            var started=DateTimeOffset.UtcNow;var database=Path.Combine(directory,"profiles.db");
            var configuration=await snapshot(database);ct.ThrowIfCancellationRequested();
            var archivePath=Path.Combine(directory,"recovery.zip");
            var entries=new List<BackupEntry>();var absent=new List<string>();long total=0;
            using(var file=new FileStream(archivePath,new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite}))
            using(var zip=new ZipArchive(file,ZipArchiveMode.Create))
            {
                await AddFile(database,"/var/lib/xur/profiles.db",true);
                foreach(var source in Sources)await Add(source,true);
                // Only pairing/config files, not Sunshine's growing logs or installed runtime.
                var streaming=HostPath("/var/lib/xur-streaming");
                CheckParents(streaming);
                if(Directory.Exists(streaming))
                {
                    if(new DirectoryInfo(streaming).LinkTarget!=null)throw new IOException("Streaming configuration directory is a symbolic link.");
                    foreach(var folder in Directory.EnumerateDirectories(streaming).Order(StringComparer.Ordinal))
                    {
                        var id=Path.GetFileName(folder);if(id=="ports")continue;
                        if(!ProfilePolicy.EntityIdentifier(id))throw new IOException("Unexpected workstation configuration directory.");
                        foreach(var name in new[]{"sunshine.conf","apps.json","key.pem","cert.pem","manager.json","state/sunshine_state.json","state/credentials.json"})
                            await Add("/var/lib/xur-streaming/"+id+"/"+name,true);
                    }
                }
                else absent.Add("/var/lib/xur-streaming");
                await Generated("configuration.json",JsonSerializer.SerializeToUtf8Bytes(new{schema=1,configuration.Profiles,configuration.Workstations},Json));
                await Generated("RESTORE.md",Encoding.UTF8.GetBytes(RestoreGuide));
                var metadata=new{
                    schema=1,kind="xur-recovery-configuration",backupId=Guid.NewGuid().ToString("N"),startedAtUtc=started,completedAtUtc=DateTimeOffset.UtcNow,
                    application=new{id=ApplicationIdentity.Id,version=typeof(RecoveryBackup).Assembly.GetName().Version?.ToString()},
                    machine=new{hostname=ReadIdentity("/etc/hostname"),osRelease=ReadIdentity("/etc/os-release")??ReadIdentity("/usr/lib/os-release"),architecture=RuntimeInformation.OSArchitecture.ToString()},
                    containsSecrets=true,encrypted=false,automaticRestoreAvailable=false,
                    consistency="SQLite online snapshot verified with integrity_check. Other files are captured during the recorded interval and checked for changes while read; this is not an atomic snapshot of all services. Avoid configuration changes while taking a backup.",
                    database="files/var/lib/xur/profiles.db",fileCount=entries.Count,uncompressedBytes=total,
                    exclusions=Exclusions,notPresent=absent,files=entries,
                    restore="Read RESTORE.md before restoring. Symlinks are described in metadata only. File hashes are SHA-256; they detect corruption, not a maliciously replaced archive."
                };
                // Metadata cannot contain its own checksum. All payload entries are covered.
                var manifest=zip.CreateEntry("metadata.json",CompressionLevel.Optimal);manifest.ExternalAttributes=(0x8180<<16);
                using var output=manifest.Open();await JsonSerializer.SerializeAsync(output,metadata,Json,ct);

                async Task Generated(string name,byte[] content)
                {
                    var e=zip.CreateEntry(name,CompressionLevel.Optimal);e.ExternalAttributes=(0x8180<<16);
                    using(var output=e.Open())await output.WriteAsync(content,ct);
                    total+=content.Length;
                    entries.Add(new(name,null,"generated",content.Length,Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),"0600",null,null,null,null));
                }
                async Task Add(string source,bool optional=false)
                {
                    ct.ThrowIfCancellationRequested();if(entries.Count>=MaxEntries)throw new IOException("Configuration backup exceeds the file count limit.");
                    var path=HostPath(source);CheckParents(path);
                    var info=new FileInfo(path);
                    if(!Path.Exists(path)&&info.LinkTarget==null){if(optional){absent.Add(source);return;}throw new IOException("Configuration disappeared during backup: "+source);}
                    var stat=await Stat(path,ct);
                    if((stat.Mode&0xf000)==0xa000)
                    {
                        if(Directory.Exists(path))throw new IOException("Configuration directory is a symbolic link: "+source);
                        // Record links, but do not traverse into arbitrary user data or the OS image.
                        entries.Add(new(null,source,"symlink",0,null,stat.Permissions,stat.Uid,stat.Gid,info.LastWriteTimeUtc,info.LinkTarget));return;
                    }
                    if((stat.Mode&0xf000)==0x4000)
                    {
                        entries.Add(new(null,source,"directory",0,null,stat.Permissions,stat.Uid,stat.Gid,Directory.GetLastWriteTimeUtc(path),null));
                        var children=Directory.GetFileSystemEntries(path).Order(StringComparer.Ordinal).ToArray();
                        foreach(var child in children)await Add(source+"/"+Path.GetFileName(child));
                        if(!children.SequenceEqual(Directory.GetFileSystemEntries(path).Order(StringComparer.Ordinal)))throw new IOException("Configuration directory changed during backup: "+source);
                        return;
                    }
                    await AddFile(path,source,false);
                }
                async Task AddFile(string path,string source,bool databaseSnapshot)
                {
                    var before=await Stat(path,ct);
                    if((before.Mode&0xf000)!=0x8000)throw new IOException("Configuration is not a regular file: "+source);
                    if(before.Size>MaxFile||total+before.Size>MaxTotal)throw new IOException("Configuration backup exceeds its size limit. No incomplete backup was created.");
                    var name="files"+source;
                    var e=zip.CreateEntry(name,CompressionLevel.Optimal);e.ExternalAttributes=((databaseSnapshot?0x8180:before.Mode)<<16);
                    using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    // Linux O_NOFOLLOW + O_NONBLOCK prevents a replaced link/FIFO from being followed or hanging the server.
                    var fd=Open(path,0x20000|0x800|0x80000);
                    if(fd<0)throw new IOException("Could not open configuration: "+source);
                    using var input=new FileStream(new SafeFileHandle((IntPtr)fd,true),FileAccess.Read);
                    using(var output=e.Open())
                    {
                        var buffer=new byte[64*1024];long written=0;int count;
                        while((count=await input.ReadAsync(buffer,ct))>0)
                        {
                            written+=count;if(written>MaxFile||total+written>MaxTotal)throw new IOException("Configuration grew beyond the backup size limit.");
                            hash.AppendData(buffer,0,count);await output.WriteAsync(buffer.AsMemory(0,count),ct);
                        }
                        if(written!=before.Size)throw new IOException("Configuration changed during backup: "+source);
                    }
                    if(before!=await Stat(path,ct))throw new IOException("Configuration changed during backup: "+source);
                    total+=before.Size;
                    entries.Add(new(name,source,databaseSnapshot?"sqlite-snapshot":"file",before.Size,Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),databaseSnapshot?"0600":before.Permissions,before.Uid,before.Gid,File.GetLastWriteTimeUtc(path),null));
                }
            }
            return new(directory,archivePath,"xur-backup-"+started.ToString("yyyyMMdd-HHmmss")+".zip");
        }
        catch {if(Directory.Exists(directory))Directory.Delete(directory,true);throw;}
        finally {Gate.Release();}
    }
    string HostPath(string source)=>Path.Combine(Path.GetFullPath(root),source.TrimStart('/'));
    void CheckParents(string path)
    {
        var boundary=Path.GetFullPath(root).TrimEnd('/');
        for(var parent=Path.GetDirectoryName(path);parent!=null&&parent.Length>boundary.Length;parent=Path.GetDirectoryName(parent))
            if(new DirectoryInfo(parent).LinkTarget!=null)throw new IOException("Configuration parent is a symbolic link: "+parent);
    }
    string? ReadIdentity(string source)
    {
        var path=HostPath(source);CheckParents(path);var file=new FileInfo(path);
        // OS release is commonly an immutable-image symlink; do not traverse it in fixture roots.
        return file.Exists&&file.LinkTarget==null&&file.Length<64*1024?File.ReadAllText(path).Trim():null;
    }
    [DllImport("libc",EntryPoint="open",SetLastError=true)]static extern int Open(string path,int flags);
    record FileStat(int Mode,int Uid,int Gid,long Size,string Identity)
    { public string Permissions=>Convert.ToString(Mode&0xfff,8).PadLeft(4,'0'); }
    static async Task<FileStat> Stat(string path,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result=await Processes.Run("stat",["--printf=%f|%u|%g|%s|%y|%z|%i|%d","--",path],10,ct);
        if(result.ExitCode!=0)throw new IOException("Could not read configuration metadata: "+path);
        var fields=result.Output.Split('|');if(fields.Length!=8)throw new IOException("Invalid configuration metadata.");
        return new(Convert.ToInt32(fields[0],16),int.Parse(fields[1]),int.Parse(fields[2]),long.Parse(fields[3]),result.Output);
    }
    public const string RestoreGuide="""
        # Xur configuration and identity recovery

        This ZIP contains credentials and private keys. Keep it private. ZIP compression is not encryption.
        It is NOT a backup of models, container images/volumes, games or home directories. Back up those
        separately. Locally built images must be saved separately: Xur deletes their build contexts.

        1. Read metadata.json: verify the Xur version, host, backup interval, exclusions and notPresent
           entries. Verify each payload's SHA-256 and size before use. This is not a signed archive.
        2. Install a compatible Xur version and restore data disks/home directories separately. Keep the
           original machine offline when restoring machine identities. Do not run two hosts with the same
           machine ID, Tailscale, SSH, HTTPS or Moonlight identity. Hardware-bound keys may require re-enrollment.
        3. Stop Xur control/agent/gateway, workstation streaming, and services whose configuration will be
           restored. Keep local console access before changing network or account settings.
        4. files/ mirrors original absolute paths, but DO NOT blindly extract it over a running system.
           Review /etc/passwd, shadow, group, gshadow, subuid and subgid; merge workstation/admin accounts
           with matching UID/GID rather than replacing the new OS's system accounts. Restore home files
           separately. Review fstab/crypttab, network interfaces and GPU assignments against new hardware.
        5. Restore the standalone profiles.db only with Xur stopped. Remove old profiles.db-wal and
           profiles.db-shm sidecars first. The snapshot contains committed WAL data, revisions and journals.
           Running processes are not backed up: review the active profile and operation journal before
           restarting; do not assume previously running workloads or partial operations remain valid.
        6. Restore selected configuration/identity files to their source paths. metadata.json records
           UID/GID, Unix permissions, directory metadata and symbolic-link targets. Recreate reviewed
           links explicitly (there are no symlink payloads). Restore restrictive credential permissions
           and ownership before starting services; run restorecon on restored paths for SELinux labels.
        7. Restore TLS/API/account/Hugging Face identities and Sunshine pairing files/port reservations.
           Restore Tailscale state with tailscaled stopped, or re-enroll the machine if identity recovery
           is not supported. Restoring the session signing key also restores trust in unexpired sessions;
           omit it to invalidate old sessions. Review authorized SSH keys before enabling remote access.
        8. Restart services, confirm local login/network access, review profiles and devices, then load
           workloads deliberately. Validate Moonlight pairing, API access and update-signing settings.

        SQLite is captured using its online backup API and checked with integrity_check. Other files
        are captured individually during the recorded interval, not at one global instant. Avoid settings
        changes while backing up. Automatic restore is not implemented. Missing optional files are listed
        explicitly; read failures and detected mid-read changes abort the entire archive.
        """;
}
public record BackupEntry(string? ArchivePath,string? SourcePath,string Kind,long Bytes,string? Sha256,string Mode,int? Uid,int? Gid,DateTime? ModifiedAtUtc,string? LinkTarget);
public sealed record RecoveryArchive(string DirectoryPath,string Path,string DownloadName):IDisposable
{
    public Stream OpenDownload()=>new CleanupStream(Path,DirectoryPath);
    public void Dispose(){if(Directory.Exists(DirectoryPath))Directory.Delete(DirectoryPath,true);}
    sealed class CleanupStream(string path,string directory):FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)
    {
        protected override void Dispose(bool disposing){base.Dispose(disposing);if(disposing&&Directory.Exists(directory))Directory.Delete(directory,true);}
        public override async ValueTask DisposeAsync(){await base.DisposeAsync();if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
}
