using Xur.Agent;
using Xur.Domain;
static class TimezoneTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var dir=Path.Combine(Path.GetTempPath(),"xur-zone-test-"+Guid.NewGuid());
        var current="UTC";var calls=new List<string[]>();bool fail=false;
        Task<ProcessResult> Run(string exe,string[] args,int timeout)
        {
            calls.Add(args);
            if(args[0]=="list-timezones")return Task.FromResult(new ProcessResult(0,"America/Denver\nAsia/Kolkata\nUTC\n"));
            if(args[0]=="show")return Task.FromResult(new ProcessResult(0,current));
            if(fail)return Task.FromResult(new ProcessResult(1,"Operation failed"));
            current=args[1];return Task.FromResult(new ProcessResult(0,""));
        }
        try
        {
            var settings=new TimezoneSettings(dir,Run);var result=await settings.Set("Asia/Kolkata");
            check(result.Current=="Asia/Kolkata"&&File.ReadAllText(Path.Combine(dir,"timezone")).Trim()==result.Current,"Global timezone is applied and saved for installer handoff");
            check((await new TimezoneSettings(dir,Run).Read()).Current==result.Current,"A new settings instance reads the persistent system timezone");
            foreach(var bad in new[]{"../../etc/passwd","UTC;reboot","Unknown/Zone","","UTC\nAmerica/Denver"})
            {
                var before=calls.Count(c=>c[0]=="set-timezone");bool rejected=false;
                try{await settings.Set(bad);}catch(InvalidOperationException){rejected=true;}
                check(rejected&&calls.Count(c=>c[0]=="set-timezone")==before,"Unknown or malformed timezone never reaches timedatectl: "+bad.Replace('\n',' '));
            }
            fail=true;try{await settings.Set("UTC");throw new Exception("Failure accepted");}catch(InvalidOperationException){}
            check(File.ReadAllText(Path.Combine(dir,"timezone")).Trim()=="Asia/Kolkata","Failed timezone change does not overwrite saved selection");
            check(TimezoneSettings.ContainerArguments().Contains("--tz=local")&&TimezoneSettings.ContainerArguments().Contains("--env=TZ=:/etc/localtime"),"Model and generic container timezone uses host zone data even without image tzdata");
            var gpu=new GpuDevice("0000:c6:00.0","NVIDIA","RTX 3090","nvidia","GPU-test",24576,[],[]);
            check(StationGraphics.LaunchEnvironment(gpu).SequenceEqual(new[]{"__GLX_VENDOR_LIBRARY_NAME=nvidia"}),"NVIDIA GLX selects its vendor without assuming GPU index or changing Vulkan driver paths");
            check(StationGraphics.LaunchEnvironment(gpu with {Vendor="AMD"}).Single()=="DRI_PRIME=pci-0000_c6_00_0!","Mesa workstation selects exact PCI device rather than ordinal");
            check(StationGraphics.LaunchEnvironment(gpu with {Vendor="Unknown",Pci="vmbus:test"}).Length==0,"Virtual graphics does not receive fabricated PCI selection");
            var environment=StationGraphics.SelectEnvironment("DISPLAY=:1\nHF_TOKEN=private\nWAYLAND_DISPLAY=wayland-0\nTZ=:/etc/localtime\nSECRET_KEY=private\nLD_PRELOAD=untrusted");
            check(environment.Count==3&&!environment.Values.Contains("private"),"Graphics diagnostics copy only allowlisted display variables, not credentials or preload hooks");
        }
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
}
