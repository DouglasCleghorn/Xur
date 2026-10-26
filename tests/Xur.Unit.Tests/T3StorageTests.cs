using System.Text.Json;
using Xur.Agent;
using Xur.Domain;

static class T3StorageTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(".build/evidence/t3-storage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var data=new DataStorage(root);var settings=new T3Storage(root,data);
            var defaults=settings.Read();var args=await settings.Arguments();
            check(defaults is {AllDrives:true,Models:true}&&args.Contains("/:/host:rw,rbind,rslave"),"T3 defaults to recursive read/write access to server folders and drives");
            check(args.Contains(root+"/models:/models:rw,rbind,rslave")&&args.Contains(root+"/model-sets:/model-sets:rw,rbind,rslave"),"Both model stores have stable default mounts");
            check(args.Contains("1000:1000")&&args.Contains("--cap-add=DAC_OVERRIDE")&&!args.Contains("--privileged")&&!args.Any(a=>a.EndsWith(":z")||a.EndsWith(":Z")||a.EndsWith(":U")),"Storage permissions retain node identity without chowning or relabeling server files");
            var workload=new Workload("t3","T3 Code",T3CodeTests.Recipe(),[],"coding");
            var labels=new Dictionary<string,string>{{T3CodeRuntime.Label,T3CodeRuntime.Policy},{T3CodeGpuAccess.Label,"all-v1"},{T3Storage.Label,args[1].Split('=',2)[1]}};
            using var current=JsonDocument.Parse(JsonSerializer.Serialize(labels));
            using var legacy=JsonDocument.Parse("{}");
            check(settings.Fingerprint(workload,current.RootElement)==workload.Fingerprint&&settings.Fingerprint(workload,legacy.RootElement)!=workload.Fingerprint,"Storage labels adopt current containers and replace old mounts without changing recipes");
            var folder=root+"/projects with spaces";Directory.CreateDirectory(folder);
            settings.Save(new(false,false,[folder+"/",folder]));
            settings=new T3Storage(root,data);args=await settings.Arguments();
            check(settings.Read().Folders!.SequenceEqual([folder])&&args.Contains(folder+":/host"+folder+":rw,rbind,rslave")&&!args.Any(a=>a.StartsWith("/:/host")||a.Contains(":/models:")),"Selected folders survive restart, accept spaces, deduplicate, and replace all-drive/model mounts");
            check(settings.Fingerprint(workload,current.RootElement)!=workload.Fingerprint,"Changing storage invalidates the running container's storage policy");
            settings.Save(new(false,false));args=await settings.Arguments();
            check(!args.Contains("--volume")&&!args.Any(a=>a.StartsWith("--cap-add")),"Private storage mode removes host binds and file-permission capabilities");
            async Task<bool> Rejected(string path)
            {
                try{settings.Save(new(false,false,[path]));await settings.Arguments();return false;}catch(InvalidOperationException){return true;}
            }
            foreach(var path in new[]{"relative","/",root+"/../escape",root+"/missing",root+"/bad:rw",root+"/bad,path",root+"/bad\npath",null!})
                check(await Rejected(path),"Invalid or missing storage path is rejected before saving: "+path);
            Directory.CreateSymbolicLink(root+"/link",folder);
            check(await Rejected(root+"/link"),"Folder sharing rejects symlinks instead of silently changing the mount target");
            check(settings.Read() is {AllDrives:false,Models:false,Folders.Length:0},"Rejected settings leave the saved policy unchanged");
            settings.Save(new(false,true));
            var moved=root+"/data/library";Directory.CreateDirectory(moved+"/models");Directory.CreateDirectory(moved+"/model-sets");
            await File.WriteAllTextAsync(root+"/storage-locations.json",JsonSerializer.Serialize(new DataStorageConfig("fixture",null,[],new(){{"models",new(null,moved)}})));
            args=await settings.Arguments();
            check(args.Contains(moved+"/models:/models:rw,rbind,rslave")&&args.Contains(moved+"/model-sets:/model-sets:rw,rbind,rslave"),"Model mounts follow Xur storage relocation rather than stale default paths");
            Directory.Delete(moved+"/models");bool failed=false;
            try{await settings.Arguments();}catch(InvalidOperationException){failed=true;}
            check(failed&&!Directory.Exists(moved+"/models"),"Missing selected model storage fails without creating an empty fallback");
        }
        finally{Directory.Delete(root,true);}
    }
}
