using System.Text.Json;
using System.Text.RegularExpressions;
using Xur.Domain;
namespace Xur.Agent;

public record ModelChoice(string Id,string Name,string Engine);
public record ModelVariant(string Id,string Name,long Bytes);
public record ModelOptions(string Model,string Revision,string License,string Engine,ModelVariant[] Variants);
public record ModelSelection(string Model,string Revision,string Variant,string Engine="llama.cpp",string Device="Auto");

// Discovery may change upstream. A saved choice is a separate immutable recipe;
// catalog refresh never edits a profile or changes a running instance.
public sealed class ModelCatalog(string state)
{
    static readonly HttpClient http=new(){Timeout=TimeSpan.FromSeconds(30)};
    static ModelCatalog(){http.DefaultRequestHeaders.UserAgent.ParseAdd("Xur/1.0");}
    static readonly JsonSerializerOptions json=new(JsonSerializerDefaults.Web);
    readonly SemaphoreSlim gate=new(1,1);
    const string Cpu="ghcr.io/ggml-org/llama.cpp@sha256:e33f80e54fc3f403118ab92b24f21dc1a3125ffd0c7725532028eca8128b548d";
    static string Image(string vendor)=>vendor switch {
        "NVIDIA"=>"ghcr.io/ggml-org/llama.cpp@sha256:6b4e57594fbb8fb1111bfe64a470570586bb4649a1d38647680a4ec3ed159695",
        "AMD"=>"ghcr.io/ggml-org/llama.cpp@sha256:4b1e4cee27366a6250b88960a8dd2ad6a111ced7a1b9a234ff0f53299bedca8f",
        "Intel"=>"ghcr.io/ggml-org/llama.cpp@sha256:09800bdcf619dbea87cd22194c3210ebbb80c943e5d93fb27e4e5bb6f6e7b1ce",_=>Cpu};
    const string Vllm="mirror.gcr.io/vllm/vllm-openai@sha256:082ca6f035279109041ffd3fe0695cb568b29bc580b35c4f297a66a08b216c1b";
    const string Omni="mirror.gcr.io/vllm/vllm-omni@sha256:4780186f168af96634917208596675439fa71cb5ed3440a30e8c21ca1defc451";
    const string OmniModels="https://raw.githubusercontent.com/vllm-project/vllm-omni/eb11446b7f2e30ca582f8aff3afe12e9a2e66f6c/docs/models/supported_models.md";
    static void Validate(string model,string? revision=null)
    {
        if(!Regex.IsMatch(model,@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$") || revision!=null && !Regex.IsMatch(revision,@"^[0-9a-f]{40}$"))throw new InvalidOperationException("Invalid model identity.");
    }
    static void Engine(string engine) {if(engine is not ("llama.cpp" or "vLLM" or "vLLM-Omni"))throw new InvalidOperationException("Unknown model engine.");}
    async Task<string> Cached(string url,TimeSpan age)
    {
        var folder=Path.Combine(state,"catalog-cache");Directory.CreateDirectory(folder);var path=Path.Combine(folder,Canonical.Hash(url+"\n"+new HuggingFaceCredentials(state).Read())+".cache");
        if(File.Exists(path) && DateTime.UtcNow-File.GetLastWriteTimeUtc(path)<age)return await File.ReadAllTextAsync(path);
        try
        {
            using var request=new HuggingFaceCredentials(state).Request(url);using var response=await http.SendAsync(request);response.EnsureSuccessStatusCode();var text=await response.Content.ReadAsStringAsync();
            if(text.Length>8*1024*1024)throw new InvalidOperationException("The catalog response is too large.");
            var temp=path+"."+Guid.NewGuid().ToString("N");await File.WriteAllTextAsync(temp,text);File.Move(temp,path,true);return text;
        }
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException)
        {if(File.Exists(path))return await File.ReadAllTextAsync(path);throw new InvalidOperationException("The model catalog could not be reached. Check the network and try again.");}
    }
    async Task<JsonDocument> Metadata(string model,string? revision=null)
    {Validate(model,revision);return JsonDocument.Parse(await Cached("https://huggingface.co/api/models/"+model+(revision==null?"":"/revision/"+revision)+"?blobs=true",revision==null?TimeSpan.FromMinutes(10):TimeSpan.FromDays(365)));}
    public async Task<ModelChoice[]> Search(string? query,string engine)
    {
        Engine(engine);query=NormalizeQuery(query);if(query.Length>120)throw new InvalidOperationException("Search is too long.");
        if(engine=="vLLM-Omni")
        {
            var doc=await Cached(OmniModels,TimeSpan.FromDays(1));
            return Regex.Matches(doc,@"`([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)`").Select(m=>m.Groups[1].Value).Distinct().Where(id=>id.Contains(query,StringComparison.OrdinalIgnoreCase)).Take(100).Select(id=>new ModelChoice(id,id,engine)).ToArray();
        }
        // Exact repositories bypass the hub's search tags, which differ between
        // publishers and omit valid multimodal/vLLM checkpoints.
        if(engine=="vLLM" && Regex.IsMatch(query,@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
        {
            using var exact=await Metadata(query);var root=exact.RootElement;
            return WeightBytes(root)>0?[new(root.GetProperty("id").GetString()!,root.GetProperty("id").GetString()!,engine)]:[];
        }
        var url="https://huggingface.co/api/models?sort=downloads&direction=-1&limit=100&search="+Uri.EscapeDataString(query)+(engine=="llama.cpp"?"&author=unsloth&filter=gguf":"&filter=safetensors");
        using var data=JsonDocument.Parse(await Cached(url,TimeSpan.FromMinutes(10)));
        return data.RootElement.EnumerateArray().Where(m=>!m.TryGetProperty("private",out var p)||!p.GetBoolean()).Select(m=>m.GetProperty("id").GetString()!).Select(id=>new ModelChoice(id,id,engine)).ToArray();
    }
    public static string NormalizeQuery(string? query)
    {
        query=(query??"").Trim();
        if(Uri.TryCreate(query,UriKind.Absolute,out var url) && url.Scheme=="https" && url.Host=="huggingface.co" && url.UserInfo.Length==0)
        {var parts=url.AbsolutePath.Trim('/').Split('/');if(parts.Length>=2)return string.Join('/',parts.Take(2));}
        return query;
    }
    static string License(JsonElement data)=>data.TryGetProperty("cardData",out var card) && card.ValueKind==JsonValueKind.Object && card.TryGetProperty("license",out var l)?l.ToString():"See upstream model license";
    static JsonElement[] Ggufs(JsonElement data)=>data.GetProperty("siblings").EnumerateArray().Where(f=>f.GetProperty("rfilename").GetString()!.EndsWith(".gguf",StringComparison.OrdinalIgnoreCase) && f.TryGetProperty("lfs",out _)).ToArray();
    static string FileName(JsonElement file)=>file.GetProperty("rfilename").GetString()!;
    static JsonElement[] Group(JsonElement[] files,string first)
    {
        var match=Regex.Match(first,@"^(.*)-00001-of-([0-9]{5})\.gguf$");
        if(!match.Success)return files.Where(f=>FileName(f)==first).ToArray();
        int count=int.Parse(match.Groups[2].Value);if(count>256)throw new InvalidOperationException("Too many model shards.");
        var result=Enumerable.Range(1,count).Select(n=>files.SingleOrDefault(f=>FileName(f)==$"{match.Groups[1].Value}-{n:00000}-of-{count:00000}.gguf")).ToArray();
        if(result.Any(f=>f.ValueKind==JsonValueKind.Undefined))throw new InvalidOperationException("The upstream GGUF shard set is incomplete.");return result;
    }
    public async Task<ModelOptions> Options(string model,string engine)
    {
        Engine(engine);using var data=await Metadata(model);var root=data.RootElement;
        if(root.TryGetProperty("gated",out var g) && g.ValueKind is not (JsonValueKind.False or JsonValueKind.Null) && !new HuggingFaceCredentials(state).Configured)throw new InvalidOperationException("This model requires upstream access approval. Add an authorized Hugging Face token in Settings first.");
        var revision=root.GetProperty("sha").GetString()!;var license=License(root);
        if(engine!="llama.cpp")return new(model,revision,license,engine,[new("upstream","Upstream checkpoint",WeightBytes(root))]);
        var files=Ggufs(root);
        var variants=files.Where(f=>!Path.GetFileName(FileName(f)).StartsWith("mmproj",StringComparison.OrdinalIgnoreCase) && (!Regex.IsMatch(FileName(f),@"-[0-9]{5}-of-[0-9]{5}\.gguf$") || FileName(f).Contains("-00001-of-"))).Select(f=>new ModelVariant(FileName(f),VariantName(FileName(f)),Group(files,FileName(f)).Sum(s=>s.GetProperty("size").GetInt64()))).OrderBy(v=>v.Id.Contains("Q4_K_M")?0:v.Id.Contains("Q4_K")?1:2).ThenBy(v=>v.Bytes).ToArray();
        if(variants.Length==0)throw new InvalidOperationException("No complete GGUF files were published for this model.");return new(model,revision,license,engine,variants);
    }
    static long WeightBytes(JsonElement data)=>data.GetProperty("siblings").EnumerateArray().Where(f=>FileName(f).EndsWith(".safetensors") && f.TryGetProperty("size",out _)).Sum(f=>f.GetProperty("size").GetInt64());
    static string VariantName(string name){var m=Regex.Match(Path.GetFileName(name),@"(?:IQ|Q|BF|F)[0-9][A-Z0-9_]*");return m.Success?m.Value:Path.GetFileName(name);}
    async Task<(Dictionary<string,double> Values,string Source)> Sampling(string model)
    {
        using var commit=JsonDocument.Parse(await Cached("https://api.github.com/repos/unslothai/unsloth/commits/main",TimeSpan.FromDays(1)));
        var revision=commit.RootElement.GetProperty("sha").GetString()!;
        if(!Regex.IsMatch(revision,@"^[a-f0-9]{40}$"))throw new InvalidOperationException("Invalid upstream settings revision.");
        var prefix="https://raw.githubusercontent.com/unslothai/unsloth/"+revision+"/studio/backend/assets/configs/";
        var values=new Dictionary<string,double>();
        void ReadYaml(string yaml)
        {
            var document=new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<Dictionary<string,object>>(yaml);
            if(document.TryGetValue("inference",out var inference) && inference is Dictionary<object,object> fields)
                foreach(var field in fields)if(field.Key is string key && double.TryParse(field.Value?.ToString(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var v))values[key]=v;
        }
        ReadYaml(await Cached(prefix+"model_defaults/default.yaml",TimeSpan.FromDays(365)));
        using var defaults=JsonDocument.Parse(await Cached(prefix+"inference_defaults.json",TimeSpan.FromDays(365)));
        var families=defaults.RootElement.GetProperty("families");
        foreach(var pattern in defaults.RootElement.GetProperty("patterns").EnumerateArray())
        {
            var family=pattern.GetString()!;if(!model.Contains(family,StringComparison.OrdinalIgnoreCase) || !families.TryGetProperty(family,out var settings))continue;
            foreach(var v in settings.EnumerateObject())if(v.Value.ValueKind==JsonValueKind.Number && v.Value.TryGetDouble(out var number))values[v.Name]=number;break;
        }
        // A model-specific numeric inference block overrides the family defaults.
        using var tree=JsonDocument.Parse(await Cached("https://api.github.com/repos/unslothai/unsloth/git/trees/"+revision+"?recursive=1",TimeSpan.FromDays(365)));
        var key=model.Replace('/','_').Replace("-GGUF","",StringComparison.OrdinalIgnoreCase)+".yaml";
        var path=tree.RootElement.GetProperty("tree").EnumerateArray().Select(e=>e.GetProperty("path").GetString()!).FirstOrDefault(p=>p.StartsWith("studio/backend/assets/configs/model_defaults/") && Path.GetFileName(p).Equals(key,StringComparison.OrdinalIgnoreCase));
        if(path!=null)ReadYaml(await Cached("https://raw.githubusercontent.com/unslothai/unsloth/"+revision+"/"+path,TimeSpan.FromDays(365)));
        return(values,"https://github.com/unslothai/unsloth/tree/"+revision+"/studio/backend/assets/configs");
    }
    public async Task ValidateEngineCheckpoint(Recipe recipe)
    {
        if(recipe.Hub is {} hub && recipe.Engine is "vLLM" or "vLLM-Omni")await ValidateCheckpoint(hub.Repository,hub.Revision,recipe.Engine);
    }
    async Task ValidateCheckpoint(string model,string revision,string engine)
    {
        using var config=JsonDocument.Parse(await Cached("https://huggingface.co/"+model+"/raw/"+revision+"/config.json",TimeSpan.FromDays(365)));
        EngineStartup.ValidateCheckpoint(engine,model,config.RootElement);
    }
    public async Task<Recipe> Resolve(ModelSelection selection)
    {
        Engine(selection.Engine);Validate(selection.Model,selection.Revision);
        if(selection.Engine=="vLLM-Omni" && !(await Search(selection.Model,selection.Engine)).Any(m=>m.Id==selection.Model))throw new InvalidOperationException("Select a model from the upstream vLLM-Omni supported-model list.");
        await gate.WaitAsync();try
        {
            using var metadata=await Metadata(selection.Model,selection.Revision);var data=metadata.RootElement;var license=License(data);
            ModelFile[]? files=null;long bytes;string variant=selection.Variant;
            if(selection.Engine=="llama.cpp")
            {
                var group=Group(Ggufs(data),variant);if(group.Length==0)throw new InvalidOperationException("Select a published GGUF file.");
                files=group.Select(f=>new ModelFile(Path.GetFileName(FileName(f)),new("https://huggingface.co/"+selection.Model+"/resolve/"+selection.Revision+"/"+string.Join('/',FileName(f).Split('/').Select(Uri.EscapeDataString)),f.GetProperty("lfs").GetProperty("sha256").GetString()!,f.GetProperty("size").GetInt64(),license,selection.Model,selection.Revision))).ToArray();bytes=files.Sum(f=>f.Asset.Bytes);
            }
            else {await ValidateCheckpoint(selection.Model,selection.Revision,selection.Engine);if(variant!="upstream")throw new InvalidOperationException("Select the upstream checkpoint.");bytes=WeightBytes(data);if(bytes==0)throw new InvalidOperationException("The model does not publish safetensors weights.");}
            var hardware=await GpuInventory.Observe();var vendor=selection.Device;
            if(vendor=="Auto")vendor=hardware.FirstOrDefault(g=>g.Problems.Length==0 && g.MemoryMiB>0)?.Vendor??"CPU";
            if(selection.Engine!="llama.cpp" && vendor!="NVIDIA")throw new InvalidOperationException("This vLLM engine image requires an available NVIDIA GPU.");
            if(vendor is not ("CPU" or "NVIDIA" or "AMD" or "Intel"))throw new InvalidOperationException("Select a supported execution device.");
            var available=hardware.Where(g=>g.Vendor==vendor && g.Problems.Length==0).ToArray();
            // An explicit capacity estimate, not a claim of measured peak memory.
            long totalMiB=(bytes+1048575)/1048576+2048;int count=0;long minimum=0;
            if(vendor!="CPU")
            {
                if(available.Length==0)throw new InvalidOperationException("No compatible GPU is available.");
                long perGpu=available.Min(g=>g.MemoryMiB)*85/100;
                count=(int)Math.Max(1,(totalMiB+perGpu-1)/perGpu);if(count>available.Length)throw new InvalidOperationException("This checkpoint exceeds the observed GPU capacity. Choose a smaller model or quantization.");
                minimum=(totalMiB+count-1)/count;
            }
            var args=new List<string>();string image;HubModel? hub=null;string? settingsSource=null;
            if(selection.Engine=="llama.cpp")
            {
                image=Image(vendor);args.AddRange(["-m","/models/"+files![0].Name,"--host","0.0.0.0","--port","8080","--ctx-size","4096","--parallel","1","--alias","model"]);
                if(count>0)args.AddRange(["--n-gpu-layers","999"]);
                var sampling=await Sampling(selection.Model);settingsSource=sampling.Source;
                // Import numeric defaults as data. Never execute upstream scripts.
                foreach(var (key,flag) in new[]{("temperature","--temp"),("top_p","--top-p"),("top_k","--top-k"),("min_p","--min-p"),("repetition_penalty","--repeat-penalty"),("presence_penalty","--presence-penalty")})
                    if(sampling.Values.TryGetValue(key,out var n) && double.IsFinite(n) && n>=-1 && n<=1000)args.AddRange([flag,(key=="top_k"?Math.Max(0,n):n).ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            }
            else
            {
                image=selection.Engine=="vLLM"?Vllm:Omni;hub=new(selection.Model,selection.Revision,license);
                args.AddRange(["serve",selection.Model,"--revision",selection.Revision,"--host","0.0.0.0","--port","8080"]);
                if(selection.Engine=="vLLM")args.AddRange(ModelLaunchSettings.Vllm(selection.Model,count));
                else args.Add("--omni");
            }
            var name=selection.Model.Split('/')[1]+(selection.Engine=="llama.cpp"?" · "+Regex.Match(Path.GetFileName(variant),@"(?:IQ|Q|BF|F)[0-9][A-Z0-9_]*").Value:"");if(name.Length>80)name=name[..80];
            var recipe=new Recipe("model-"+Canonical.Hash(new{selection.Model,selection.Revision,variant,vendor,count,args,image,settingsSource})[..32],name,image,args.ToArray(),8080,"/health",vendor,count,minimum,license+" · "+selection.Model,null,"Model",selection.Engine,files,hub,settingsSource);
            ProfilePolicy.ValidateRecipe(recipe);var selected=Path.Combine(state,"catalog-selected");Directory.CreateDirectory(selected);var path=Path.Combine(selected,recipe.Id+".json");
            if(!File.Exists(path)){var tmp=path+".tmp";await File.WriteAllTextAsync(tmp,JsonSerializer.Serialize(recipe,json));File.Move(tmp,path);}
            return recipe;
        }finally{gate.Release();}
    }
}
