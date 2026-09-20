using Xur.Agent;
using Xur.Domain;

static class StationStreamingTests
{
    public static void Run(Action<bool,string> check)
    {
        using(var headless=System.Text.Json.JsonDocument.Parse(StationStreaming.Applications(true,"/safe/display.py")))
        {
            var prep=headless.RootElement.GetProperty("apps")[0].GetProperty("prep-cmd")[0];
            check(prep.GetProperty("do").GetString()=="/usr/bin/python3 /safe/display.py --moonlight"&&!prep.GetProperty("elevated").GetBoolean(),"Moonlight resolution hook runs as the workstation user");
        }
        using(var local=System.Text.Json.JsonDocument.Parse(StationStreaming.Applications(false,"/safe/display.py")))
            check(!local.RootElement.GetProperty("apps")[0].TryGetProperty("prep-cmd",out _),"Streaming never resizes an attached physical monitor automatically");
        const string uuid="GPU-fe86dde8-4ea4-e04c-abee-e30b29c3c79b";
        var gpu=new GpuDevice("0000:c6:00.0","NVIDIA","RTX 3090","nvidia",uuid,24576,["/dev/dri/renderD128"],[],["/dev/dri/card1"],["card1-HDMI-A-1"]);
        var environment=StationStreaming.EncoderEnvironment(gpu);
        check(environment.Contains("--setenv=CUDA_VISIBLE_DEVICES="+uuid),"Sunshine CUDA device zero is scoped to the assigned GPU UUID regardless of runtime index or device minor");
        check(StationStreaming.EncoderEnvironment(gpu with{Cards=["/dev/dri/card3"],Nodes=["/dev/dri/renderD131"]}).SequenceEqual(environment),"DRM renumbering cannot redirect Sunshine's CUDA device selection");
        foreach(var invalid in new[]{"","0","GPU-test",uuid+",GPU-other",uuid+"\nCUDA_VISIBLE_DEVICES=0"})
        {
            var rejected=false;try{StationStreaming.EncoderEnvironment(gpu with{RuntimeId=invalid});}catch(InvalidOperationException){rejected=true;}
            check(rejected,"Missing, ambiguous or malformed streaming GPU UUID is rejected");
        }
        check(StationStreaming.EncoderEnvironment(gpu with{Vendor="AMD",RuntimeId=""}).Length==0,"Non-NVIDIA streaming does not receive a CUDA device selection");
        check(StationStreaming.EncodingHealth(new ProcessResult(1,"-- No entries --\n"),"nvenc") is {Ready:false,Error:null},"No matching startup journal entries means wait, not startup failure");
        check(StationStreaming.EncodingHealth(new ProcessResult(1,"Permission denied"),"nvenc") is {Ready:false,Error:not null},"A journal permission failure remains visible");
        check(StationStreaming.EncodingHealth(new ProcessResult(1,""),"nvenc","core-dump") is {Ready:false,Error:not null},"A crash remains a failure even without encoder log entries");
        check(StationStreaming.EncodingHealth("Couldn't open: /dev/dri/card2: Permission denied\nCouldn't open DRM FD for CUDA device: No such file or directory","nvenc") is {Ready:false,Error:not null},"Recorded headless DRM permission denial fails immediately with a device error");
        const string failure="Error: [h264_nvenc] OpenEncodeSessionEx failed: unsupported device (2): (no details)\nError: Couldn't find any working encoder matching [nvenc]\nInfo: Trying encoder [vulkan]";
        var capture=StationStreaming.EncodingHealth("Unable to initialize capture method\nCouldn't find any working encoder matching [nvenc]","nvenc");
        check(!capture.Ready&&capture.Error?.Contains("could not capture")==true,"Missing virtual output is reported as capture failure instead of an unsupported NVIDIA encoder");
        check(StationStreaming.EncodingHealth(failure,"nvenc") is {Ready:false,Error:not null},"Reported NVENC initialization failure is detected before fallback can become ready");
        check(StationStreaming.EncodingHealth(failure+"\nFound H.264 encoder: h264_vulkan [vulkan]","nvenc") is {Ready:false,Error:not null},"A fallback encoder does not satisfy a workstation's requested NVENC encoder");
        check(StationStreaming.EncodingHealth("Found H.264 encoder: h264_vulkan [vulkan]","nvenc") is {Ready:false,Error:not null},"An unexpected encoder is rejected even without the preceding failure log");
        const string warnings="Error: Failed to gain CAP_SYS_ADMIN\nWarning: EGL: context priority set to HIGH but CAP_SYS_NICE capability is missing\n";
        check(StationStreaming.EncodingHealth(warnings+"Found H.264 encoder: h264_nvenc [nvenc]","nvenc") is {Ready:true,Error:null},"Capability warnings alone do not invalidate a successful NVENC startup");
        check(StationStreaming.EncodingHealth("OpenEncodeSessionEx failed: unsupported device (2)","nvenc") is {Ready:false,Error:null},"A single codec probe failure waits for the encoder's final result");
        check(StationStreaming.EncodingHealth("OpenEncodeSessionEx failed: unsupported device (2)\nFound H.264 encoder: h264_nvenc [nvenc]","nvenc") is {Ready:true,Error:null},"Earlier unsuccessful codec probes do not hide a final successful requested encoder");
        check(StationStreaming.EncodingHealth("Found H.264 encoder: libx264 [software]","software") is {Ready:true,Error:null},"The software encoder remains available for a software workstation");
        foreach(var result in new[]{"core-dump","signal","start-limit-hit","exit-code"})
            check(StationStreaming.EncodingHealth("Found H.264 encoder: h264_nvenc [nvenc]","nvenc",result) is {Ready:false,Error:not null},"A terminated Sunshine invocation cannot remain ready: "+result);
    }
}
