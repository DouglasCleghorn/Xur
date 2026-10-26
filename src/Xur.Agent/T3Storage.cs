using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

// Storage policy belongs to the persistent service, not its prepared recipe.
// /host preserves host paths; model aliases follow Xur's selected data location.
public sealed class T3Storage(string state,DataStorage storage)
{
    public const string Label="io.xur.t3-storage";
    readonly object sync=new();
    static readonly JsonSerializerOptions json=new(JsonSerializerDefaults.Web);
    string FilePath=>Path.Combine(state,"t3-storage.json");
    public T3StorageConfig Read()
    {
        lock(sync)return Normalize(File.Exists(FilePath)
            ?JsonSerializer.Deserialize<T3StorageConfig>(File.ReadAllText(FilePath),json)??throw new IOException("Invalid T3 storage settings.")
            :new());
    }
    public static T3StorageConfig Normalize(T3StorageConfig config)
    {
        var folders=config.Folders??[];
        if(folders.Length>64)throw new InvalidOperationException("Choose at most 64 folders.");
        foreach(var path in folders)
            if(string.IsNullOrWhiteSpace(path)||!path.StartsWith('/')||path.Length>4096||path.Any(char.IsControl)||path.Contains(':')||path.Contains(',')||path.Contains('\\')||
                path.Split('/').Any(p=>p is "." or ".."))
                throw new InvalidOperationException("Use absolute folder paths without '.', '..', commas or colons.");
        folders=folders.Select(p=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if(folders.Contains("/"))throw new InvalidOperationException("Use All drives and folders to share the server root.");
        return config with{Folders=folders};
    }
    static void RequireFolder(string path)
    {
        if(!Directory.Exists(path))throw new InvalidOperationException("T3 storage folder is unavailable: "+path);
        for(var folder=new DirectoryInfo(path);folder!=null;folder=folder.Parent)
            if(folder.LinkTarget!=null)throw new InvalidOperationException("Select the real folder instead of a symbolic link: "+path);
    }
    public T3StorageConfig Save(T3StorageConfig config)
    {
        config=Normalize(config);
        if(!config.AllDrives)foreach(var folder in config.Folders!)RequireFolder(folder);
        lock(sync)
        {
            Directory.CreateDirectory(state);
            using(var stream=new FileStream(FilePath+".tmp",new FileStreamOptions{Mode=FileMode.Create,Access=FileAccess.Write,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite}))
            {JsonSerializer.Serialize(stream,config,json);stream.Flush(true);}
            File.Move(FilePath+".tmp",FilePath,true);
        }
        return config;
    }
    string Policy(T3StorageConfig config)=>Canonical.Hash(new{Version=1,Config=config,
        Models=config.Models?new[]{storage.PathFor("models","models"),storage.PathFor("models","model-sets")}:[]});
    public string Fingerprint(Workload workload,JsonElement labels)
    {
        var expected=Policy(Read());
        return labels.TryGetProperty(Label,out var label)&&label.GetString()==expected
            ?T3CodeRuntime.Fingerprint(workload,labels):Canonical.Hash(new{Workload=workload.Fingerprint,Storage=expected,Stale=true});
    }
    public async Task<string[]> Arguments()
    {
        var config=Read();var args=new List<string>{"--label",Label+"="+Policy(config)};
        if(config.AllDrives)args.AddRange(["--volume","/:/host:rw,rbind,rslave"]);
        else foreach(var folder in config.Folders!)
        {
            RequireFolder(folder);
            args.AddRange(["--volume",folder+":/host"+folder+":rw,rbind,rslave"]);
        }
        if(config.Models)
        {
            await storage.Require("models");
            foreach(var part in new[]{"models","model-sets"})
            {
                var path=storage.PathFor("models",part);Directory.CreateDirectory(path);RequireFolder(path);
                args.AddRange(["--volume",path+":/"+part+":rw,rbind,rslave"]);
            }
        }
        if(config.AllDrives||config.Models||config.Folders!.Length>0)
        {
            // Retain node's identity and home ownership. Ambient DAC_OVERRIDE
            // lets it read/write shared files without chowning host directories.
            // Never relabel the host root or make the container privileged.
            args.AddRange(["--user","1000:1000","--cap-add=DAC_OVERRIDE","--security-opt=label=disable"]);
        }
        return args.ToArray();
    }
}
