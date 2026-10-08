using System.Net;
using System.Text.Json;
using Xur.Agent;
using Xur.Domain;

static class CheckpointFormatTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(Path.Combine(".build","checkpoint-formats-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        const string model="owner/model";var revision=new string('c',40);
        var files=new Dictionary<string,(long Bytes,string? Text)>();
        using var handler=new Source(files,model,revision);using var http=new HttpClient(handler);
        Task<GpuDevice[]> Hardware()=>Task.FromResult<GpuDevice[]>([new("0000:01:00.0","NVIDIA","Fixture GPU","nvidia","GPU-test",24576,[],[])]);
        ModelCatalog Catalog(string name)=>new(Path.Combine(root,name),Hardware,http);
        var selection=new ModelSelection(model,revision,"upstream","vLLM","NVIDIA");
        async Task<string> Error(Func<Task> action)
        {try{await action();throw new Exception("Expected rejection");}catch(InvalidOperationException e){return e.Message;}}
        string Arg(Recipe recipe,string flag)=>recipe.Command[Array.IndexOf(recipe.Command,flag)+1];
        void File(string name,long bytes,string? text=null)=>files[name]=(bytes,text);
        try
        {
            File("params.json",100,"""{"dim":16,"n_layers":1}""");File("consolidated.safetensors",1024);
            File("model.safetensors",4096);File("pytorch_model.bin",8192);
            var catalog=Catalog("mistral");var options=await catalog.Options(model,"vLLM");var recipe=await catalog.Resolve(selection);
            check(options.Variants.Single().Bytes==1024&&options.Variants.Single().Name=="Native Mistral checkpoint",
                "Native Mistral sizing excludes alternate HF and PyTorch serializations");
            check(Arg(recipe,"--config-format")=="mistral"&&Arg(recipe,"--tokenizer-mode")=="mistral"&&Arg(recipe,"--load-format")=="mistral",
                "Mistral recipes select the native config, tokenizer and consolidated weight loader");
            await Catalog("mistral-startup").ValidateEngineCheckpoint(recipe);
            check(!handler.Requests.Any(path=>path.EndsWith("/config.json")),"Native Mistral selection and saved startup work without root config.json");
            new RecipeCatalog(Path.Combine(root,"empty"),Path.Combine(root,"mistral","catalog-selected")).Verify(recipe);

            files.Clear();File("config.json",100,"""{"max_position_embeddings":2048}""");File("pytorch_model.bin",2*1024*1024);
            File("optimizer.bin",1024*1024*1024);File("training_args.bin",1024*1024*1024);
            catalog=Catalog("pytorch");options=await catalog.Options(model,"vLLM");recipe=await catalog.Resolve(selection);
            check(options.Variants.Single().Bytes==2*1024*1024&&Arg(recipe,"--load-format")=="hf",
                "PyTorch-only checkpoints resolve using the upstream bin loader without counting training state");
            check(Arg(recipe,"--max-model-len")=="2048","Generated context limits respect the checkpoint's smaller supported context");
            check((await catalog.Search(model,"vLLM")).Single().Id==model,"Exact model lookup discovers PyTorch-only repositories");
            await Catalog("pytorch-startup").ValidateEngineCheckpoint(recipe);
            check(true,"Saved PyTorch recipes pass the shared checkpoint validation before startup");
            await catalog.Search("model","vLLM");
            check(handler.Requests.Last().Contains("search=model")&&!handler.Requests.Last().Contains("filter=safetensors"),
                "General vLLM search includes repositories without a safetensors tag");

            files.Clear();File("config.json",100,"{}");
            File("model-00001-of-00002.safetensors",1024);File("model-00002-of-00002.safetensors",2048);
            File("model.safetensors",16384);File("pytorch_model.bin",32768);
            File("model.safetensors.index.json",100,"""{"metadata":{"total_size":1},"weight_map":{"layer.a":"model-00001-of-00002.safetensors","layer.b":"model-00001-of-00002.safetensors","layer.c":"model-00002-of-00002.safetensors"}}""");
            catalog=Catalog("safetensors-index");options=await catalog.Options(model,"vLLM");recipe=await catalog.Resolve(selection);
            check(options.Variants.Single().Bytes==3072&&Arg(recipe,"--load-format")=="safetensors",
                "Safetensors indexes count each selected shard once and exclude alternate representations");
            await Catalog("indexed-startup").ValidateEngineCheckpoint(recipe);
            files.Remove("model-00002-of-00002.safetensors");
            var message=await Error(()=>Catalog("missing-shard").Resolve(selection));
            check(message.Contains("unavailable weight file"),"Incomplete indexed checkpoints cannot produce recipes");
            File("model-00002-of-00002.safetensors",2048);
            File("model.safetensors.index.json",100,"""{"weight_map":{"layer":"../outside.safetensors"}}""");
            message=await Error(()=>Catalog("invalid-index-path").Resolve(selection));
            check(message.Contains("invalid weight path"),"Shard indexes cannot reference paths outside the checkpoint");
            File("model.safetensors.index.json",100,"""{"weight_map":{"model.embed_tokens.weight_packed":"model-00001-of-00002.safetensors"}}""");
            message=await Error(()=>Catalog("packed-index").Resolve(selection));
            var startupMessage=await Error(()=>Catalog("packed-index-startup").ValidateEngineCheckpoint(recipe));
            check(message.Contains("packed token embeddings")&&startupMessage.Contains("packed token embeddings"),
                "Published shard tensor names enforce packed-embedding compatibility during selection and startup");

            files.Clear();File("config.json",100,"{}");File("pytorch_model-00001-of-00002.bin",1024);File("pytorch_model-00002-of-00002.bin",2048);
            File("pytorch_model.bin.index.json",100,"""{"weight_map":{"a":"pytorch_model-00001-of-00002.bin","b":"pytorch_model-00002-of-00002.bin"}}""");
            options=await Catalog("pytorch-index").Options(model,"vLLM");recipe=await Catalog("pytorch-index-resolve").Resolve(selection);
            check(options.Variants.Single().Bytes==3072,"PyTorch shard indexes select a complete set with accurate capacity estimates");
            File("hf_quant_config.json",100,"""{"quant_algo":"FP8"}""");
            await Catalog("quant-sidecar").Resolve(selection);
            check(handler.Requests.Any(path=>path.EndsWith("/hf_quant_config.json")),"Published quantization sidecars participate in checkpoint validation");
            File("hf_quant_config.json",100,"""{"config_groups":{"embedding":{"targets":["embed_tokens"]}}}""");
            message=await Error(()=>Catalog("packed-sidecar").Resolve(selection));
            startupMessage=await Error(()=>Catalog("packed-sidecar-startup").ValidateEngineCheckpoint(recipe));
            check(message.Contains("packed token embeddings")&&startupMessage.Contains("packed token embeddings"),
                "Sidecar-only embedding quantization cannot bypass selection or startup restrictions");

            files.Clear();File("params.json",100,"{}");File("consolidated-00001-of-00002.safetensors",1024);File("consolidated-00002-of-00002.safetensors",2048);
            File("consolidated.safetensors.index.json",100,"""{"weight_map":{"a":"consolidated-00001-of-00002.safetensors","b":"consolidated-00002-of-00002.safetensors"}}""");
            options=await Catalog("mistral-shards").Options(model,"vLLM");
            check(options.Variants.Single().Bytes==3072,"Mistral consolidated shard indexes are supported alongside single-file checkpoints");

            files.Clear();File("model_index.json",100,"""{"_class_name":"FixturePipeline","transformer":["diffusers","FixtureTransformer"]}""");
            File("transformer/diffusion_pytorch_model.bin",1024);File("transformer/diffusion_pytorch_model.fp16.safetensors",8192);
            options=await Catalog("diffusion-variant").Options(model,"vLLM-Omni");
            check(options.Variants.Single().Bytes==1024,"Diffusers precision variants do not hide the default PyTorch weight representation");
            File("transformer/diffusion_pytorch_model-00001-of-00002.safetensors",1024);File("transformer/diffusion_pytorch_model-00002-of-00002.safetensors",2048);
            File("transformer/diffusion_pytorch_model.safetensors.index.json",100,"""{"weight_map":{"a":"diffusion_pytorch_model-00001-of-00002.safetensors","b":"diffusion_pytorch_model-00002-of-00002.safetensors"}}""");
            options=await Catalog("diffusion-index").Options(model,"vLLM-Omni");
            check(options.Variants.Single().Bytes==3072,"Diffusers component indexes select complete default shards without counting bin or precision alternatives");
        }
        finally{Directory.Delete(root,true);}
    }
    sealed class Source(Dictionary<string,(long Bytes,string? Text)> files,string model,string revision):HttpMessageHandler
    {
        public List<string> Requests {get;}=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var uri=request.RequestUri!;Requests.Add(uri.AbsoluteUri);string? text;
            if(uri.AbsolutePath=="/api/models")text=JsonSerializer.Serialize(new[]{new{id=model}});
            else if(uri.AbsolutePath.StartsWith("/api/models/",StringComparison.Ordinal))
                text=JsonSerializer.Serialize(new{id=model,sha=revision,siblings=files.Select(p=>new{rfilename=p.Key,size=p.Value.Bytes})});
            else if(uri.AbsolutePath.StartsWith('/'+model+"/raw/"+revision+"/",StringComparison.Ordinal))
                text=files.GetValueOrDefault(uri.AbsolutePath[('/'+model+"/raw/"+revision+"/").Length..]).Text;
            else text=null;
            return Task.FromResult(new HttpResponseMessage(text==null?HttpStatusCode.NotFound:HttpStatusCode.OK){Content=new StringContent(text??"Missing fixture file"),RequestMessage=request});
        }
    }
}
