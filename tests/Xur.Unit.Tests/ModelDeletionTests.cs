using System.Text.Json;
using Xur.Agent;
using Xur.Domain;

static class ModelDeletionTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(".build/evidence/model-deletion-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var state=Path.Combine(root,"state");var models=Path.Combine(state,"models");var volumes=Path.Combine(root,"volumes");
            var hash=new string('a',64);var gguf=Path.Combine(models,hash+".gguf");
            void Write(string path,string text="GGUFfixture"){Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,text);}
            Write(gguf);var other=Path.Combine(models,"other.gguf");Write(other);
            var privateFile=Path.Combine(state,"private.gguf");Write(privateFile);
            var traversal=Path.Combine(models,"..","private.gguf");
            var outside=Path.Combine(root,"external","external.gguf");Write(outside);
            var link=Path.Combine(models,"linked.gguf");File.CreateSymbolicLink(link,outside);
            var linkedDirectory=Path.Combine(models,"linked-directory");Directory.CreateSymbolicLink(linkedDirectory,Path.GetDirectoryName(outside)!);
            var repository=Path.Combine(volumes,"xur-cache-test","_data","huggingface","hub","models--owner--fixture");
            var blob=Path.Combine(repository,"blobs","weights");Write(blob,"weights");
            var first=Path.Combine(repository,"snapshots","revision-1");var second=Path.Combine(repository,"snapshots","revision-2");
            Directory.CreateDirectory(first);Directory.CreateDirectory(second);
            File.CreateSymbolicLink(Path.Combine(first,"model.safetensors"),"../../blobs/weights");
            File.CreateSymbolicLink(Path.Combine(second,"model.safetensors"),"../../blobs/weights");
            // Recursive deletion must unlink a child link, never its outside target.
            Directory.CreateSymbolicLink(Path.Combine(repository,"outside-link"),Path.GetDirectoryName(outside)!);
            var prior=new ModelScanStatus(null,false,[new("External",outside,"GGUF",11,1,"Attached storage"),new("Traversal",traversal,"GGUF",11,1,"Xur model storage")],[],[]);
            File.WriteAllText(Path.Combine(state,"model-library.json"),JsonSerializer.Serialize(prior));
            var library=new ModelLibrary(state,volumes:volumes);library.Load();
            var deadline=DateTime.UtcNow.AddSeconds(10);
            while(library.Status().Scanning&&DateTime.UtcNow<deadline)await Task.Delay(10);
            check(!library.Status().Scanning,"Model inventory finishes before deletion tests");
            ModelFolder Entry(string path)=>library.Status().Models.Single(m=>m.Path==path);
            ModelDeleteRequest Request(string path)=>new(path,Entry(path).DeletePath??path,true);
            async Task Reject(ModelDeleteRequest request,Workload[]? workloads=null)
            {
                var rejected=false;try{await library.Delete(request,workloads??[]);}catch(InvalidOperationException){rejected=true;}
                check(rejected,"Model deletion rejects unsafe, unconfirmed, stale or in-use request: "+request.Path);
            }
            check(Entry(gguf).DeletePath==gguf,"Managed GGUF inventory exposes its deletion path");
            check(Entry(first).DeletePath==repository&&Entry(second).DeletePath==repository,"All HF snapshots identify the entire repository including blobs for deletion");
            check(Entry(outside).DeletePath==null&&Entry(traversal).DeletePath==null&&Entry(link).DeletePath==null,"External files, traversal paths and symbolic-link models cannot be deleted");
            await Reject(Request(outside));await Reject(Request(traversal));await Reject(Request(link));
            await Reject(new(Path.Combine(linkedDirectory,"external.gguf"),linkedDirectory,true));
            await Reject(Request(gguf) with{Confirm=false});await Reject(Request(gguf) with{DeletePath=models});
            await Reject(new(privateFile,privateFile,true));
            var recipe=new Recipe("fixture","Fixture","mirror.gcr.io/library/alpine:latest",[],0,"","CPU",0,0,"Fixture",new("https://example.test/fixture",hash,11,"MIT","fixture","revision"));
            var workload=new Workload("test","Using model",recipe,[],"fixture");
            await Reject(Request(gguf),[workload]);await Reject(Request(first),[workload]);
            await Reject(Request(gguf),[workload with{Recipe=UnslothWorkload.Recipe}]);
            await Reject(Request(gguf),[workload with{Recipe=T3CodeTests.Recipe()}]);
            // Recipes may mount a model set made from the same cached assets.
            ModelFile[] files=[new("fixture.gguf",recipe.Model!)];
            var set=Path.Combine(state,"model-sets",Canonical.Hash(files));
            var setFile=Path.Combine(set,"fixture.gguf");Directory.CreateDirectory(set);
            var hardlink=await Processes.Run("ln",[gguf,setFile],5);
            check(hardlink.ExitCode==0,"Model-set fixture shares the cached weights through a hard link");
            library.Start(false);
            deadline=DateTime.UtcNow.AddSeconds(10);while(library.Status().Scanning&&DateTime.UtcNow<deadline)await Task.Delay(10);
            await Reject(Request(setFile),[workload with{Recipe=recipe with{Model=null,Files=files}}]);
            var runtimeDirectory=Path.Combine(state,"runtime");Directory.CreateDirectory(runtimeDirectory);
            File.WriteAllText(Path.Combine(runtimeDirectory,"test.json"),JsonSerializer.Serialize(workload));
            var runtime=new WorkloadRuntime(runtimeDirectory,new RecipeCatalog("catalog"),dataStorage:new DataStorage(state));
            var rejected=false;try{await runtime.DeleteModel(library,Request(gguf));}catch(InvalidOperationException){rejected=true;}
            check(rejected&&File.Exists(gguf),"Runtime deletion checks saved workload ownership before touching weights");
            File.Delete(Path.Combine(runtimeDirectory,"test.json"));
            await runtime.DeleteModel(library,Request(gguf));
            check(!File.Exists(gguf)&&File.Exists(other)&&File.ReadAllText(setFile)=="GGUFfixture"&&library.Status().Models.All(m=>m.Path!=gguf),"Deleting GGUF removes only the selected file, preserves hard-linked copies and updates inventory immediately");
            await library.Delete(Request(setFile),[]);
            check(!File.Exists(setFile),"A remaining model-set location can be deleted independently");
            await Reject(new(gguf,gguf,true));
            await library.Delete(Request(first),[]);
            check(!Directory.Exists(repository)&&File.Exists(outside)&&library.Status().Models.All(m=>!m.Path.StartsWith(repository)),"HF deletion frees blobs and all revisions without following nested directory links");
            var saved=JsonSerializer.Deserialize<ModelScanStatus>(File.ReadAllText(Path.Combine(state,"model-library.json")))!;
            check(saved.Models.All(m=>m.Path!=gguf&&!m.Path.StartsWith(repository)),"Deleted model locations are removed from the persisted inventory");
            // Replace a previously inventoried folder after scanning.
            var moved=Path.Combine(root,"retained-models");Directory.Move(models,moved);Directory.CreateSymbolicLink(models,Path.GetDirectoryName(outside)!);
            await Reject(new(other,other,true));
            check(File.Exists(outside)&&File.Exists(privateFile),"Rejected deletions preserve private and external files");
        }
        finally{Directory.Delete(root,true);}
    }
}
