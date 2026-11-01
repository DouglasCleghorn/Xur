using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

public sealed partial class ModelLibrary
{
    static bool Within(string path,string root)=>path.StartsWith(root.TrimEnd('/')+"/",StringComparison.Ordinal);
    static bool Overlaps(string a,string b)=>a==b||Within(a,b)||Within(b,a);
    string ModelsRoot=>storage?.PathFor("models","models")??Path.Combine(directory,"models");
    string SetsRoot=>storage?.PathFor("models","model-sets")??Path.Combine(directory,"model-sets");
    string DeleteTarget(ModelFolder model)
    {
        var path=model.Path;
        if(!Path.IsPathFullyQualified(path)||Path.GetFullPath(path)!=path)
            throw new InvalidOperationException("Mount this storage first. Only models in Xur-managed storage can be deleted here.");
        string target=path;
        if(!Within(path,ModelsRoot)&&!Within(path,SetsRoot))
        {
            var relative=Path.GetRelativePath(volumes,path).Split('/');
            if(relative.Length<4||!relative[0].StartsWith("xur-cache-",StringComparison.Ordinal)||relative[1]!="_data"||!Within(path,volumes))
                throw new InvalidOperationException("Only models in Xur-managed storage can be deleted here.");
            // Remove the whole HF repository, including blobs and every revision.
            // Removing a snapshot alone leaves its weight blobs in the cache.
            var repository=Array.FindIndex(relative,2,p=>p.StartsWith("models--",StringComparison.Ordinal));
            if(repository>=0)target=Path.Combine(volumes,Path.Combine(relative[..(repository+1)]));
        }
        // Recheck every ancestor, including a moved storage root. Never follow
        // a caller-controlled directory link into unrelated host files.
        for(FileSystemInfo? entry=Directory.Exists(target)?new DirectoryInfo(target):new FileInfo(target);entry!=null;
            entry=entry is DirectoryInfo folder?folder.Parent:((FileInfo)entry).Directory)
            if(entry.LinkTarget!=null)throw new InvalidOperationException("This model location contains a symbolic link. Search storage again or manage the original location directly.");
        if(!File.Exists(target)&&!Directory.Exists(target))throw new InvalidOperationException("This model is no longer available. Search storage again.");
        return target;
    }
    ModelFolder DeletionInfo(ModelFolder model)
    {
        try{return model with{DeletePath=DeleteTarget(model),DeleteUnavailableReason=null};}
        catch(Exception error) when(error is InvalidOperationException or IOException or UnauthorizedAccessException)
        {return model with{DeletePath=null,DeleteUnavailableReason=error.Message};}
    }
    void RequireUnused(string target,Workload[] workloads)
    {
        // T3 Code's shared storage access does not make it an owner of every
        // model. Protect actual recipe assets and workload-specific caches.
        foreach(var w in workloads)
        {
            var assets=(w.Recipe.Model is {} asset?new[]{asset}:[]).Concat(w.Recipe.Files?.Select(f=>f.Asset)??[]);
            var used=assets.Any(a=>Overlaps(target,Path.Combine(ModelsRoot,a.Sha256+".gguf")))||
                w.Recipe.Files!=null&&Overlaps(target,Path.Combine(SetsRoot,Canonical.Hash(w.Recipe.Files)))||
                Overlaps(target,Path.Combine(volumes,"xur-cache-"+w.Id,"_data"))||
                w.Recipe.Engine=="audio.cpp"&&Overlaps(target,Path.Combine(ModelsRoot,"audio.cpp",w.Id))||
                UnslothWorkload.Is(w.Recipe);
            if(used)throw new InvalidOperationException("Stop or disable '"+w.Name+"' before deleting this model. Unload its profile if it is profile-managed.");
        }
    }
    public async Task Delete(ModelDeleteRequest request,Workload[] workloads)
    {
        lock(gate)
        {
            if(snapshot.Scanning||deleting)throw new InvalidOperationException("Wait for the current model scan or deletion to finish.");
            deleting=true;
        }
        try
        {
            if(!request.Confirm)throw new InvalidOperationException("Confirm permanent model deletion first.");
            if(storage!=null)await storage.Require("models");
            ModelFolder model;lock(gate)model=snapshot.Models.SingleOrDefault(m=>m.Path==request.Path)
                ??throw new InvalidOperationException("This model is no longer listed. Search storage again.");
            var target=DeleteTarget(model);
            if(target!=request.DeletePath)throw new InvalidOperationException("The model location changed. Refresh the page and confirm its new location.");
            RequireUnused(target,workloads);
            if(Directory.Exists(target))Directory.Delete(target,true);else File.Delete(target);
            lock(gate)
            {
                // One HF repository may appear as several snapshots or files.
                snapshot=snapshot with{CapturedAt=DateTimeOffset.UtcNow,Models=snapshot.Models.Where(m=>m.Path!=target&&!Within(m.Path,target)).ToArray()};
                Directory.CreateDirectory(directory);
                using(var file=new FileStream(Path.Combine(directory,"model-library.json.tmp"),FileMode.Create,FileAccess.Write))
                {JsonSerializer.Serialize(file,snapshot);file.Flush(true);}
                File.Move(Path.Combine(directory,"model-library.json.tmp"),Path.Combine(directory,"model-library.json"),true);
            }
        }
        finally{lock(gate)deleting=false;}
    }
}
