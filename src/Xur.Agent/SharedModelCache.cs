using Xur.Domain;
namespace Xur.Agent;

// Hugging Face's own cache format is shared across Studio and inference engines.
// Credentials remain outside this shared model cache.
public static class SharedModelCache
{
    public const string Label="io.xur.model-storage";
    public static string Policy(Workload workload,DataStorage storage)=>Canonical.Hash(new{Version=1,
        Models=storage.PathFor("models","models"),Sets=storage.PathFor("models","model-sets"),
        Studio=UnslothWorkload.Is(workload.Recipe)?new[]{storage.PathFor("unsloth","workspace"),storage.PathFor("unsloth","studio")}:[]});
    public static bool Applies(Workload workload)=>UnslothWorkload.Is(workload.Recipe)||workload.Recipe.Kind=="Model";
    public static async Task<string?> ImportAsset(DataStorage storage,ModelAsset asset)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(asset.Sha256,@"\A[a-f0-9]{64}\z"))return null;
        if(!Uri.TryCreate(asset.Url,UriKind.Absolute,out var url)||url.Host!="huggingface.co")return null;
        var parts=url.AbsolutePath.Split('/',StringSplitOptions.RemoveEmptyEntries);
        if(parts.Length<5||parts[2]!="resolve"||!System.Text.RegularExpressions.Regex.IsMatch(parts[0]+"/"+parts[1],@"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z"))return null;
        var root=storage.HuggingFacePath;
        var blob=Path.Combine(root,"hub","models--"+parts[0]+"--"+parts[1],"blobs",asset.Sha256);
        if(!File.Exists(blob))return null;
        // Reuse only ordinary files inside the selected cache. Never trust a path
        // from downloaded metadata without verifying the requested content hash.
        for(FileSystemInfo? entry=new FileInfo(blob);entry!=null;entry=entry is FileInfo file?file.Directory:((DirectoryInfo)entry).Parent)
        {
            if(entry.LinkTarget!=null)return null;
            if(entry.FullName==root)break;
        }
        if(new FileInfo(blob).Length!=asset.Bytes)return null;
        // Publish an independent inode. Studio may replace or modify its writable
        // cache while a different runtime is serving the imported model.
        var models=storage.PathFor("models","models");
        DataStorage.CheckCapacity(models,asset.Bytes);
        var target=Path.Combine(models,asset.Sha256+".gguf");
        var temporary=target+"."+Guid.NewGuid().ToString("N")+".partial";
        try
        {
            var copy=await Processes.Run("cp",["--reflink=auto","--no-dereference","--",blob,temporary],86400);
            if(copy.ExitCode!=0||new FileInfo(temporary).LinkTarget!=null)return null;
            await using(var stream=File.OpenRead(temporary))
                if(stream.Length!=asset.Bytes||Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream))!=asset.Sha256)return null;
            File.Move(temporary,target,true);
            return target;
        }
        finally{File.Delete(temporary);}
    }
    public static string[] Arguments(DataStorage storage,string destination="/root/.cache/huggingface")
    {
        Directory.CreateDirectory(storage.HuggingFacePath);
        return ["--volume",storage.HuggingFacePath+":"+destination+(storage.Config().Locations.GetValueOrDefault("models")?.ConnectionId!=null?":rw":":rw,z"),"--env","HF_HOME="+destination,"--env","HF_HUB_CACHE="+destination+"/hub","--env","HF_TOKEN_PATH=/root/.cache/xur-huggingface/token"];
    }
}
