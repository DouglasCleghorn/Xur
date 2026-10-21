namespace Xur.Domain;

// GPU allocation may vary per profile; every other recipe setting still comes
// from the installed catalog. Derive variants from that source, never a request.
public static class RecipeGpuSelection
{
    public static bool Supports(Recipe recipe)=>UnslothWorkload.Is(recipe) || recipe.Kind=="Model" && recipe.Engine=="llama.cpp";
    public static Recipe Select(Recipe source,string[] devices,GpuDevice[] hardware)
    {
        if(!Supports(source))return source;
        var selected=devices.Select(pci=>hardware.SingleOrDefault(g=>g.Pci==pci)??throw new InvalidOperationException("Select an available GPU for each GPU row.")).ToArray();
        if(selected.Select(g=>g.Vendor).Distinct().Count()>1)throw new InvalidOperationException("Select GPUs from the same vendor for this workload.");
        return Apply(source,selected.FirstOrDefault()?.Vendor??"CPU",selected.Length);
    }
    public static Recipe Apply(Recipe source,string vendor,int count)
    {
        if(!Supports(source) || count is <0 or >16 || vendor is not ("CPU" or "NVIDIA" or "AMD" or "Intel") || (vendor=="CPU")!=(count==0))
            throw new InvalidOperationException("Select up to 16 compatible GPUs.");
        if(UnslothWorkload.Is(source))
        {
            if(vendor!="NVIDIA" || count<1)throw new InvalidOperationException("Unsloth needs at least one NVIDIA GPU.");
            return source with{GpuCount=count};
        }
        // Preserve exact fingerprints when the allocation settings are unchanged.
        if(source.Vendor==vendor && source.GpuCount==count)return source;
        var args=new List<string>();
        var gpuFlags=new HashSet<string>{"-ngl","--gpu-layers","--n-gpu-layers","-sm","--split-mode","-ts","--tensor-split","-mg","--main-gpu","-dev","--device"};
        for(var i=0;i<source.Command.Length;i++)
        {
            var argument=source.Command[i];var flag=argument.Split('=',2)[0];
            if(!gpuFlags.Contains(flag)){args.Add(argument);continue;}
            if(!argument.Contains('='))i++; // These flags all take a value.
        }
        args.AddRange(["--n-gpu-layers",count==0?"0":"999"]);
        if(count>1)args.AddRange(["--split-mode","layer","--tensor-split",string.Join(',',Enumerable.Repeat("1",count))]);
        return source with{Vendor=vendor,GpuCount=count,MemoryMiB=count==0?0:(MemoryMiB(source)+count-1)/count,
            Image=EngineImages.For("llama.cpp",vendor),Command=args.ToArray()};
    }
    public static long MemoryMiB(Recipe recipe)
    {
        var bytes=recipe.Model?.Bytes??recipe.Files?.Sum(f=>f.Asset.Bytes)??0;
        return bytes>0?(bytes+1048575)/1048576+2048:recipe.MemoryMiB*recipe.GpuCount;
    }
    public static bool Matches(Recipe source,Recipe candidate)
    {
        if(!Supports(source))return false;
        try{return Canonical.Hash(Apply(source,candidate.Vendor,candidate.GpuCount))==Canonical.Hash(candidate);}
        catch(InvalidOperationException){return false;}
    }
}
