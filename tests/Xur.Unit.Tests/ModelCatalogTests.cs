using System.Net;
using System.Text.Json;
using Xur.Agent;
using Xur.Domain;

static class ModelCatalogTests
{
    const string Model="Qwen/Qwen-Image-2.1";
    const string Revision="d26bb61231c349cf6b7896fa83353113880e1ba3";
    const string Hub="https://huggingface.co/";
    const string Supported="https://raw.githubusercontent.com/vllm-project/vllm-omni/main/docs/models/supported_models.md";
    static string MetadataUrl(bool pinned)=>Hub+"api/models/"+Model+(pinned?"/revision/"+Revision:"")+"?blobs=true";
    static string FileUrl(string file)=>Hub+Model+"/raw/"+Revision+"/"+file;
    static string Metadata(params string[] configs)=>JsonSerializer.Serialize(new
    {
        id=Model,sha=Revision,cardData=new{license="See upstream license"},
        siblings=configs.Append(configs.Contains("config.json")?"model.safetensors":"transformer/model.safetensors").Select(file=>new{rfilename=file,size=file.EndsWith(".safetensors")?33115613408L:100L})
    });
    static async Task<string> Error(Func<Task> action)
    {
        try{await action();throw new Exception("Expected catalog rejection");}
        catch(InvalidOperationException e){return e.Message;}
    }
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(Path.Combine(".build","model-catalog-"+Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var metadata=Metadata("model_index.json","transformer/config.json","text_encoder/config.json","vae/config.json");
        var replies=new Dictionary<string,Reply>
        {
            [Supported]=new(HttpStatusCode.OK,"`"+Model+"`"),
            [MetadataUrl(false)]=new(HttpStatusCode.OK,metadata),
            [MetadataUrl(true)]=new(HttpStatusCode.OK,metadata),
            [FileUrl("model_index.json")]=new(HttpStatusCode.OK,"""{"_class_name":"QwenImage21Pipeline","transformer":["diffusers","QwenImage21Transformer2DModel"],"text_encoder":["transformers","Qwen3VLForConditionalGeneration"],"vae":["diffusers","AutoencoderKLQwenImage21"],"optional":[null,null]}"""),
            [FileUrl("transformer/config.json")]=new(HttpStatusCode.OK,"""{"_class_name":"QwenImage21Transformer2DModel"}"""),
            [FileUrl("text_encoder/config.json")]=new(HttpStatusCode.OK,"""{"model_type":"qwen3_vl"}"""),
            [FileUrl("vae/config.json")]=new(HttpStatusCode.OK,"{}")
        };
        using var source=new Source(replies);
        using var http=new HttpClient(source);
        Task<GpuDevice[]> Hardware()=>Task.FromResult<GpuDevice[]>([new("0000:01:00.0","NVIDIA","Fixture GPU","nvidia","GPU-fixture",49152,[],[])]);
        ModelCatalog Catalog(string name)=>new(Path.Combine(root,name),Hardware,http);
        var selection=new ModelSelection(Model,Revision,"upstream","vLLM-Omni","NVIDIA");
        try
        {
            var catalog=Catalog("resolve");
            var options=await catalog.Options(Model,"vLLM-Omni");
            var recipe=await catalog.Resolve(selection with{Revision=options.Revision});
            check(recipe.Hub?.Repository==Model&&recipe.Hub.Revision==Revision&&recipe.Command.Contains("--omni")&&recipe.GpuCount==1,
                "Qwen-Image-2.1 resolves its Diffusers checkpoint into a pinned Omni GPU recipe");
            new RecipeCatalog(Path.Combine(root,"empty"),Path.Combine(root,"resolve","catalog-selected")).Verify(recipe);
            await Catalog("startup").ValidateEngineCheckpoint(recipe);
            check(!source.Requests.Contains(FileUrl("config.json"))&&source.Requests.Count(url=>url==FileUrl("text_encoder/config.json"))==2,
                "Model selection and saved workload startup validate Diffusers component configs without requesting a missing root config.json");
            check(source.Requests.Where(url=>url.Contains("/raw/")).All(url=>url.Contains("/raw/"+Revision+"/")),
                "Diffusers configuration reads use the selected immutable checkpoint revision");

            var message=await Error(()=>Catalog("vllm").Resolve(selection with{Engine="vLLM"}));
            check(message.Contains("config.json required by vLLM"),"Text vLLM rejects a diffusion-only checkpoint with a configuration error");

            replies[MetadataUrl(true)]=new(HttpStatusCode.OK,Metadata());
            message=await Error(()=>Catalog("missing").Resolve(selection));
            check(message.Contains("does not publish config.json or a Diffusers model_index.json"),"Missing checkpoint configuration is not presented as a network outage");
            replies[MetadataUrl(true)]=new(HttpStatusCode.OK,metadata);
            var pipeline=replies[FileUrl("model_index.json")];
            replies[FileUrl("model_index.json")]=new(HttpStatusCode.OK,"""{"_class_name":null}""");
            message=await Error(()=>Catalog("invalid").Resolve(selection));
            check(message.Contains("does not identify a pipeline class"),"Malformed Diffusers manifests cannot produce recipes");
            replies[FileUrl("model_index.json")]=pipeline;

            var encoder=replies[FileUrl("text_encoder/config.json")];
            replies[FileUrl("text_encoder/config.json")]=new(HttpStatusCode.OK,"""{"quantization_config":{"config_groups":{"embeddings":{"targets":["embed_tokens"]}}}}""");
            message=await Error(()=>Catalog("packed-diffusion").Resolve(selection));
            var startupMessage=await Error(()=>Catalog("packed-startup").ValidateEngineCheckpoint(recipe));
            check(message.Contains("packed token embeddings")&&startupMessage.Contains("packed token embeddings"),
                "Diffusers text encoders retain packed-embedding protection during selection and startup");
            replies[FileUrl("text_encoder/config.json")]=encoder;

            replies[MetadataUrl(true)]=new(HttpStatusCode.OK,Metadata("model_index.json","transformer/config.json","text_encoder/hf_quant_config.json"));
            replies[FileUrl("text_encoder/hf_quant_config.json")]=new(HttpStatusCode.OK,"""{"config_groups":{"embeddings":{"targets":["embed_tokens"]}}}""");
            message=await Error(()=>Catalog("diffusion-sidecar").Resolve(selection));
            check(message.Contains("packed token embeddings"),"Diffusers component quantization sidecars are checked even without a component config.json");

            replies[MetadataUrl(true)]=new(HttpStatusCode.OK,Metadata("config.json","model_index.json"));
            replies[FileUrl("config.json")]=new(HttpStatusCode.OK,"{}");
            var reads=source.Requests.Count(url=>url==FileUrl("model_index.json"));
            await Catalog("root-config").Resolve(selection with{Engine="vLLM"});
            await Catalog("omni-root-config").Resolve(selection);
            check(source.Requests.Count(url=>url==FileUrl("model_index.json"))==reads,
                "Transformers checkpoints keep root config.json validation for both GPU engines");
            replies[FileUrl("config.json")]=new(HttpStatusCode.OK,"""{"quantization_config":{"config_groups":{"embeddings":{"targets":["embed_tokens"]}}}}""");
            message=await Error(()=>Catalog("packed-root").Resolve(selection));
            check(message.Contains("packed token embeddings"),"Root checkpoint configuration still rejects packed token embeddings");
            replies[MetadataUrl(true)]=new(HttpStatusCode.OK,metadata);

            foreach(var status in new[]{HttpStatusCode.NotFound,HttpStatusCode.Unauthorized,HttpStatusCode.Forbidden,HttpStatusCode.TooManyRequests,HttpStatusCode.ServiceUnavailable})
            {
                replies[FileUrl("model_index.json")]=new(status,"upstream response");
                message=await Error(()=>Catalog("http-"+(int)status).Resolve(selection));
                check(message.Contains("HTTP "+(int)status)&&!message.Contains("Check the network"),"Upstream HTTP "+(int)status+" remains distinct from a connection failure");
                if(status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    check(message.Contains("Hugging Face token in Settings"),"Denied model access identifies the repository permissions and credential settings");
            }
            replies[FileUrl("model_index.json")]=pipeline;
            var supported=replies[Supported];
            replies[Supported]=new(HttpStatusCode.Forbidden,"upstream response");
            message=await Error(()=>Catalog("github-denied").Resolve(selection));
            check(message.Contains("HTTP 403")&&!message.Contains("Hugging Face token"),"GitHub access failures do not point to unrelated Hugging Face credentials");
            replies[Supported]=supported;
            var cached=Catalog("cached");
            await cached.Options(Model,"vLLM-Omni");
            foreach(var file in Directory.GetFiles(Path.Combine(root,"cached","catalog-cache")))File.SetLastWriteTimeUtc(file,DateTime.UtcNow.AddDays(-2));
            source.Offline=true;
            check((await cached.Options(Model,"vLLM-Omni")).Revision==Revision,"Catalog connection failures retain the stale metadata fallback");
            message=await Error(()=>Catalog("offline").Options(Model,"vLLM-Omni"));
            check(message=="The model catalog could not be reached. Check the network and try again.","Uncached connection failures retain network recovery guidance");
        }
        finally{Directory.Delete(root,true);}
    }
    record Reply(HttpStatusCode Status,string Body);
    sealed class Source(Dictionary<string,Reply> replies):HttpMessageHandler
    {
        public List<string> Requests {get;}=[];
        public bool Offline {get;set;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(Offline)throw new HttpRequestException("Fixture connection failure");
            var url=request.RequestUri!.AbsoluteUri;Requests.Add(url);
            var reply=replies.GetValueOrDefault(url,new Reply(HttpStatusCode.NotFound,"Fixture resource not found"));
            return Task.FromResult(new HttpResponseMessage(reply.Status){Content=new StringContent(reply.Body),RequestMessage=request});
        }
    }
}
