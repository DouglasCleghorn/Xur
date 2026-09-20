using System.Text.Json;
using Xur.Agent;
using Xur.Domain;

static class StationGraphicsTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var gpu=new GpuDevice("0000:81:00.0","NVIDIA","RTX 3090","nvidia","GPU-afa09eca-a74d-c2af-29f1-d0be9a8eabc4",24576,["/dev/dri/renderD129"],[],["/dev/dri/card2"],[]);
        var recipe=new Recipe("graphics-probe","Desktop","",[],0,"","",0,0,"",Kind:"Workstation");
        var workload=new Workload("nvenc-probe-test","Desktop",recipe,[gpu.Pci],"graphics-probe");
        var calls=new List<(string Exe,string[] Args,int Seconds)>();
        Task<ProcessResult> Run(string exe,string[] args,int seconds)
        {
            calls.Add((exe,args,seconds));
            if(exe=="id")return Task.FromResult(new ProcessResult(0,"15432\n"));
            if(exe=="runuser")return Task.FromResult(new ProcessResult(0,"WAYLAND_DISPLAY=wayland-0\nDISPLAY=:0\nHF_TOKEN=private\n"));
            if(exe=="systemd-run"&&args.Contains("/usr/bin/ffmpeg"))return Task.FromResult(new ProcessResult(1,"OpenEncodeSessionEx failed: unsupported device (2)"));
            if(exe=="nvidia-smi"&&args.Contains("encodersessions"))throw new TaskCanceledException("Continuous monitoring timed out");
            if(exe=="nvidia-smi"&&args.Any(a=>a.Contains("minor_number")))return Task.FromResult(new ProcessResult(2,"Field minor_number is not a valid field to query."));
            return Task.FromResult(new ProcessResult(0,""));
        }
        using var report=JsonDocument.Parse(JsonSerializer.Serialize(await StationGraphics.Collect(workload,gpu,Run)));
        var nvenc=calls.Single(c=>c.Args.Contains("/usr/bin/ffmpeg"));
        check(nvenc.Exe=="systemd-run"&&nvenc.Args.Contains("--property=User="+StationAccounts.Username(workload))&&nvenc.Args.Contains("--property=Slice=user-15432.slice"),"NVENC diagnostic runs as the workstation user inside its existing device boundary");
        check(nvenc.Args.Contains("CUDA_VISIBLE_DEVICES="+gpu.RuntimeId)&&nvenc.Args.Contains("CUDA_DEVICE_ORDER=PCI_BUS_ID")&&nvenc.Args.Contains("--property=RuntimeMaxSec=20"),"NVENC diagnostic uses Sunshine's exact UUID selection with a bounded runtime");
        check(nvenc.Args.TakeLast(3).SequenceEqual(new[]{"-f","null","-"})&&nvenc.Args.Contains("-nostdin")&&nvenc.Args.Contains("30"),"NVENC diagnostic uses bounded synthetic frames and writes no video file");
        check(!calls.Any(c=>c.Exe is "chmod" or "setfacl" or "modprobe"||c.Args.Any(a=>a.Contains("DeviceAllow=")||a.Contains("private"))),"Graphics diagnostics do not relax access or forward unrelated session secrets");
        var probes=report.RootElement.GetProperty("probes");
        check(probes.GetProperty("nvenc").GetProperty("exitCode").GetInt32()==1&&probes.GetProperty("nvenc").GetProperty("output").GetString()!.Contains("unsupported device"),"NVENC initialization errors survive in the graphics report");
        check(probes.TryGetProperty("devicePolicy",out _)&&probes.GetProperty("nvidiaMapping").GetProperty("exitCode").GetInt32()==0&&probes.GetProperty("nvidiaDeviceDetails").GetProperty("exitCode").GetInt32()==0,"A failed encode still collects NVIDIA diagnostics without unsupported CSV fields or continuous monitoring");
        check(report.RootElement.GetProperty("gpuUuid").GetString()==gpu.RuntimeId,"Graphics report records the intended physical GPU identity");
        calls.Clear();
        await StationGraphics.Collect(workload,gpu with{Vendor="AMD",RuntimeId=""},Run);
        check(!calls.Any(c=>c.Exe=="nvidia-smi"||c.Args.Contains("/usr/bin/ffmpeg")),"Non-NVIDIA graphics checks skip NVIDIA-specific probes");
    }
}
