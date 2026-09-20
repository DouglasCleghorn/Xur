using System.Globalization;
using System.Text.RegularExpressions;
using Xur.Domain;
namespace Xur.Agent;
public static class NvLinkTopology
{
    public static async Task<GpuTopology> Observe(GpuDevice[]? inventory=null,Func<string,string[],int,Task<ProcessResult>>? run=null)
    {
        var now=DateTimeOffset.UtcNow;var probes=new List<GpuTopologyProbe>();var errors=new List<string>();
        run??=(exe,args,timeout)=>Processes.Run(exe,args,timeout);
        async Task<ProcessResult> Probe(string[] args)
        {
            ProcessResult r;
            try {r=await run("nvidia-smi",args,10);}
            catch(Exception e) when(e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {r=new(-1,"Probe failed: "+e.GetType().Name);}
            probes.Add(new(args,r.ExitCode,r.Output.Length>16384?r.Output[..16384]+"\n[truncated]":r.Output));return r;
        }
        try
        {
            inventory??=await GpuInventory.Observe(probeRuntime:false);
            var nvidia=inventory.Where(g=>g.Vendor=="NVIDIA").ToArray();
            if(nvidia.Length==0)return new(now,[],State:"NotApplicable");
            var mapping=await Probe(["--query-gpu=index,pci.bus_id","--format=csv,noheader,nounits"]);
            var matrix=await Probe(["topo","-m"]);
            var status=new Dictionary<string,string>();
            foreach(var gpu in nvidia)
            {
                var r=await Probe(["nvlink","--status","--id="+gpu.Pci]);
                if(r.ExitCode==0)status[GpuMonitor.Pci(gpu.Pci)]=r.Output;
            }
            GpuLink[] links=[];var observed=false;
            if(mapping.ExitCode!=0||matrix.ExitCode!=0)errors.Add($"NVIDIA topology query failed (GPU mapping: {mapping.ExitCode}; matrix: {matrix.ExitCode}).");
            else try
            {
                var ids=Mapping(mapping.Output);
                if(nvidia.Any(g=>!ids.Values.Contains(GpuMonitor.Pci(g.Pci))))throw new FormatException("GPU mapping omitted an observed NVIDIA card.");
                links=Parse(mapping.Output,matrix.Output,status);observed=true;
            }
            catch(FormatException e){errors.Add(e.Message);}
            // A bridge may be visible through remote link endpoints even when the
            // topology matrix is unavailable or does not label its pair as NV#.
            var peers=new Dictionary<string,string>();
            foreach(var gpu in nvidia.Where(g=>Speeds(status.GetValueOrDefault(GpuMonitor.Pci(g.Pci),"")).Length>0 && !links.Any(l=>l.Connection=="NVLink"&&(l.From==GpuMonitor.Pci(g.Pci)||l.To==GpuMonitor.Pci(g.Pci)))))
            {
                var r=await Probe(["nvlink","-p","--id="+gpu.Pci]);
                if(r.ExitCode==0)peers[GpuMonitor.Pci(gpu.Pci)]=r.Output;
            }
            foreach(var pair in PeerLinks(peers,status,nvidia.Select(g=>GpuMonitor.Pci(g.Pci)).ToHashSet()))
                links=links.Where(l=>l.From!=pair.From||l.To!=pair.To).Append(pair).ToArray();
            if(status.Any(p=>Speeds(p.Value).Length>0&&!links.Any(l=>l.Connection=="NVLink"&&(l.From==p.Key||l.To==p.Key))))
                errors.Add("NVLink activity is reported, but its peer GPU could not be resolved.");
            if(links.Any(l=>l.Connection=="NVLink"&&l.State=="Unknown"))errors.Add("An NVLink connection was found, but its current link state could not be read.");
            return new(now,links,errors.Count>0?string.Join(" ",errors)+" See Diagnostics for the driver output.":null,
                observed?"Observed":links.Any(l=>l.Connection=="NVLink")?"Partial":"Unknown",probes.ToArray());
        }
        catch(Exception e) when(e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {return new(now,[],"NVIDIA topology could not be observed. See Diagnostics for the driver output.",Probes:probes.ToArray());}
    }
    static Dictionary<string,string> Mapping(string mapping)
    {
        var ids=new Dictionary<string,string>();
        foreach(var line in mapping.Split('\n',StringSplitOptions.RemoveEmptyEntries))
        {
            var parts=line.Split(',',StringSplitOptions.TrimEntries);
            if(parts.Length!=2||!int.TryParse(parts[0],NumberStyles.None,CultureInfo.InvariantCulture,out var index)||!IsPci(parts[1]))throw new FormatException("NVIDIA GPU mapping was not recognized.");
            var pci=GpuMonitor.Pci(parts[1]);
            if(ids.ContainsValue(pci)||!ids.TryAdd("GPU"+index,pci))throw new FormatException("NVIDIA GPU mapping contains duplicate identities.");
        }
        if(ids.Count==0)throw new FormatException("NVIDIA GPU mapping was empty.");
        return ids;
    }
    public static GpuLink[] Parse(string mapping,string matrix,Dictionary<string,string> status)
    {
        var ids=Mapping(mapping);
        // nvidia-smi emits SGR styling even with redirected output. Strip it
        // only from the parser's copy; diagnostic probes keep the raw output.
        var plainMatrix=Regex.Replace(matrix,@"\x1B\[[0-9;:]*m","");
        var lines=plainMatrix.Split('\n').Select(l=>l.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries)).ToArray();
        var header=lines.FirstOrDefault(l=>l.Length>0&&ids.ContainsKey(l[0])&&!l.Contains("X")&&ids.Keys.All(l.Contains));
        if(header==null)throw new FormatException("NVIDIA topology matrix header was not recognized.");
        if(header.Count(ids.ContainsKey)!=ids.Count)throw new FormatException("NVIDIA topology matrix has duplicate columns.");
        var rows=new Dictionary<string,string[]>();
        foreach(var row in lines.Where(l=>l.Length>1&&ids.ContainsKey(l[0])&&l.Contains("X")))
            if(!rows.TryAdd(row[0],row))throw new FormatException("NVIDIA topology matrix has duplicate rows.");
        string Cell(string from,string to)
        {
            var column=Array.IndexOf(header,to)+1;
            if(!rows.TryGetValue(from,out var row)||column>=row.Length)throw new FormatException("NVIDIA topology matrix was incomplete.");
            return row[column];
        }
        foreach(var id in ids.Keys)if(Cell(id,id)!="X")throw new FormatException("NVIDIA topology matrix identities did not align.");
        var result=new List<GpuLink>();
        foreach(var a in ids)foreach(var b in ids)
        {
            if(string.CompareOrdinal(a.Value,b.Value)>=0)continue;
            var connection=Cell(a.Key,b.Key);
            if(connection!=Cell(b.Key,a.Key))throw new FormatException("NVIDIA topology matrix reported inconsistent connections.");
            var nv=Regex.Match(connection,@"^NV([1-9][0-9]*)$");
            if(!nv.Success&&connection is not ("SYS" or "NODE" or "PHB" or "PXB" or "PIX"))throw new FormatException("NVIDIA topology matrix connection was not recognized: "+connection);
            if(nv.Success)
            {
                if(!int.TryParse(nv.Groups[1].Value,out var count))throw new FormatException("Invalid NVIDIA topology link count.");
                result.Add(Link(a.Value,b.Value,count,status));
            }
            else result.Add(new(a.Value,b.Value,"PCIe",null,null,connection));
        }
        return result.ToArray();
    }
    static GpuLink Link(string from,string to,int count,Dictionary<string,string> status)
    {
        var a=Speeds(status.GetValueOrDefault(from,""));var b=Speeds(status.GetValueOrDefault(to,""));
        var state=a.Length>=count&&b.Length>=count?"Active":Inactive(status.GetValueOrDefault(from,""))>=count&&Inactive(status.GetValueOrDefault(to,""))>=count?"Inactive":"Unknown";
        double? speed=state=="Active"&&a.Distinct().Count()==1&&b.Distinct().Count()==1&&a[0]==b[0]?a[0]:null;
        return new(from,to,state,count,speed,"NVLink");
    }
    static IEnumerable<GpuLink> PeerLinks(Dictionary<string,string> peers,Dictionary<string,string> status,HashSet<string> known)
    {
        Dictionary<string,int[]> Remote(string text)=>Regex.Matches(text,@"(?im)^\s*Link\s+(\d+):[^\r\n]*?((?:0000)?[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7])\b")
            .Where(m=>int.TryParse(m.Groups[1].Value,out _)).GroupBy(m=>GpuMonitor.Pci(m.Groups[2].Value)).ToDictionary(g=>g.Key,g=>g.Select(m=>int.Parse(m.Groups[1].Value)).Distinct().ToArray());
        var all=peers.ToDictionary(p=>p.Key,p=>Remote(p.Value));
        foreach(var a in all)foreach(var b in a.Value)
        {
            if(!known.Contains(b.Key)||string.CompareOrdinal(a.Key,b.Key)>=0||!all.TryGetValue(b.Key,out var reverse)||!reverse.TryGetValue(a.Key,out var back)||back.Length!=b.Value.Length)continue;
            // Require reciprocal endpoints and activity on those exact link IDs.
            bool Active(string pci,int[] ids)=>ids.All(id=>Regex.IsMatch(status.GetValueOrDefault(pci,""),@"(?m)^\s*Link\s+"+id+@":\s*[0-9]+(?:\.[0-9]+)?\s*GB/s\b"));
            if(Active(a.Key,b.Value)&&Active(b.Key,back))yield return Link(a.Key,b.Key,b.Value.Length,status);
        }
    }
    static bool IsPci(string value)=>Regex.IsMatch(value,@"^(?:0000)?[0-9a-fA-F]{4}:[0-9a-fA-F]{2}:[0-9a-fA-F]{2}\.[0-7]$");
    static int Inactive(string text)=>Regex.Matches(text,@"(?im)^\s*Link\s+\d+:\s*[<(]?inactive[>)]?\s*$").Count;
    static double[] Speeds(string text)=>Regex.Matches(text,@"(?im)^\s*Link\s+\d+:\s*([0-9]+(?:\.[0-9]+)?)\s*GB/s\b")
        .Select(m=>double.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture)).Where(n=>n>0).ToArray();
}
