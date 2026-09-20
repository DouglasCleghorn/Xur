using Xur.Agent;
using Xur.Domain;

static class NvLinkTopologyTests
{
    public static async Task Run(Action<bool,string> check)
    {
        await FormattedDriverOutput(check);
        const string a="0000:01:00.0",b="0000:41:00.0";
        var inventory=new[]{a,b}.Select(p=>new GpuDevice(p,"NVIDIA","RTX 3090","nvidia","",24576,[],[])).ToArray();
        const string mapping="0, 00000000:41:00.0\n1, 00000000:01:00.0\n";
        const string matrix="\tGPU0\tGPU1\tNIC0\tCPU Affinity\tNUMA Affinity\nGPU0\tX\tNV2\tPHB\t0-31\t0\nGPU1\tNV2\tX\tSYS\t32-63\t1\nNIC0\tPHB\tSYS\tX\n\nLegend:\n  NV# = Connection traversing a bonded set of # NVLinks\n";
        const string active="GPU 0: NVIDIA GeForce RTX 3090 (UUID: GPU-example)\n Link 0: 14.062 GB/s\n Link 1: 14.062 GB/s\n";
        var map=mapping;var topo=matrix;var state=active;var failMatrix=false;var throwProbe=false;var reciprocal=true;var calls=new List<string>();
        Task<ProcessResult> Run(string exe,string[] args,int timeout)
        {
            calls.Add(string.Join(' ',args));
            if(throwProbe)throw new IOException("driver unavailable");
            if(args[0].StartsWith("--query-gpu"))return Task.FromResult(new ProcessResult(0,map));
            if(args[0]=="topo")return Task.FromResult(new ProcessResult(failMatrix?9:0,topo));
            if(args[1]=="--status")return Task.FromResult(new ProcessResult(0,state));
            if(args[1]=="-p")
            {
                var pci=args[2][5..];var peer=pci==a?b:a;
                return Task.FromResult(new ProcessResult(0,!reciprocal&&pci==b?"":"Link 0: Remote PCI Bus ID: "+peer+"\nLink 1: Remote PCI Bus ID: "+peer));
            }
            throw new Exception("Unexpected command: "+string.Join(' ',args));
        }
        Task<GpuTopology> Observe()=>NvLinkTopology.Observe(inventory,Run);
        var result=await Observe();
        check(result is {State:"Observed",Error:null}&&result.Links.Single() is {From:a,To:b,Connection:"NVLink",State:"Active",LinkCount:2,SpeedGBps:14.062},"Tab-separated NVIDIA matrix with NIC columns resolves NVLink by PCI identity");
        check(result.Probes?.Length==4&&result.Probes.Any(p=>p.Output==matrix),"Copyable topology diagnostics retain actual fixed-command output");
        state="Link 0: <inactive>\nLink 1: <inactive>";result=await Observe();
        check(result.Error==null&&result.Links.Single().State=="Inactive","Explicit inactive NVIDIA links remain visible as an inactive pair");
        state="Status unavailable";result=await Observe();
        check(result.Error!=null&&result.Links.Single().State=="Unknown","Unrecognized NVLink status is unknown rather than inactive");
        state=active;failMatrix=true;topo="Unable to read topology";result=await Observe();
        check(result.State=="Partial"&&result.Error!=null&&result.Links.Single() is {From:a,To:b,State:"Active",LinkCount:2},"Reciprocal remote PCI endpoints recover the real NVLink pair when matrix query fails");
        reciprocal=false;result=await Observe();
        check(result.State=="Unknown"&&result.Links.Length==0&&result.Error!.Contains("peer GPU"),"One-sided endpoint evidence never invents an NVLink pair");
        reciprocal=true;failMatrix=false;topo=matrix.Replace("NV2","SYS");result=await Observe();
        check(result.Links.Length==1&&result.Links[0].Connection=="NVLink","Remote endpoints supersede a matrix PCIe edge when both endpoints report active NVLink");
        state="No NVLink available";result=await Observe();
        check(result.State=="Observed"&&result.Error==null&&result.Links.Single() is {State:"PCIe",Connection:"SYS"},"A valid PCIe-only observation is distinguished from a topology read failure");
        foreach(var broken in new[]{"",matrix.Replace("GPU1\tNV2\tX","GPU1\tSYS\tX"),matrix.Replace("GPU1\tNV2\tX\tSYS\t32-63\t1\n",""),matrix+"GPU0\tX\tNV2\tPHB\n"})
        {
            topo=broken;result=await Observe();
            check(result.State=="Unknown"&&result.Error!=null,"Invalid or incomplete matrix reports unknown topology");
        }
        topo=matrix;
        foreach(var broken in new[]{"","0, N/A","0, 0000:41:00.0\n0, 0000:01:00.0","0, 0000:41:00.0"})
        {
            map=broken;result=await Observe();
            check(result.State=="Unknown"&&result.Error!=null,"Missing or ambiguous GPU mapping reports unknown topology");
        }
        throwProbe=true;result=await Observe();
        check(result.State=="Unknown"&&result.Probes!.All(p=>p.ExitCode==-1),"Driver execution failures preserve diagnostic failures without claiming absent NVLink");
        calls.Clear();result=await NvLinkTopology.Observe([],Run);
        check(result.State=="NotApplicable"&&calls.Count==0,"Non-NVIDIA inventory skips NVIDIA topology probes");
    }
    static async Task FormattedDriverOutput(Action<bool,string> check)
    {
        // Header and cells from the owner's 2026-09-16 diagnostic report.
        const string mapping="0, 00000000:01:00.0\n1, 00000000:41:00.0\n2, 00000000:81:00.0\n3, 00000000:C6:00.0\n";
        const string matrix="\t\u001b[4mGPU0\tGPU1\tGPU2\tGPU3\tCPU Affinity\tNUMA Affinity\tGPU NUMA ID\u001b[0m\nGPU0\t X \tNODE\tNODE\tNV4\t0-63\t0\t\tN/A\nGPU1\tNODE\t X \tNODE\tNODE\t0-63\t0\t\tN/A\nGPU2\tNODE\tNODE\t X \tNODE\t0-63\t0\t\tN/A\nGPU3\tNV4\tNODE\tNODE\t X \t0-63\t0\t\tN/A\n";
        const string active="\t Link 0: 14.062 GB/s\n\t Link 1: 14.062 GB/s\n\t Link 2: 14.062 GB/s\n\t Link 3: 14.062 GB/s\n";
        const string inactive="NVML: Unable to retrieve Nvlink information as all links are inActive\n";
        var inventory=new[]{"0000:01:00.0","0000:41:00.0","0000:81:00.0","0000:c6:00.0"}.Select(p=>new GpuDevice(p,"NVIDIA","RTX 3090","nvidia","",24576,[],[])).ToArray();
        var calls=new List<string[]>();
        Task<ProcessResult> Run(string exe,string[] args,int timeout)
        {
            calls.Add(args);
            var output=args[0].StartsWith("--query-gpu")?mapping:args[0]=="topo"?matrix:
                args[1]=="--status"?(args[2] is "--id=0000:01:00.0" or "--id=0000:c6:00.0"?active:inactive):"";
            return Task.FromResult(new ProcessResult(0,output));
        }
        var result=await NvLinkTopology.Observe(inventory,Run);
        check(result is {State:"Observed",Error:null},"NVIDIA underlined topology header parses completely without a false warning");
        check(result.Links.Length==6&&result.Links.Single(l=>l.Connection=="NVLink") is {From:"0000:01:00.0",To:"0000:c6:00.0",State:"Active",LinkCount:4,SpeedGBps:14.062},"Reported four-GPU topology resolves the GPU0/GPU3 bridge and all five PCIe pairs");
        check(result.Probes!.Single(p=>p.Arguments[0]=="topo").Output==matrix,"Parsing formatting leaves the exact diagnostic driver output unchanged");
        check(calls.Count==6&&!calls.Any(c=>c.Contains("-p")),"A recognized styled matrix needs no remote endpoint fallback");
    }

}
