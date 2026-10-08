using System.Text.Json;
using System.Text.RegularExpressions;
namespace Xur.Agent;

// Match the engine's default weight representation. Indexes describe one
// complete set; alternate serializations and training state are not its size.
internal sealed record CheckpointWeights(string Format,long Bytes,JsonElement[] Indexes,string[] Folders)
{
    public int MaxModelLength {get;init;}=4096;
    public string[] Arguments=>Format switch
    {
        "mistral"=>["--config-format","mistral","--tokenizer-mode","mistral","--load-format","mistral"],
        "safetensors"=>["--config-format","hf","--load-format","safetensors"],
        "pytorch"=>["--config-format","hf","--load-format","hf"],
        _=>[]
    };
    public static async Task<CheckpointWeights> Read(JsonElement metadata,Func<string,Task<string>> read)
    {
        var files=metadata.GetProperty("siblings").EnumerateArray().ToDictionary(f=>f.GetProperty("rfilename").GetString()!,StringComparer.Ordinal);
        var indexes=new List<JsonElement>();var selected=new HashSet<string>(StringComparer.Ordinal);var folders=new HashSet<string>(StringComparer.Ordinal);
        bool Native(string file)=>!file.Contains('/')&&file.StartsWith("consolidated",StringComparison.Ordinal)&&file.EndsWith(".safetensors",StringComparison.Ordinal);
        var mistral=files.ContainsKey("params.json")&&files.Keys.Any(Native);
        var diffusion=!mistral&&!files.ContainsKey("config.json")&&files.ContainsKey("model_index.json");
        var format=mistral?"mistral":diffusion?"diffusers":"";
        IEnumerable<string> roots;
        if(mistral)roots=[""];
        else if(diffusion)
        {
            using var pipeline=JsonDocument.Parse(await read("model_index.json"));var root=pipeline.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("_class_name",out var name)||name.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(name.GetString()))
                throw new InvalidOperationException("The Diffusers model_index.json does not identify a pipeline class.");
            roots=root.EnumerateObject().Where(p=>Regex.IsMatch(p.Name,@"^[A-Za-z0-9_-]+$")&&p.Value.ValueKind==JsonValueKind.Array&&p.Value.GetArrayLength()==2
                &&p.Value[0].ValueKind==JsonValueKind.String&&p.Value[1].ValueKind==JsonValueKind.String).Select(p=>p.Name+"/").ToArray();
        }
        else roots=files.Keys.Where(Weight).Select(file=>file.Contains('/')?file[..(file.LastIndexOf('/')+1)]:"").Distinct().ToArray();
        foreach(var folder in roots)
        {
            if(folder!=""&&!SafePath(folder.TrimEnd('/')))throw new InvalidOperationException("The checkpoint contains an invalid weight path.");
            var weights=files.Keys.Where(file=>file.StartsWith(folder,StringComparison.Ordinal)&&!file[folder.Length..].Contains('/')&&Weight(file)).ToArray();
            if(mistral)weights=weights.Where(Native).ToArray();
            else
            {
                // Diffusers defaults use unqualified filenames. A safetensors
                // precision variant must not hide the default PyTorch weights.
                if(diffusion)weights=weights.Where(file=>!Regex.IsMatch(file,@"\.(?:fp16|bf16|fp32)\.",RegexOptions.IgnoreCase)).ToArray();
                var safe=weights.Where(f=>f.EndsWith(".safetensors",StringComparison.Ordinal)).ToArray();
                weights=safe.Length>0?safe:weights.Where(f=>f.EndsWith(".bin",StringComparison.Ordinal)).ToArray();
            }
            if(weights.Length==0)continue;
            var safeFormat=weights[0].EndsWith(".safetensors",StringComparison.Ordinal);
            if(folder==""&&format=="")format=safeFormat?"safetensors":"pytorch";
            var indexNames=mistral?["consolidated.safetensors.index.json"]:safeFormat
                ?new[]{"model.safetensors.index.json","diffusion_pytorch_model.safetensors.index.json"}
                :new[]{"pytorch_model.bin.index.json","diffusion_pytorch_model.bin.index.json"};
            var indexName=indexNames.Select(name=>folder+name).FirstOrDefault(files.ContainsKey);
            if(indexName!=null)
            {
                using var index=JsonDocument.Parse(await read(indexName));var root=index.RootElement;
                if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("weight_map",out var map)||map.ValueKind!=JsonValueKind.Object||!map.EnumerateObject().Any())
                    throw new InvalidOperationException("The checkpoint shard index has no valid weight_map.");
                var references=new HashSet<string>(StringComparer.Ordinal);
                foreach(var entry in map.EnumerateObject())
                {
                    if(entry.Value.ValueKind!=JsonValueKind.String||entry.Value.GetString() is not {} file||!SafePath(file))
                        throw new InvalidOperationException("The checkpoint shard index contains an invalid weight path.");
                    var path=folder+file;
                    if(!weights.Contains(path,StringComparer.Ordinal))throw new InvalidOperationException("The checkpoint shard index references an unavailable weight file: "+file);
                    references.Add(path);
                }
                if(!safeFormat&&!diffusion&&weights.Any(file=>!references.Contains(file)))
                    throw new InvalidOperationException("The PyTorch checkpoint publishes additional inference weights outside its shard index.");
                weights=references.ToArray();indexes.Add(root.Clone());
            }
            foreach(var file in weights)selected.Add(file);
            if(weights.Length>0)folders.Add(folder);
        }
        long bytes=0;
        try
        {
            foreach(var file in selected)
            {
                if(!SafePath(file))throw new InvalidOperationException("The checkpoint contains an invalid weight path.");
                if(!files[file].TryGetProperty("size",out var size)||!size.TryGetInt64(out var value)||value<=0)
                    throw new InvalidOperationException("The checkpoint does not publish a valid size for "+file+".");
                bytes=checked(bytes+value);
            }
        }
        catch(OverflowException){throw new InvalidOperationException("The checkpoint weight size is invalid.");}
        if(bytes==0)throw new InvalidOperationException("The model does not publish a complete supported Safetensors or PyTorch weight set.");
        return new(format,bytes,indexes.ToArray(),folders.ToArray());
    }
    static bool Weight(string file)=>file.EndsWith(".safetensors",StringComparison.Ordinal)||file.EndsWith(".bin",StringComparison.Ordinal)
        &&Path.GetFileName(file) is not ("optimizer.bin" or "training_args.bin");
    static bool SafePath(string file)=>file.Split('/').All(part=>part is not ("" or "." or "..")&&Regex.IsMatch(part,@"^[A-Za-z0-9_.-]+$"));
}
