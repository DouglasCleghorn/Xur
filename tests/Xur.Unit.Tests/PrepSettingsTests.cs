using System.Net;
using System.IO.Compression;
using System.Text.Json;
using Xur.Agent;
using Xur.Control;
using Xur.Domain;
static class PrepSettingsTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"xur-prep-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try
        {
            var zone="UTC";var http=new Reply();using var client=new HttpClient(http);
            Task<ProcessResult> Clock(string exe,string[] args,int timeout){if(args[0]=="list-timezones")return Task.FromResult(new ProcessResult(0,"UTC\nAmerica/Denver\n"));if(args[0]=="set-timezone")zone=args[1];return Task.FromResult(new ProcessResult(0,zone));}
            var settings=new TimezoneSettings(root,Clock,client);
            check((await settings.Read()).Automatic,"New installation defaults to automatic timezone");
            var result=await settings.Refresh();check(result.Current=="America/Denver"&&result.RefreshedAt!=null&&http.Request=="https://ipwho.is/","Timezone refresh applies validated IP timezone and records freshness");
            http.Json="{\"success\":true,\"timezone\":{\"id\":\"../../etc/passwd\"}}";
            try{await settings.Refresh();check(false,"Invalid timezone rejected");}catch(InvalidOperationException){}
            check(zone=="America/Denver"&&(await settings.Read()).Error!=null,"Invalid geolocation keeps the last timezone and reports the lookup failure");
            await settings.Set("UTC");http.Json="broken";
            await settings.Initialize(CancellationToken.None);
            check(zone=="UTC"&&!(await new TimezoneSettings(root,Clock,client).Read()).Automatic,"Manual timezone survives restart without making an automatic lookup");
            try{await settings.Set(new TimezoneRequest(Automatic:true));}catch(InvalidOperationException){}
            check(zone=="UTC"&&(await settings.Read()).Automatic,"Failed automatic lookup preserves current zone and automatic preference for a later retry");
            File.WriteAllText(root+"/gpu-power.json","{\"GPU-test\":250}");File.WriteAllText(root+"/api-keys.json","{\"secret\":\"DO-NOT-EXPORT\"}");File.WriteAllText(root+"/account.json","{\"secret\":\"DO-NOT-EXPORT\"}");
            var exported=ConfigExport.Read(root,root+"/absent");
            check(exported.Count==1&&exported.ContainsKey("gpu-power.json"),"Configuration export allowlist excludes account and API credentials");
            using var host=JsonDocument.Parse(JsonSerializer.Serialize(exported));
            var station=new StationDefinition("desk-1","Desktop",new StationUser("person",1000));
            var profile=new Profile("profile-1","My profile",1,[]);
            var backup=ConfigBackup.Create([profile],host.RootElement,[station]);
            using var archive=new ZipArchive(new MemoryStream(backup),ZipArchiveMode.Read);
            check(archive.Entries.Count==1&&archive.Entries[0].FullName=="xur-config.json","Configuration backup is a ZIP containing readable JSON");
            var entry=archive.Entries[0];using var reader=new StreamReader(entry.Open());var json=await reader.ReadToEndAsync();using var restored=JsonDocument.Parse(json);
            check(entry.CompressedLength<entry.Length,"Configuration JSON is compressed in the download");
            check(restored.RootElement.GetProperty("profiles")[0].GetProperty("name").GetString()==profile.Name&&restored.RootElement.GetProperty("workstations")[0].GetProperty("id").GetString()==station.Id&&restored.RootElement.GetProperty("host").GetProperty("gpu-power.json").GetProperty("GPU-test").GetInt32()==250,"ZIP round trip preserves profiles, workstation identities and settings outside the database");
            check(!json.Contains("DO-NOT-EXPORT"),"Decompressed backup excludes account and API credentials");
            var link=ConsoleQr.LoginUrl("https://xur.example.ts.net/",false,"ABC-DEF");
            check(link=="https://xur.example.ts.net/login#code=ABC-DEF"&&new Uri(link).Query=="","Initial console QR keeps the access code out of HTTP requests");
            check(ConsoleQr.LoginUrl("https://xur.example.ts.net/",true,"ABC-DEF")=="https://xur.example.ts.net/login","Configured account QR contains only login URL");
            check(ConsoleQr.LoginUrl("https://xur.example.ts.net/",false,"Expired; reboot to generate a new code")=="https://xur.example.ts.net/login","Expired bootstrap messages never enter QR credentials");
            var rows=ConsoleQr.Rows(link);check(rows.All(r=>r.Length==rows[0].Length)&&rows[0].All(c=>c=='█'),"Terminal QR retains rectangular modules and a light quiet zone");
            var frame=LocalConsole.Clean(LocalConsole.Frame("Status","Ready",120,45,statusQr:rows));check(rows.All(frame.Contains),"Status dashboard includes the complete QR without wrapping its rows");
            await Trim(root,check);
        }
        finally{Directory.Delete(root,true);}
    }
    sealed class Reply:HttpMessageHandler
    {
        public string Json="{\"success\":true,\"timezone\":{\"id\":\"America/Denver\"}}";public string? Request;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Request=request.RequestUri!.AbsoluteUri;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Json)});}
    }
    static async Task Trim(string root,Action<bool,string> check)
    {
        var mounts="""{"filesystems":[{"source":"/dev/nvme0n1p1","target":"/var","fstype":"ext4","size":1000,"used":400,"avail":500,"options":"rw","maj:min":"259:1"},{"source":"/dev/sda1","target":"/archive","fstype":"ext4","size":2000,"used":1000,"avail":900,"options":"rw","maj:min":"8:1"},{"source":"/dev/nvme0n1p2","target":"/boot","fstype":"ext4","size":1000,"used":400,"avail":500,"options":"ro","maj:min":"259:2"}]}""";
        var disks="""{"blockdevices":[{"path":"/dev/nvme0n1p1","uuid":"ssd-a","rota":false,"disc-max":1000000,"maj:min":"259:1"},{"path":"/dev/sda1","uuid":"hdd-a","rota":true,"disc-max":0,"maj:min":"8:1"},{"path":"/dev/nvme0n1p2","uuid":"ssd-b","rota":0,"disc-max":1000000,"maj:min":"259:2"}]}""";
        var parsed=StorageMounts.Parse(mounts,disks);check(parsed.Single(m=>m.Path=="/var").TrimSupported&&!parsed.Single(m=>m.Path=="/boot").TrimSupported&&!parsed.Single(m=>m.Path=="/archive").Ssd,"TRIM detects SSD discard support and excludes read-only/HDD mounts");
        var trimmed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int calls=0;string[]? command=null;
        async Task<ProcessResult> Run(string exe,string[] args,int timeout)
        {
            if(exe=="findmnt")return new(0,mounts);if(exe=="lsblk")return new(0,disks);
            if(exe=="fstrim"){Interlocked.Increment(ref calls);command=args;await trimmed.Task;return new(0,"/var: 500 bytes trimmed");}return new(0,"ActiveState=active");
        }
        var service=new StorageTrim(root,Run);
        foreach(var id in new[]{"../../dev/sda",parsed.Single(m=>m.Path=="/archive").Id,parsed.Single(m=>m.Path=="/boot").Id})
        {try{await service.Start(id);check(false,"Unsafe trim target rejected");}catch(InvalidOperationException){}}
        check(calls==0,"Invalid or ineligible TRIM requests never execute fstrim");
        await service.Start(parsed.Single(m=>m.Path=="/var").Id);
        try{await service.Start(parsed.Single(m=>m.Path=="/var").Id);check(false,"Duplicate trim rejected");}catch(InvalidOperationException){}
        trimmed.SetResult();for(int i=0;i<200&&(await service.Read()).Busy;i++)await Task.Delay(5);
        check(command!.SequenceEqual(new[]{"--verbose","--","/var"})&&calls==1,"TRIM runs once with an observed mount and explicit argument boundary");
        check((await new StorageTrim(root,Run).Read()).Results is [{Success:true,FinishedAt:not null}],"TRIM result and completion survive service restart");
        var observations=0;var unsafeCalls=0;
        Task<ProcessResult> Changed(string exe,string[] args,int timeout)
        {
            if(exe=="findmnt")return Task.FromResult(new ProcessResult(0,mounts));
            if(exe=="lsblk")return Task.FromResult(new ProcessResult(0,++observations==1?disks:disks.Replace("ssd-a","replacement")));
            if(exe=="fstrim")unsafeCalls++;
            return Task.FromResult(new ProcessResult(0,""));
        }
        var changed=new StorageTrim(root+"/changed",Changed);await changed.Start(parsed.Single(m=>m.Path=="/var").Id);
        for(int i=0;i<200&&(await changed.Read()).Busy;i++)await Task.Delay(5);
        check(unsafeCalls==0&&(await changed.Read()).Results.Single().Success==false,"Replaced filesystem identity blocks a queued TRIM before execution");
    }
}
