using System.Buffers.Binary;
using System.Text.Json;
using Xur.Agent;
using Xur.Domain;

static class ModelGpuTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(Path.Combine(".build","model-gpus-"+Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(root,"catalog-cache"));
        const string model="owner/model";var revision=new string('b',40);
        void Cache(string url,string value)=>File.WriteAllText(Path.Combine(root,"catalog-cache",Canonical.Hash(url+"\n")+".cache"),value);
        Cache("https://huggingface.co/api/models/"+model+"/revision/"+revision+"?blobs=true",JsonSerializer.Serialize(new{siblings=new[]{new{rfilename="model.safetensors",size=20L*1024*1024*1024}},cardData=new{license="Apache-2.0"}}));
        Cache("https://huggingface.co/"+model+"/raw/"+revision+"/config.json","{}");
        Cache("https://raw.githubusercontent.com/vllm-project/vllm-omni/main/docs/models/supported_models.md","`owner/model`");
        GpuDevice[] hardware=[];
        var catalog=new ModelCatalog(root,()=>Task.FromResult(hardware));
        GpuDevice Gpu(string vendor,int index)=>new($"0000:0{index}:00.0",vendor,vendor+" fixture",vendor=="NVIDIA"?"nvidia":vendor=="AMD"?"amdgpu":"xe","GPU-"+index,16384,["/dev/dri/renderD"+(127+index)],[]);
        try
        {
            foreach(var engine in new[]{"vLLM","vLLM-Omni"})
            foreach(var vendor in new[]{"NVIDIA","AMD","Intel"})
            {
                hardware=[Gpu(vendor,1),Gpu(vendor,2)];
                var recipe=await catalog.Resolve(new(model,revision,"upstream",engine,vendor));
                check(recipe.Vendor==vendor&&recipe.GpuCount==2&&recipe.MemoryMiB==11264,"Catalog resolves "+engine+" on "+vendor+" against observed dedicated GPU capacity");
                check(recipe.Image==EngineImages.For(engine,vendor)&&recipe.Hub?.Revision==revision,"Catalog pins checkpoint and chooses the matching "+engine+" / "+vendor+" runtime");
                check(engine=="vLLM"?recipe.Command[Array.IndexOf(recipe.Command,"--tensor-parallel-size")+1]=="2":recipe.Command.Contains("--omni"),"Generated "+engine+" command retains its upstream GPU launch settings");
                new RecipeCatalog(Path.Combine(root,"empty"),Path.Combine(root,"catalog-selected")).Verify(recipe);
                var workload=new Workload("test","Test",recipe,[hardware[1].Pci,hardware[0].Pci],"test");
                ProfilePolicy.Validate(new("test","Test",1,[workload]),new("test",hardware,[]));
                var args=ModelGpuArguments.For(workload,[..hardware,Gpu(vendor,3)]);
                check(!args.Contains("/dev/dri")&&!args.Contains("/dev/dri/renderD130")&&!args.Contains("--privileged"),"GPU container access excludes unallocated "+vendor+" devices");
                check(vendor=="AMD"?args.Contains("/dev/kfd"):!args.Contains("/dev/kfd"),"Only ROCm receives the kernel compute device");
                check(vendor=="NVIDIA"?args.Contains("CUDA_VISIBLE_DEVICES=GPU-2,GPU-1"):args.Contains("--security-opt=seccomp=unconfined"),"Container launch uses the "+vendor+" runtime requirements");
            }
            async Task<bool> Rejected(string device)
            {try{await catalog.Resolve(new(model,revision,"upstream","vLLM",device));return false;}catch(InvalidOperationException){return true;}}
            hardware=[Gpu("AMD",1),Gpu("AMD",2)];
            check((await catalog.Resolve(new(model,revision,"upstream","vLLM","Auto"))).Vendor=="AMD","Automatic vLLM selection accepts a healthy AMD host");
            hardware=[Gpu("Intel",1),Gpu("Intel",2)];
            check((await catalog.Resolve(new(model,revision,"upstream","vLLM-Omni","Auto"))).Vendor=="Intel","Automatic Omni selection accepts a healthy Intel host");
            check(await Rejected("CPU")&&await Rejected("NVIDIA"),"GPU engines reject CPU execution and absent GPU vendors");
            hardware=hardware.Select(g=>g with{Problems=["Intel graphics driver is unavailable"]}).ToArray();
            check(await Rejected("Intel"),"Unhealthy Intel devices cannot produce model recipes");
        }
        finally{Directory.Delete(root,true);}

        foreach(var driver in new[]{"i915","xe"})
        {
            int header=driver=="xe"?8:16;var bytes=new byte[header+3*88];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes,3);
            for(int i=0;i<3;i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(header+i*88),(ushort)(i==0?0:1));
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(header+i*88+8),i==0?64UL<<30:8UL<<30);
            }
            check(IntelGpuMemory.DeviceBytes(bytes,driver)==16L<<30,driver+" VRAM sums local regions without counting host RAM");
            check(IntelGpuMemory.DeviceBytes(bytes[..^1],driver)==0,driver+" truncated query cannot claim GPU capacity");
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(header+88+8),ulong.MaxValue);
            check(IntelGpuMemory.DeviceBytes(bytes,driver)==0,driver+" unknown or overflowing memory cannot satisfy allocations");
            check(IntelGpuMemory.DeviceBytes(new byte[header+88],driver)==0,driver+" integrated GPU cannot borrow system RAM for dedicated-memory estimates");
        }
    }
}
