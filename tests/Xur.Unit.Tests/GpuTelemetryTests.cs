using Xur.Agent;
using Xur.Domain;
static class GpuTelemetryTests
{
    public static void Run(Action<bool,string> check)
    {
        var at=DateTimeOffset.UtcNow;
        var xml="""
            <?xml version="1.0"?><!DOCTYPE nvidia_smi_log SYSTEM "nvsmi_device_v12.dtd"><nvidia_smi_log><driver_version>test-driver</driver_version><gpu><pci><pci_bus_id>00000000:01:00.0</pci_bus_id></pci><fb_memory_usage><total>24576 MiB</total><used>12288 MiB</used><free>12288 MiB</free></fb_memory_usage><utilization><gpu_util>45 %</gpu_util></utilization><temperature><gpu_temp>62 C</gpu_temp></temperature><power_readings><power_draw>213.5 W</power_draw><power_limit>350 W</power_limit></power_readings><fan_speed>30 %</fan_speed><performance_state>P2</performance_state><clocks><graphics_clock>1800 MHz</graphics_clock><mem_clock>9501 MHz</mem_clock></clocks><processes><process_info><pid>999999</pid><process_name>python --token=not-to-be-collected</process_name><used_memory>12000 MiB</used_memory></process_info></processes></gpu></nvidia_smi_log>
            """;
        var entry=GpuMonitor.ParseNvidia(xml,at)["0000:01:00.0"];
        check(entry.Reading is {MemoryUsedMiB:12288,MemoryTotalMiB:24576,Utilization:45,PowerWatts:213.5,PowerLimitWatts:350,TemperatureC:62,GraphicsClockMHz:1800},"NVIDIA XML maps readings to stable PCI identity with numeric units");
        check(entry.Processes is [{Pid:999999,MemoryMiB:12000}] && !entry.Processes[0].Name.Contains("token"),"GPU process inventory collects PID and memory without upstream command arguments");
        var newer=xml.Replace("power_readings","gpu_power_readings").Replace("power_draw","instant_power_draw").Replace("power_limit","current_power_limit");
        check(GpuMonitor.ParseNvidia(newer,at)["0000:01:00.0"].Reading.PowerWatts==213.5,"GPU telemetry handles newer NVIDIA power XML fields");
        check(GpuMonitor.Number("N/A")==null && GpuMonitor.Number("[Not Supported]")==null && GpuMonitor.Number("NaN")==null && GpuMonitor.Number("0 W")==0,"Missing GPU telemetry stays unknown while a real zero stays zero");
        var root=Path.Combine(Path.GetTempPath(),"xur-gpu-sensors-"+Guid.NewGuid().ToString("N"));var device=root+"/bus/pci/devices/0000:02:00.0";
        try{
            Directory.CreateDirectory(device+"/hwmon/hwmon1");
            foreach(var pair in new Dictionary<string,string>{{"mem_info_vram_total","8589934592"},{"mem_info_vram_used","1073741824"},{"gpu_busy_percent","17"},{"hwmon/hwmon1/temp1_input","54000"},{"hwmon/hwmon1/power1_average","95000000"},{"hwmon/hwmon1/power1_cap","150000000"},{"hwmon/hwmon1/fan1_input","1100"}})File.WriteAllText(device+"/"+pair.Key,pair.Value);
            var gpu=new GpuDevice("0000:02:00.0","AMD","Test","amdgpu","",8192,[],[]);var sample=GpuMonitor.ReadSysfs(gpu,at,root);
            check(sample is {Utilization:17,MemoryTotalMiB:8192,MemoryUsedMiB:1024,MemoryFreeMiB:7168,TemperatureC:54,PowerWatts:95,PowerLimitWatts:150,FanRpm:1100},"Native sysfs sensor units convert bytes, millidegrees and microwatts correctly");
            check(GpuMonitor.ReadSysfs(gpu with{Pci="0000:03:00.0"},at,root) is {Utilization:null,MemoryUsedMiB:null,PowerWatts:null},"Virtual or unsupported GPUs never get invented telemetry");
        }finally{Directory.Delete(root,true);}
    }
}
