using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xur.Domain;
namespace Xur.Agent;

public sealed class GpuPower
{
    readonly string directory,sys;
    readonly Func<Task<GpuDevice[]>> inventory;
    readonly Func<string,string[],int,Task<ProcessResult>> run;
    readonly SemaphoreSlim gate=new(1,1);
    readonly Dictionary<string,string> errors=[];
    Dictionary<string,double?> saved=[];
    string? configurationError;
    record Probe(GpuPowerCard Card,string? CapPath);
    public GpuPower(string directory="/var/lib/xur",string sys="/sys",Func<Task<GpuDevice[]>>? inventory=null,Func<string,string[],int,Task<ProcessResult>>? run=null)
    {
        this.directory=directory;this.sys=sys;this.inventory=inventory??(()=>GpuInventory.Observe());this.run=run??((exe,args,timeout)=>Processes.Run(exe,args,timeout));
        var path=Path.Combine(directory,"gpu-power.json");
        try{if(File.Exists(path))saved=JsonSerializer.Deserialize<Dictionary<string,double?>>(File.ReadAllText(path))??[];}
        catch(Exception e) when(e is JsonException or IOException or UnauthorizedAccessException){configurationError="Saved GPU power settings could not be read. Restore gpu-power.json before changing limits.";}
    }
    static double? Number(string value)=>double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out var n)&&double.IsFinite(n)&&n>=0?n:null;
    static string Read(string path){try{return File.ReadAllText(path).Trim();}catch{return "";}}
    async Task<Probe> Observe(GpuDevice g)
    {
        string id=g.Vendor+":"+g.Pci, message="This driver does not expose an adjustable GPU power limit.";
        double? current=null,min=null,max=null,normal=null;string? cap=null;bool intelSustained=false;
        try
        {
            if(g.Vendor=="NVIDIA")
            {
                var r=await run("nvidia-smi",["--id="+g.Pci,"--query-gpu=uuid,power.limit,power.default_limit,power.min_limit,power.max_limit","--format=csv,noheader,nounits"],10);
                var rows=r.Output.Trim().Split('\n');var f=rows[0].Split(',',StringSplitOptions.TrimEntries);
                if(r.ExitCode==0 && rows.Length==1 && f.Length==5 && f[0].StartsWith("GPU-",StringComparison.Ordinal))
                {id="NVIDIA:"+f[0];current=Number(f[1]);normal=Number(f[2]);min=Number(f[3]);max=Number(f[4]);}
            }
            else if(g.Vendor is "AMD" or "Intel" && Regex.IsMatch(g.Pci,@"^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$"))
            {
                var device=sys+"/bus/pci/devices/"+g.Pci;
                var unique=Read(device+"/unique_id");if(unique.TrimStart('0','x').Length==0)unique="";
                id=g.Vendor+":"+(unique.Length>0?unique:g.Pci+":"+Read(device+"/vendor")+":"+Read(device+"/device"));
                // Only GPU-local hwmon caps. Never substitute CPU/package RAPL
                // controls for an integrated GPU that has no independent cap.
                var sensors=Directory.Exists(device+"/hwmon")?Directory.GetDirectories(device+"/hwmon"):[];
                var field=g.Vendor=="Intel"?"power1_max":"power1_cap";
                var matches=sensors.Where(p=>(g.Vendor=="AMD"?Read(p+"/name")=="amdgpu":Read(p+"/name") is "i915" or "xe") && File.Exists(p+"/"+field)).ToArray();
                if(matches.Length==1)
                {
                    var p=matches[0];cap=p+"/"+field;
                    double? Watts(string field)=>Number(Read(p+"/"+field))/1000000;
                    current=Watts(field);
                    if(g.Vendor=="Intel")
                    {
                        // Intel's PL1 sustained limit is power1_max. power1_cap
                        // is a separate burst limit on xe and must not be used.
                        intelSustained=true;normal=Watts("power1_rated_max");max=normal;
                    }
                    else {min=Watts("power1_cap_min");max=Watts("power1_cap_max");normal=Watts("power1_cap_default");}
                    // Missing bounds/default remain unavailable instead of guessed.
                    if((File.GetUnixFileMode(cap)&UnixFileMode.UserWrite)==0)cap=null;
                }
            }
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidOperationException){message="Could not read the driver's power controls.";}
        bool supported=current!=null&&(min!=null||intelSustained)&&max!=null&&normal!=null&&max>0&&(min??0)<=max&&normal>=(min??0)&&normal<=max&&(g.Vendor=="NVIDIA"||cap!=null);
        return new(new(g.Pci,id,g.Name,g.Vendor,current,min,max,normal,saved.TryGetValue(id,out var wanted)?wanted:null,supported&&configurationError==null,configurationError??errors.GetValueOrDefault(id)??(supported?(intelSustained?"Sustained limit (PL1). Xur caps it at the card’s rated default; the driver validates lower values.":null):message),saved.ContainsKey(id)),cap);
    }
    async Task<Probe[]> Probes()
    {
        var result=new List<Probe>();foreach(var gpu in await inventory())result.Add(await Observe(gpu));
        var duplicates=result.GroupBy(p=>p.Card.Identity).Where(g=>g.Count()>1).Select(g=>g.Key).ToHashSet();
        return result.Select(p=>duplicates.Contains(p.Card.Identity)?p with{Card=p.Card with{CanChange=false,Message="Duplicate GPU identity; power changes are disabled."}}:p).ToArray();
    }
    public async Task<GpuPowerSnapshot> Status()
    {await gate.WaitAsync();try{return new((await Probes()).Select(p=>p.Card).ToArray());}finally{gate.Release();}}
    static void Validate(Probe p,double watts)
    {if(!p.Card.CanChange||!double.IsFinite(watts)||watts<=0||watts<(p.Card.MinimumWatts??1)||watts>p.Card.MaximumWatts)throw new InvalidOperationException("Choose a power limit within the driver's supported range.");}
    async Task Apply(Probe p,double watts)
    {
        Validate(p,watts);
        if(p.Card.Vendor=="NVIDIA")
        {
            var uuid=p.Card.Identity["NVIDIA:".Length..];
            var r=await run("nvidia-smi",["--id="+uuid,"--power-limit="+watts.ToString(CultureInfo.InvariantCulture)],10);
            if(r.ExitCode!=0)throw new InvalidOperationException("The NVIDIA driver rejected the power limit.");
        }
        else await File.WriteAllTextAsync(p.CapPath!,Math.Round(watts*1000000).ToString(CultureInfo.InvariantCulture));
        var after=(await Probes()).SingleOrDefault(x=>x.Card.Identity==p.Card.Identity&&x.Card.Pci==p.Card.Pci);
        if(after?.Card.CurrentWatts is not {} actual||Math.Abs(actual-watts)>.1)throw new InvalidOperationException("The driver did not confirm the requested power limit.");
    }
    public async Task Set(GpuPowerRequest request)
    {
        await gate.WaitAsync();try
        {
            if(configurationError!=null)throw new InvalidOperationException(configurationError);
            var p=(await Probes()).SingleOrDefault(p=>p.Card.Pci==request.Pci&&p.Card.Identity==request.Identity)??throw new InvalidOperationException("GPU identity changed. Refresh the page.");
            var watts=request.Watts??p.Card.DefaultWatts??throw new InvalidOperationException("The default power limit is unavailable.");
            Validate(p,watts);
            var next=new Dictionary<string,double?>(saved);
            next[p.Card.Identity]=request.Watts;
            // Write the durable intent first. A crash is recovered by the next
            // reconciliation; driver errors remain visible and are retried.
            Directory.CreateDirectory(directory);var path=Path.Combine(directory,"gpu-power.json");
            using(var f=new FileStream(path+".tmp",FileMode.Create,FileAccess.Write)){JsonSerializer.Serialize(f,next);f.Flush(true);}
            File.SetUnixFileMode(path+".tmp",UnixFileMode.UserRead|UnixFileMode.UserWrite);File.Move(path+".tmp",path,true);saved=next;
            try{await Apply(p,watts);errors.Remove(p.Card.Identity);}catch{errors[p.Card.Identity]="Saved, but the driver has not applied the limit. Check the GPU and try again.";throw;}
        }finally{gate.Release();}
    }
    public async Task Reconcile()
    {
        await gate.WaitAsync();try
        {
            foreach(var p in await Probes())if(saved.TryGetValue(p.Card.Identity,out var desired))
            {
                try{var watts=desired??p.Card.DefaultWatts??throw new InvalidOperationException("Default limit unavailable");Validate(p,watts);if(p.Card.CurrentWatts is not {} actual||Math.Abs(actual-watts)>.1)await Apply(p,watts);errors.Remove(p.Card.Identity);}
                catch(Exception e) when(e is IOException or InvalidOperationException or UnauthorizedAccessException){errors[p.Card.Identity]="Saved limit could not be restored. Check the driver and supported range.";}
            }
        }finally{gate.Release();}
    }
    public async Task Run(CancellationToken stop)
    {
        while(!stop.IsCancellationRequested)
        {
            try{await Reconcile();}catch(Exception e) when(e is IOException or InvalidOperationException or UnauthorizedAccessException){}
            try{await Task.Delay(TimeSpan.FromSeconds(30),stop);}catch(OperationCanceledException){break;}
        }
    }
}
