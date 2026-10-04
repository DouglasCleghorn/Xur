using System.Text.Json;
using Xur.Agent;
using Xur.Domain;
static class CachedEngineImagesTests
{
    public static async Task Run(Action<bool,string> check)
    {
        string Id(char c)=>"sha256:"+new string(c,64);
        var recipe=new Recipe("cache","Cache","ghcr.io/ggml-org/llama.cpp@sha256:"+new string('f',64),[],8080,"/health","CPU",0,0,"");
        var rows=new[]{(Id:'a',Name:"ghcr.io/ggml-org/llama.cpp:server",Created:"2026-09-01T01:00:00Z",Arch:"amd64",Os:"linux"),
            (Id:'b',Name:"ghcr.io/ggml-org/llama.cpp:server-b11500",Created:"2026-10-01T01:00:00.123456789Z",Arch:"amd64",Os:"linux"),
            (Id:'c',Name:"ghcr.io/ggml-org/llama.cpp:server-cuda-b12000",Created:"2026-10-02T01:00:00Z",Arch:"amd64",Os:"linux"),
            (Id:'d',Name:"ghcr.io/ggml-org/llama.cpp:server-b12000",Created:"2026-10-03T01:00:00Z",Arch:"arm64",Os:"linux"),
            (Id:'e',Name:"ghcr.io/ggml-org/llama.cpp:server-b12001",Created:"2026-10-04T01:00:00Z",Arch:"amd64",Os:"windows"),
            (Id:'f',Name:recipe.Image,Created:"2026-08-01T01:00:00Z",Arch:"amd64",Os:"linux")};
        var inspected=new List<string>();
        Task<ProcessResult> Run(string exe,string[] args,int seconds)
        {
            if(args[1]=="ls")return Task.FromResult(new ProcessResult(0,string.Join('\n',rows.Where(r=>!r.Name.Contains('@')).Select(r=>Id(r.Id)+" "+r.Name))));
            inspected.Add(args[2]);
            var matches=rows.Where(r=>Id(r.Id)==args[2]||r.Name==args[2]).ToArray();
            if(matches.Length==0)return Task.FromResult(new ProcessResult(1,"Image not available"));
            var row=matches.Single();
            return Task.FromResult(new ProcessResult(0,JsonSerializer.Serialize(new[]{new{Id=Id(row.Id),row.Created,Architecture=row.Arch,row.Os}})));
        }
        var cache=new CachedEngineImages(Run);
        check(await cache.Newest(recipe)==Id('b'),"Offline CPU engine selection chooses the newest Linux amd64 image by build timestamp");
        check(!inspected.Contains(Id('c')),"Offline CPU engine selection cannot substitute a CUDA variant");
        check(inspected.Contains(recipe.Image),"Previously downloaded digest-only saved images are eligible for offline startup");
        inspected.Clear();check(await cache.Newest(recipe with{Vendor="NVIDIA",Image="ghcr.io/ggml-org/llama.cpp:server-cuda"})==Id('c'),"Offline NVIDIA engine selection keeps its correct CUDA variant");
        Task<ProcessResult> Vllm(string exe,string[] args,int seconds)
        {
            if(args[1]=="ls")return Task.FromResult(new ProcessResult(0,string.Join('\n',new[]{Id('a')+" mirror.gcr.io/vllm/vllm-omni:latest",Id('b')+" mirror.gcr.io/vllm/vllm-omni:v0.30.0",Id('c')+" mirror.gcr.io/vllm/vllm-omni:nightly",Id('d')+" mirror.gcr.io/vllm/vllm-omni:v0.31.0-cu130"})));
            var newer=args[2]==Id('b');
            return Task.FromResult(new ProcessResult(0,JsonSerializer.Serialize(new[]{new{Id=newer?Id('b'):Id('a'),Created=newer?"2026-10-02T00:00:00Z":"2026-09-01T00:00:00Z",Architecture="amd64",Os="linux"}})));
        }
        check(await new CachedEngineImages(Vllm).Newest(recipe with{Engine="vLLM-Omni",Image="mirror.gcr.io/vllm/vllm-omni:latest"})==Id('b'),"Offline Omni uses its newest cached release while excluding nightly and alternate CUDA tags");
    }
}
