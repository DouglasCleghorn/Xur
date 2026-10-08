using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Xur.Domain;
namespace Xur.Agent;
public static class GpuOwnership
{
    static string Read(string path){try{return File.ReadAllText(path).Trim();}catch(IOException){return "";}}
    public static async Task<GpuOwner[]> Observe(GpuDevice gpu,bool includeIdentity=false)
    {
        var nodes=gpu.Nodes.Concat(gpu.Cards??[]).ToHashSet();var compute=new HashSet<int>();
        if(gpu.Vendor=="NVIDIA")
        {
            nodes.Add(await NvidiaDevice.Resolve(gpu));
            var r=await Processes.Run("nvidia-smi",["--id="+gpu.Pci,"--query-compute-apps=pid","--format=csv,noheader,nounits"],10);
            if(r.ExitCode!=0)throw new InvalidOperationException($"Could not observe compute owners for {gpu.Pci}.");
            foreach(var line in r.Output.Split('\n'))if(int.TryParse(line.Trim(),out var pid))compute.Add(pid);
        }
        return ObserveProcesses(nodes,compute,includeIdentity:includeIdentity).Select(o=>o.Service=="xur-console-"+Canonical.Hash(gpu.Pci)[..16]+".service" && o.Devices.All(d=>(gpu.Cards??[]).Contains(d))
            ? o with {Blocking=false,Role="Xur display console"} : o).ToArray();
    }
    static bool PersistenceIdentity(string status,Func<string,uint?> accountUid)
    {
        var fields=Regex.Match(status,@"(?m)^Uid:\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)$");
        if(!fields.Success||fields.Groups.Cast<Group>().Skip(1).Any(g=>g.Value!=fields.Groups[1].Value))return false;
        var uid=fields.Groups[1].Value;if(uid=="0")return true;
        return uint.TryParse(uid,out var value)&&value<1000&&accountUid("nvidia-persistenced")==value;
    }
    // Fedora's immutable system accounts can be supplied by NSS altfiles rather
    // than /etc/passwd. Resolve the vendor account through libc's NSS lookup.
    static uint? AccountUid(string name)
    {
        var buffer=Marshal.AllocHGlobal(65536);
        try{return GetPassword(name,out var account,buffer,65536,out var found)==0&&found!=IntPtr.Zero?account.Uid:null;}
        finally{Marshal.FreeHGlobal(buffer);}
    }
    [StructLayout(LayoutKind.Sequential)]struct PasswordEntry
    {public IntPtr Name,Password;public uint Uid,Gid;public IntPtr Gecos,Home,Shell;}
    [DllImport("libc",EntryPoint="getpwnam_r")]static extern int GetPassword([MarshalAs(UnmanagedType.LPUTF8Str)]string name,out PasswordEntry account,IntPtr buffer,nuint length,out IntPtr found);
    public static GpuOwner[] ObserveProcesses(HashSet<string> nodes,HashSet<int>? compute=null,string procRoot="/proc",bool includeIdentity=false,Func<string,uint?>? accountUid=null)
    {
        var owners=new List<GpuOwner>();compute??=[];
        foreach(var proc in Directory.EnumerateDirectories(procRoot))
        {
            if(!int.TryParse(Path.GetFileName(proc),out var pid))continue;
            try
            {
                var held=Directory.EnumerateFiles(proc+"/fd").Select(f=>new FileInfo(f).LinkTarget??"").Where(nodes.Contains).Distinct().Order().ToArray();
                if(held.Length==0&&!compute.Contains(pid))continue;
                var name=Read(proc+"/comm");var cgroup=Read(proc+"/cgroup");
                var service=cgroup.Split('/','\n').LastOrDefault(p=>p.EndsWith(".service")||p.EndsWith(".scope"));
                var executable=new FileInfo(proc+"/exe").LinkTarget??"";
                // An open persistence handle alone is not a workload. Exempt only
                // the vendor binary in its exact system service and system account, never a process name.
                var persistenceService=service=="nvidia-persistenced.service"&&cgroup.Contains("/system.slice/nvidia-persistenced.service");
                var persistenceExecutable=executable is "/usr/bin/nvidia-persistenced" or "/usr/sbin/nvidia-persistenced";
                var status=includeIdentity||!compute.Contains(pid)&&persistenceService&&persistenceExecutable?Read(proc+"/status"):"";
                var persistenceAccount=PersistenceIdentity(status,accountUid??AccountUid);
                var persistence=!compute.Contains(pid)&&persistenceService&&persistenceExecutable&&persistenceAccount;
                var fields=includeIdentity?Regex.Match(status,@"(?m)^Uid:\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)$"):null;
                var uids=fields?.Success==true?fields.Groups.Cast<Group>().Skip(1).Select(g=>uint.Parse(g.Value,System.Globalization.CultureInfo.InvariantCulture)).ToArray():null;
                var identity=includeIdentity?new GpuProcessIdentity(executable,cgroup,uids,compute.Contains(pid),persistenceService,persistenceExecutable,persistenceAccount):null;
                owners.Add(new(pid,name.Length==0?"Process":name,service,held.Length>0?held:["NVIDIA compute context"],!persistence,persistence?"Driver persistence":compute.Contains(pid)?"Compute":"Device handle",identity));
            }
            catch(DirectoryNotFoundException){}catch(FileNotFoundException){}
        }
        return owners.OrderBy(o=>o.Pid).ToArray();
    }
}
