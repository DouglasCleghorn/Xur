using System.Text.Json;
using System.Text.RegularExpressions;
using Xur.Domain;
namespace Xur.Agent;

public static class StationGraphics
{
    static readonly SemaphoreSlim gate=new(1,1);
    static readonly string[] EnvironmentKeys=["DISPLAY","WAYLAND_DISPLAY","XAUTHORITY","KWIN_DRM_DEVICES","DRI_PRIME","__GLX_VENDOR_LIBRARY_NAME","VK_DRIVER_FILES","VK_ICD_FILENAMES","VK_LOADER_DRIVERS_SELECT","DXVK_FILTER_DEVICE_UUID","DXVK_FILTER_DEVICE_NAME","VKD3D_FILTER_DEVICE_NAME","TZ"];
    public static Dictionary<string,string> SelectEnvironment(string text)=>text.Split('\n').Select(l=>l.Split('=',2)).Where(p=>p.Length==2&&EnvironmentKeys.Contains(p[0])&&p[1].Length<=4096&&!p[1].Any(char.IsControl)).ToDictionary(p=>p[0],p=>p[1]);
    public static string[] LaunchEnvironment(GpuDevice gpu)
    {
        // Do not use GPU names to choose between several identical cards.
        // The user slice still enforces exact device ownership.
        if(gpu.Vendor=="NVIDIA")return ["__GLX_VENDOR_LIBRARY_NAME=nvidia"];
        if(gpu.Vendor is "AMD" or "Intel"&&Regex.IsMatch(gpu.Pci,@"^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$"))
            return ["DRI_PRIME=pci-"+gpu.Pci.Replace(':','_').Replace('.','_')+"!"];
        return [];
    }
    public static string[] NvencProbeArguments(GpuDevice gpu)=>[
        ..StationStreaming.EncoderEnvironment(gpu).Select(e=>e["--setenv=".Length..]),
        "/usr/bin/ffmpeg","-hide_banner","-nostdin","-loglevel","verbose",
        "-f","lavfi","-i","color=c=black:s=1280x720:r=30","-frames:v","30",
        "-an","-c:v","h264_nvenc","-gpu","0","-pix_fmt","yuv420p","-f","null","-"];
    public static async Task<object> Collect(Workload w,GpuDevice gpu,Func<string,string[],int,Task<ProcessResult>>? runner=null)
    {
        Task<ProcessResult> Run(string exe,string[] args,int seconds)=>runner!=null?runner(exe,args,seconds):Processes.Run(exe,args,seconds);
        if(!await gate.WaitAsync(0))throw new InvalidOperationException("A graphics check is already running. Try again shortly.");
        try
        {
            StationAccounts.ValidateForStop(w);
            var user=StationAccounts.Username(w);
            var uidResult=await Run("id",["-u",user],5);
            if(uidResult.ExitCode!=0||!int.TryParse(uidResult.Output.Trim(),out var uid)||uid<1000)throw new InvalidOperationException("Load the workstation before checking graphics.");
            var userEnvironment=await Run("runuser",["-u",user,"--","env","XDG_RUNTIME_DIR=/run/user/"+uid,"DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/"+uid+"/bus","systemctl","--user","show-environment"],10);
            if(userEnvironment.ExitCode!=0)throw new InvalidOperationException("The workstation user session is not available.");
            var environment=SelectEnvironment(userEnvironment.Output);
            var probes=new Dictionary<string,object>();
            async Task Probe(string name,string exe,string[] args,bool session)
            {
                try
                {
                    var unit="xur-graphics-"+Guid.NewGuid().ToString("N");
                    var command=session?"systemd-run":exe;
                    var arguments=session?new[]{"--quiet","--wait","--collect","--unit="+unit,"--property=User="+user,"--property=Slice=user-"+uid+".slice","--property=RuntimeMaxSec=20","--property=TimeoutStopSec=2","--setenv=HOME=/var/home/"+user,"--setenv=XDG_RUNTIME_DIR=/run/user/"+uid,"--setenv=DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/"+uid+"/bus"}.Concat(environment.Select(e=>"--setenv="+e.Key+"="+e.Value)).Concat([exe]).Concat(args).ToArray():args;
                    var result=await Run(command,arguments,session?25:10);
                    var output=result.Output;
                    if(session){var journal=await Run("journalctl",["--no-pager","--output=cat","--unit="+unit,"--lines=400"],5);output+=journal.Output;}
                    var text=Redaction.Logs(output);
                    probes[name]=new {command=exe,arguments=args,workstationDeviceBoundary=session,exitCode=result.ExitCode,output=text.Length>32768?text[..32768]+"\n[truncated]":text};
                }
                catch(Exception e) when(e is IOException or System.ComponentModel.Win32Exception or OperationCanceledException){probes[name]=new{command=exe,arguments=args,exitCode=-1,error=e.GetType().Name};}
            }
            await Probe("vulkan","/usr/bin/vulkaninfo",["--summary"],true);
            if(gpu.Vendor=="NVIDIA")
            {
                await Probe("vulkanNvidiaOnly","/usr/bin/env",["VK_LOADER_DRIVERS_SELECT=*nvidia*","VK_LOADER_DEBUG=error,warn","/usr/bin/vulkaninfo","--summary"],true);
                // Isolate encoding from KWin/PipeWire. Keep exactly the same UUID
                // selection and user slice as Sunshine; never grant peer GPUs.
                await Probe("nvenc","/usr/bin/env",NvencProbeArguments(gpu),true);
                await Probe("nvidiaMapping","nvidia-smi",["--query-gpu=index,pci.bus_id,uuid,driver_version","--format=csv,noheader,nounits"],false);
                // encodersessions continuously monitors and never completes as
                // a report probe. Collect a one-shot snapshot instead; XML also
                // supplies the device minor unavailable in the selective query.
                await Probe("nvidiaDeviceDetails","nvidia-smi",["-q","-x","-i",gpu.RuntimeId],false);
            }
            if(environment.ContainsKey("DISPLAY"))await Probe("openGl","/usr/bin/glxinfo",["-B"],true);
            else probes["openGl"]=new{error="The desktop did not publish an Xwayland DISPLAY."};
            await Probe("graphicsPackages","rpm",["-qa","--qf","%{NAME}.%{ARCH} %{VERSION}-%{RELEASE}\\n","*vulkan*","*nvidia*","*mesa*","*steam*","*wine*"],false);
            await Probe("devicePolicy","systemctl",["show","user-"+uid+".slice","--property=DevicePolicy,DeviceAllow"],false);
            return new{capturedAt=DateTimeOffset.UtcNow,workstation=w.Id,user,gpu=gpu.Pci,gpuName=gpu.Name,gpuUuid=gpu.RuntimeId,environment,probes,
                nvencNote=gpu.Vendor=="NVIDIA"?"The NVENC probe encodes 30 synthetic frames to a null output using host FFmpeg, the assigned GPU UUID and the workstation's existing device restrictions. It does not capture the desktop or change access permissions. FFmpeg missing or lacking h264_nvenc is a probe availability failure. A successful test isolates the remaining investigation to Sunshine's libraries/capture path; it does not prove Sunshine works.":null,
                note="Probes run as the workstation user inside its device-restricted slice. Host Vulkan success does not prove a game's 32-bit Steam runtime works. For a failing Steam game, use PROTON_LOG=1 %command% in Launch Options, reproduce once, then review steam-<appid>.log in the workstation home."};
        }finally{gate.Release();}
    }
}
