using System.Text.Json;
using Xur.Agent;
using Xur.Domain;
static class WorkloadSettingsSmoke
{
    public static async Task Run()
    {
        const string root="/run/xur-vm-settings-test";
        if(!File.Exists(root+"/disposable"))throw new InvalidOperationException("Requires an explicitly marked disposable test VM.");
        var settings=new TimezoneSettings(root);var previous=(await settings.Read()).Current;
        var container="xur-timezone-smoke-"+Guid.NewGuid().ToString("N")[..10];
        try
        {
            await settings.Set("Asia/Kolkata");
            var native=await Processes.Run("env",[TimezoneSettings.StationEnvironment,"date","+%z"],5);
            if(native.ExitCode!=0||native.Output.Trim()!="+0530")throw new Exception("Native workload timezone mismatch");
            var images=await Processes.Run("podman",["images","--format={{.ID}} {{.Repository}}"],10);
            var image=images.Output.Split('\n').FirstOrDefault(l=>l.EndsWith(" ghcr.io/ggml-org/llama.cpp"))?.Split(' ')[0]??throw new Exception("Needs a previously downloaded llama.cpp image");
            var created=await Processes.Run("podman",["run","--name",container,"--pull=never",..TimezoneSettings.ContainerArguments(),"--entrypoint=/bin/date",image,"+%z"],30);
            if(created.ExitCode!=0||created.Output.Trim()!="+0530")throw new Exception("Container timezone mismatch: "+created.Output);
            var again=await Processes.Run("podman",["start","--attach",container],30);
            if(again.ExitCode!=0||again.Output.Trim()!="+0530")throw new Exception("Container lost timezone on restart");
            if((await new TimezoneSettings(root).Read()).Current!="Asia/Kolkata")throw new Exception("Timezone did not persist");
            var definition=Directory.GetFiles("/var/lib/xur/workloads","*.json").Select(p=>JsonSerializer.Deserialize<Workload>(File.ReadAllText(p))).First(w=>w?.Recipe.Kind=="Workstation")!;
            var gpu=(await GpuInventory.Observe()).Single(g=>definition.Gpus.Contains(g.Pci));
            var graphics=await StationGraphics.Collect(definition,gpu);
            using var report=JsonDocument.Parse(JsonSerializer.Serialize(graphics));
            foreach(var name in new[]{"vulkan","openGl"}) {
                var probe=report.RootElement.GetProperty("probes").GetProperty(name);
                if(probe.GetProperty("exitCode").GetInt32()!=0)throw new Exception(name+" graphics probe failed: "+probe.GetProperty("output").GetString());
            }
            Console.WriteLine(JsonSerializer.Serialize(new{result="Passed",hostTimezoneApplied=true,nativeWorkloadTimezone=true,containerTimezone=true,containerRestartPreserved=true,graphics}));
        }
        finally{await settings.Set(previous);await Processes.Run("podman",["rm","--force",container],15);}
    }
}
