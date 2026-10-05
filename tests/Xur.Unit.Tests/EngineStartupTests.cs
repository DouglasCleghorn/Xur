using System.Text.Json;
using Xur.Agent;
using Xur.Domain;
static class EngineStartupTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var catalog=Path.GetFullPath(Path.Combine(".build","engine-catalog-"+Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(catalog);
        try
        {
            var recipe=new Recipe("engine-test","Engine","@engine/server",[],8080,"/health","CPU",0,0,"");
            var file=Path.Combine(catalog,"model.json");
            await File.WriteAllTextAsync(file,JsonSerializer.Serialize(recipe,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var selected=new RecipeCatalog(catalog).Recipes.Single();
            check(selected.Image==EngineImages.Image("server")&&!selected.Image.Contains('@'),"Default catalog engine aliases resolve to the channel manifest before validation and saving");
            {
                var legacy=selected with{Image="ghcr.io/ggml-org/llama.cpp@sha256:e33f80e54fc3f403118ab92b24f21dc1a3125ffd0c7725532028eca8128b548d"};
                new RecipeCatalog(catalog).Verify(legacy);
                check(legacy.Image.Contains("@sha256:"),"Previously saved default CPU recipes stay authorized before resolving their latest engine");
                bool tampered=false;try{new RecipeCatalog(catalog).Verify(legacy with{Command=["changed"]});}catch(InvalidOperationException){tampered=true;}
                check(tampered,"Image identity compatibility never authorizes modified recipe commands");
            }
            await File.WriteAllTextAsync(file,JsonSerializer.Serialize(recipe with{Image="@engine/missing"},new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            bool unknown=false;try{_ = new RecipeCatalog(catalog);}catch(InvalidOperationException){unknown=true;}
            check(unknown,"Unknown engine aliases cannot become workload image references");
        }
        finally{Directory.Delete(catalog,true);}
        foreach(var image in new[]{"registry.example/model", "model:1.0", "registry.example/model:1.0\n", "registry.example/model:bad tag", "--privileged"})
        {
            bool invalid=false;
            try{ProfilePolicy.ValidateRecipe(new Recipe("image-test","Image",image,[],8080,"/health","CPU",0,0,""));}
            catch(InvalidOperationException){invalid=true;}
            check(invalid,"Model images still reject missing versions and malformed references: "+image.Trim());
        }
        var mtp=ModelLaunchSettings.Vllm(ModelLaunchSettings.QwenMtp,2);
        check(mtp[Array.IndexOf(mtp,"--max-num-seqs")+1]=="1","Qwen MTP uses bounded single-sequence concurrency, below the reported 115 Mamba blocks");
        check(mtp.Contains("--no-enable-prefix-caching")&&mtp.Contains("--mamba-cache-mode")&&mtp.Contains("--speculative-config"),"Qwen MTP explicitly enables speculation and aligned Mamba caching with prefix reuse disabled");
        check(!ModelLaunchSettings.Vllm("owner/ordinary",1).Contains("--speculative-config"),"MTP is never inferred from unrelated models");
        check(EngineStartup.Failure("max_num_seqs (256) exceeds available Mamba cache blocks (115)")?.Contains("concurrency limit")==true,"Reported Mamba allocation failure is not reported as a health timeout");
        check(EngineStartup.Failure("ModuleNotFoundError: No module named 'missing_dependency'\nEngineCore failed to start.")=="Engine startup failed: ModuleNotFoundError: No module named 'missing_dependency'","Missing engine dependencies retain the actionable cause instead of the generic initialization failure");
        var logged="ValueError: There is no module or parameter named 'embed_tokens.weight_packed' in Qwen3_5Model. The available parameters belonging to embed_tokens (VocabParallelEmbedding) are: {'embed_tokens.weight'}\nEngineCore failed to start.";
        check(EngineStartup.Failure(logged)?.Contains("packed token-embedding weights")==true,"Recorded Qwen embedding failure produces a specific compatibility error");
        check(EngineStartup.Failure("Loading safetensors checkpoint shards: 50%\nWARNING: using default context") is null,"Slow checkpoint loading and warnings are not startup failures");
        check(EngineStartup.Failure("torch.OutOfMemoryError: CUDA out of memory")?.Contains("CUDA out of memory")==true,"Engine memory failures retain the actionable cause");
        using var packed=JsonDocument.Parse("""{"quantization_config":{"config_groups":{"group_embed":{"targets":["re:.*embed_tokens$"]}}}}""");
        var denied=false;try{EngineStartup.ValidateCheckpoint("vLLM","owner/model",packed.RootElement);}catch(InvalidOperationException e){denied=e.Message.Contains("packed token embeddings");}
        check(denied,"Packed embedding quantization cannot produce a generic vLLM recipe");
        using var empty=JsonDocument.Parse("{}");using var index=JsonDocument.Parse("""{"weight_map":{"model.language_model.embed_tokens.weight_packed":"model-1.safetensors"}}""");
        denied=false;try{EngineStartup.ValidateCheckpoint("vLLM","owner/model",empty.RootElement,index.RootElement);}catch(InvalidOperationException){denied=true;}
        check(denied,"Checkpoint index independently detects packed embeddings");
        denied=false;try{EngineStartup.ValidateCheckpoint("vLLM","lued/Qwen3.8-27B-INT8-W8A16-DFlash2",empty.RootElement);}catch(InvalidOperationException e){denied=e.Message.Contains("drafter");}
        check(denied,"DFlash2 cannot silently omit its patched runtime and separate drafter");
        EngineStartup.ValidateCheckpoint("llama.cpp","owner/model",packed.RootElement);
        EngineStartup.ValidateCheckpoint("vLLM","owner/model",empty.RootElement);
        check(true,"Compatibility restriction does not reject GGUFs or ordinary unquantized embeddings");
        async Task<ProcessResult> Run(string exe,string[] args,int timeout)
        {
            await Task.Yield();
            if(exe=="nvidia-smi")throw new System.ComponentModel.Win32Exception("Missing executable");
            return exe switch {"podman"=>new(1,""),"uname"=>new(0,"test-kernel"),"tailscale"=>new(0,"1.102.4\n commit: example"),_=>new(0,"1.2.3-1\n")};
        }
        var tools=await new ToolUpdateInventory(AppContext.BaseDirectory,Run).Read();
        check(tools.Length==17&&tools.Any(t=>t.Name=="vLLM-Omni")&&tools.Any(t=>t.Name=="Sunshine"),"Updates inventories all engine variants and system tools");
        check(tools.Where(t=>t.Image!=null).All(t=>t.Downloaded==false&&t.UpdatesWith=="Container start"),"Missing engine images are not misreported as installed");
        check(tools.Where(t=>t.Image!=null).All(t=>t.Image==EngineImages.Image(t.Id)&&t.Version==EngineImages.Version(t.Id)),"Tool inventory uses the same latest engine channels as model selection");
        check(tools.Single(t=>t.Id=="tailscale").Version=="1.102.4"&&tools.Single(t=>t.Id=="kernel").Version=="test-kernel","Host tool versions come from running tools rather than build-time defaults");
        check(tools.Single(t=>t.Id=="podman").UpdatesWith=="OS","Podman updates follow the OS lifecycle");
    }
}
