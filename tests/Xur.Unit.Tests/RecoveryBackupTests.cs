using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Xur.Control;
using Xur.Domain;
static class RecoveryBackupTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"xur-backup-test-"+Guid.NewGuid());Directory.CreateDirectory(root);
        var machine=root+"/machine";Directory.CreateDirectory(machine);var staging=root+"/private";
        void Write(string path,string content){var file=machine+path;Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllText(file,content);}
        try
        {
            Write("/var/lib/xur/manager-account.json","{\"passwordHash\":\"secret-hash\"}");
            Write("/var/lib/xur/api-keys.json","{\"secret\":\"API-SECRET\"}");
            Write("/var/lib/xur/secrets/huggingface-token","HF-SECRET");
            Write("/var/lib/xur/gpu-labels.json","{\"gpu\":3}");
            Write("/var/lib/xur/model-library.json","{\"folder\":\"/models\"}");
            Write("/etc/xur/application-update-key.pem","SIGNING-PUBLIC-KEY");
            Write("/etc/NetworkManager/system-connections/lan.nmconnection","[connection]\nid=lan\n");
            Write("/etc/shadow","user:HASH:1:2:3:4:5:6:7\n");
            Write("/var/lib/tailscale/tailscaled.state","TAILSCALE-IDENTITY");
            Write("/var/lib/xur-streaming/w1/key.pem","PAIRING-KEY");
            Write("/var/lib/xur-streaming/w1/state/sunshine_state.json","{\"client\":\"PAIRED\"}");
            Write("/var/lib/xur-streaming/ports/w1","47989");
            Write("/var/lib/xur-streaming/w1/state/sunshine.log","DO-NOT-ARCHIVE-LOGS");
            Write("/var/lib/xur/models/weights.bin","DO-NOT-ARCHIVE-MODELS");
            Write("/var/home/user/game.dat","DO-NOT-ARCHIVE-HOME");
            Write("/etc/hostname","fixture-host\n");Write("/usr/lib/os-release","ID=xur\nVERSION_ID=test\n");
            File.CreateSymbolicLink(machine+"/etc/localtime","/usr/share/zoneinfo/UTC");
            File.SetUnixFileMode(machine+"/etc/shadow",UnixFileMode.UserRead);
            using var store=new ProfileStore(machine+"/var/lib/xur");
            var profile=new Profile("1","Recover me",1,[]);store.Save(profile);
            store.Put("station","unused",new StationDefinition("unused","Unused desktop",new("temporary",0,true)));
            var manager=new ProfileManager(store,new Runtime(),new Gateway());
            var backup=new RecoveryBackup(machine);
            var started=DateTimeOffset.UtcNow;
            using(var result=await backup.Create(staging,manager.BackupConfiguration))
            {
                check((File.GetUnixFileMode(result.DirectoryPath)&(UnixFileMode.GroupRead|UnixFileMode.OtherRead))==0,"Recovery backup staging is private");
                using var zip=ZipFile.OpenRead(result.Path);
                string Read(string name){using var r=new StreamReader(zip.GetEntry(name)!.Open());return r.ReadToEnd();}
                using var metadata=JsonDocument.Parse(Read("metadata.json"));var manifest=metadata.RootElement;
                check(manifest.GetProperty("startedAtUtc").GetDateTimeOffset()>=started&&manifest.GetProperty("completedAtUtc").GetDateTimeOffset()>=manifest.GetProperty("startedAtUtc").GetDateTimeOffset(),"Recovery metadata records UTC backup start and completion");
                check(manifest.GetProperty("containsSecrets").GetBoolean()&&!manifest.GetProperty("encrypted").GetBoolean()&&manifest.GetProperty("exclusions").GetArrayLength()>0,"Recovery metadata explicitly describes secrets, encryption and omitted data");
                check(Read("files/var/lib/xur/api-keys.json").Contains("API-SECRET")&&Read("files/var/lib/xur/secrets/huggingface-token")=="HF-SECRET"&&Read("files/etc/xur/application-update-key.pem")=="SIGNING-PUBLIC-KEY","Recovery archive includes credentials and update verification key outside the database");
                check(Read("files/var/lib/tailscale/tailscaled.state")=="TAILSCALE-IDENTITY"&&Read("files/var/lib/xur-streaming/w1/key.pem")=="PAIRING-KEY"&&Read("files/var/lib/xur-streaming/w1/state/sunshine_state.json").Contains("PAIRED")&&Read("files/var/lib/xur-streaming/ports/w1")=="47989","Recovery preserves Tailscale and Moonlight identities and port assignments");
                check(zip.GetEntry("files/etc/NetworkManager/system-connections/lan.nmconnection")!=null&&zip.GetEntry("files/etc/shadow")!=null,"Recovery preserves network configuration and system account records");
                check(zip.Entries.All(e=>!e.FullName.Contains("weights.bin")&&!e.FullName.Contains("game.dat")&&!e.FullName.EndsWith("sunshine.log")&&!e.FullName.EndsWith("-wal")),"Recovery excludes workload data, streaming logs and live SQLite sidecars");
                var inventory=manifest.GetProperty("files").EnumerateArray().ToArray();
                foreach(var entry in zip.Entries.Where(e=>e.FullName!="metadata.json"))
                {
                    var item=inventory.Single(i=>i.GetProperty("archivePath").GetString()==entry.FullName);
                    using var content=entry.Open();var actual=Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
                    check(actual==item.GetProperty("sha256").GetString()&&entry.Length==item.GetProperty("bytes").GetInt64(),"Archive payload hash and size match: "+entry.FullName);
                }
                check(inventory.Single(i=>i.GetProperty("sourcePath").GetString()=="/etc/shadow").GetProperty("mode").GetString()=="0400","Recovery records restrictive original credential permissions");
                check(inventory.Single(i=>i.GetProperty("sourcePath").GetString()=="/etc/localtime").GetProperty("linkTarget").GetString()=="/usr/share/zoneinfo/UTC"&&zip.GetEntry("files/etc/localtime")==null,"Symbolic links are recorded without following external targets");
                check(manifest.GetProperty("notPresent").EnumerateArray().Any(v=>v.GetString()=="/var/lib/xur/manager-tls.pfx"),"Missing optional identity files are explicit in metadata");
                check(Read("RESTORE.md").Contains("DO NOT blindly extract")&&Read("configuration.json").Contains("Recover me"),"Archive includes restoration instructions and readable configuration");
                var restored=root+"/restored.db";zip.GetEntry("files/var/lib/xur/profiles.db")!.ExtractToFile(restored);
                using var db=new SqliteConnection("Data Source="+restored+";Pooling=False");db.Open();using var q=db.CreateCommand();q.CommandText="PRAGMA integrity_check";
                check((string?)q.ExecuteScalar()=="ok","Downloaded SQLite snapshot is independently valid");q.CommandText="SELECT json FROM documents WHERE kind='profile' AND id='1'";
                check(((string?)q.ExecuteScalar())?.Contains("Recover me")==true,"SQLite snapshot includes committed data from the live WAL database");
            }
            check(Directory.GetDirectories(staging).Length==0,"Disposing a recovery archive removes staging files");
            var callback=false;var context=new DefaultHttpContext();context.Request.Host=new HostString("xur.test");
            var redirect=await BackupEndpoints.Download(context,false,8080,()=>{callback=true;throw new Exception();});
            check(!callback&&redirect is RedirectHttpResult {Url:"https://xur.test:8443/settings/backup"},"Plain HTTP redirects to encrypted backup download before collecting secrets");
            context.Request.Scheme="https";
            var download=await BackupEndpoints.Download(context,false,8080,()=>backup.Create(staging,manager.BackupConfiguration));
            check(download is FileStreamHttpResult {ContentType:"application/zip"}&&context.Response.Headers.CacheControl=="no-store","Recovery download uses ZIP attachment and no-store");
            await ((FileStreamHttpResult)download).FileStream.DisposeAsync();
            check(Directory.GetDirectories(staging).Length==0,"Disposing the download stream removes its secret archive");
            Write("/etc/xur/linked-secret","original");File.Delete(machine+"/etc/xur/linked-secret");File.CreateSymbolicLink(machine+"/etc/xur/linked-secret",root+"/external-secret");File.WriteAllText(root+"/external-secret","DO-NOT-FOLLOW");
            using(var safe=await backup.Create(staging,manager.BackupConfiguration)){using var zip=ZipFile.OpenRead(safe.Path);check(zip.GetEntry("files/etc/xur/linked-secret")==null,"An arbitrary symlink never pulls its target into the archive");}
            File.Delete(machine+"/etc/xur/linked-secret");
            var fifo=machine+"/etc/xur/pipe";await Processes.Run("mkfifo",[fifo]);
            bool failed=false;try{using var bad=await backup.Create(staging,manager.BackupConfiguration);}catch(IOException){failed=true;}
            check(failed&&Directory.GetDirectories(staging).Length==0,"Unexpected special files fail closed and clean up incomplete backup");File.Delete(fifo);
            var error=await BackupEndpoints.Download(context,false,8080,()=>throw new IOException("Fixture read failure"));
            check(error is RedirectHttpResult failure&&failure.Url!.StartsWith("/settings?error="),"Backup failures return to Settings instead of downloading partial content");
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            try{using var bad=await backup.Create(staging,manager.BackupConfiguration,cancelled.Token);check(false,"Cancelled backup must stop");}catch(OperationCanceledException){}
            check(Directory.GetDirectories(staging).Length==0,"Cancelled backup leaves no secret staging files");
        }
        finally{Directory.Delete(root,true);}
    }
    sealed class Runtime:IWorkloadRuntime
    {
        public Task<RuntimeObservation> Observe()=>Task.FromResult(new RuntimeObservation("test",[],[]));
        public Task<RuntimeInstance> Start(Workload w)=>throw new NotSupportedException();public Task Stop(RuntimeStop r)=>throw new NotSupportedException();
    }
    sealed class Gateway:IWorkloadGateway {public Task Drain(string id)=>Task.CompletedTask;public Task Publish(BackendRoute[] routes)=>Task.CompletedTask;}
}
