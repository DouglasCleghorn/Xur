namespace Xur.Domain;

public static class ModelLaunchSettings
{
    public const string QwenMtp="lued/Qwen3.8-27B-INT8-W8A16-MTP";
    public static string[] Vllm(string model,int gpuCount)
    {
        // Deliberately bounded defaults; concurrency is not reserved KV capacity.
        var args=new List<string>{"--max-model-len","4096","--tensor-parallel-size",gpuCount.ToString(),"--gpu-memory-utilization","0.85","--served-model-name","model","--max-num-seqs",model==QwenMtp?"1":"16"};
        if(model==QwenMtp)args.AddRange(["--mamba-cache-mode","align","--no-enable-prefix-caching","--speculative-config","{\"method\":\"mtp\",\"num_speculative_tokens\":3}"]);
        return args.ToArray();
    }
    // Only the known generated recipe is migrated. Image, checkpoint revision,
    // allocation and cache identity are retained; runtime fingerprints change.
    public static Recipe RepairSaved(Recipe recipe)
    {
        if(recipe.Kind!="Model" || recipe.Engine!="vLLM" || recipe.Hub?.Repository!=QwenMtp)return recipe;
        var args=recipe.Command.ToList();
        bool Has(string flag)=>args.Any(a=>a==flag||a.StartsWith(flag+"=",StringComparison.Ordinal));
        if(Has("--max-num-seqs") && Has("--speculative-config"))return recipe;
        void Set(string flag,string? value=null)
        {
            for(var i=args.Count-1;i>=0;i--)
            {
                if(args[i].StartsWith(flag+"=",StringComparison.Ordinal))args.RemoveAt(i);
                else if(args[i]==flag){args.RemoveAt(i);if(value!=null&&i<args.Count&&!args[i].StartsWith("--"))args.RemoveAt(i);}
            }
            args.Add(flag);if(value!=null)args.Add(value);
        }
        Set("--max-num-seqs","1");
        Set("--mamba-cache-mode","align");
        args.RemoveAll(a=>a=="--enable-prefix-caching"||a.StartsWith("--enable-prefix-caching="));
        Set("--no-enable-prefix-caching");
        if(!Has("--speculative-config"))Set("--speculative-config","{\"method\":\"mtp\",\"num_speculative_tokens\":3}");
        return recipe with {Command=args.ToArray()};
    }
}
