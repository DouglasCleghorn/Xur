using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

public sealed class ToolUpdateInventory
{
    readonly Func<string,string[],int,Task<ProcessResult>> run;
    readonly string root;
    public ToolUpdateInventory(string? root=null,Func<string,string[],int,Task<ProcessResult>>? run=null)
    {this.root=root??AppContext.BaseDirectory;this.run=run??((exe,args,timeout)=>Processes.Run(exe,args,timeout));}
    async Task<ProcessResult> Probe(string command,string[] args,int seconds)
    {
        try{return await run(command,args,seconds);}
        catch(Exception e) when(e is System.ComponentModel.Win32Exception or IOException or OperationCanceledException){return new(127,"");}
    }
    public async Task<ToolUpdateInfo[]> Read()
    {
        var result=new List<ToolUpdateInfo>();
        using var engines=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"engine-lock.json")));
        foreach(var (id,name,repository) in new[]{("server","llama.cpp · CPU","ghcr.io/ggml-org/llama.cpp"),("server-cuda","llama.cpp · NVIDIA","ghcr.io/ggml-org/llama.cpp"),("server-rocm","llama.cpp · AMD","ghcr.io/ggml-org/llama.cpp"),("server-vulkan","llama.cpp · Vulkan","ghcr.io/ggml-org/llama.cpp"),("vllm","vLLM","mirror.gcr.io/vllm/vllm-openai"),("omni","vLLM-Omni","mirror.gcr.io/vllm/vllm-omni")})
        {
            var pin=engines.RootElement.GetProperty("engines").GetProperty(id);
            var image=repository+"@"+pin.GetProperty("manifestDigest").GetString();
            var exists=await Probe("podman",["image","exists",image],10);
            result.Add(new(id,name,pin.GetProperty("version").GetString()!,"Xur","Catalog version. Saved workloads retain their pinned engine image.",image,exists.ExitCode==0?true:exists.ExitCode==1?false:null));
        }
        using var sunshine=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"streaming-lock.json")));
        result.Add(new("sunshine","Sunshine",sunshine.RootElement.GetProperty("sunshine").GetProperty("version").GetString()!,"Xur","Bundled version. Running streams keep their current runtime until restarted."));
        foreach(var (id,name,args) in new[]{("tailscale","Tailscale",new[]{"tailscale"}),("podman","Podman",new[]{"podman"}),("plasma","Plasma desktop",new[]{"plasma-workspace"}),("mesa","Mesa",new[]{"mesa-dri-drivers"}),("kernel","Kernel",new[]{"kernel-core"}),("nvidia","NVIDIA driver",new[]{"nvidia-driver"})})
        {
            var r=await Probe("rpm",["-q","--qf","%{VERSION}-%{RELEASE}\\n",..args],10);
            var version=r.ExitCode==0?string.Join(", ",r.Output.Split('\n',StringSplitOptions.RemoveEmptyEntries).Distinct()):r.ExitCode==1?"Not installed":"Unavailable";
            if(id=="kernel")version=(await Probe("uname",["-r"],5)).Output.Trim();
            if(id=="tailscale"){var actual=await Probe("tailscale",["version"],5);if(actual.ExitCode==0)version=actual.Output.Split('\n')[0].Trim();}
            if(id=="nvidia"){var actual=await Probe("nvidia-smi",["--query-gpu=driver_version","--format=csv,noheader,nounits"],5);if(actual.ExitCode==0)version=string.Join(", ",actual.Output.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(v=>v.Trim()).Distinct());}
            result.Add(new(id,name,version,"OS","Updated with the operating system; a reboot activates the staged deployment."));
        }
        return result.ToArray();
    }
}
