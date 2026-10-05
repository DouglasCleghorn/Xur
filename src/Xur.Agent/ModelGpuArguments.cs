using Xur.Domain;
namespace Xur.Agent;

public static class ModelGpuArguments
{
    public static string[] For(Workload workload,GpuDevice[] hardware)
    {
        var selected=workload.Gpus.Select(pci=>hardware.Single(g=>g.Pci==pci)).ToArray();
        var args=new List<string>();
        foreach(var gpu in selected)
            if(gpu.Vendor=="NVIDIA")args.AddRange(["--device","nvidia.com/gpu="+gpu.RuntimeId]);
            else foreach(var node in gpu.Nodes)args.AddRange(["--device",node]);
        if(selected.Any(g=>g.Vendor=="NVIDIA"))args.AddRange(["--env","CUDA_VISIBLE_DEVICES="+string.Join(',',selected.Select(g=>g.RuntimeId))]);
        if(selected.Any(g=>g.Vendor=="AMD"))args.AddRange(["--device","/dev/kfd"]);
        if(workload.Recipe.Kind=="Model" && workload.Recipe.Engine is "vLLM" or "vLLM-Omni")
        {
            // ROCm and Level Zero need syscalls outside Podman's default filter.
            // Device access remains limited to the workload's render nodes.
            if(selected.Any(g=>g.Vendor is "AMD" or "Intel"))args.Add("--security-opt=seccomp=unconfined");
            if(selected.Any(g=>g.Vendor=="Intel"))args.AddRange(["--env","ZE_ENABLE_PCI_ID_DEVICE_ORDER=1"]);
        }
        return args.ToArray();
    }
}
