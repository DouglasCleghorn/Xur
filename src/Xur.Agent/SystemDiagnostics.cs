using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using Xur.Domain;
namespace Xur.Agent;

// Fixed read-only probes through the existing authenticated diagnostics API.
public sealed class SystemDiagnostics(
    Func<string,string[],int,Task<ProcessResult>>? runner=null,
    Func<Task<GpuDevice[]>>? inventory=null,Func<GpuDevice,Task<GpuOwner[]>>? observeOwners=null)
{
    readonly SemaphoreSlim collectionGate=new(1,1);
    public async Task<JsonObject> Collect()
    {
        if(!await collectionGate.WaitAsync(0))throw new InvalidOperationException("System diagnostics are already collecting. Try again shortly.");
        try
        {
            var probes=new List<DiagnosticProbe>();
            foreach(var (command,args) in new[]{
                ("journalctl",new[]{"--boot","--no-pager","--output=short-monotonic","--lines=2000"}),
                ("journalctl",new[]{"--boot","--dmesg","--no-pager","--priority=warning","--lines=200"}),
                ("systemctl",new[]{"--failed","--type=service","--output=json","--no-pager"}),
                ("systemctl",new[]{"show","nvidia-persistenced.service","--property=MainPID,ExecStart,User,Group,ActiveState,Result,FragmentPath"})})
            {
                try
                {
                    using var capture=new ProbeCapture();int exit;
                    if(runner!=null)
                    {
                        var result=await runner(command,args,10);exit=result.ExitCode;
                        var output=Encoding.UTF8.GetBytes(result.Output);capture.Write(output,0,output.Length);
                    }
                    else exit=await Xur.IO.CommandRunner.RunLogged(command,args,capture,10);
                    probes.Add(new(command,args,exit,Redaction.Logs(capture.Text),capture.Truncated));
                }
                catch(Exception e) when(e is IOException or System.ComponentModel.Win32Exception or OperationCanceledException)
                {probes.Add(new(command,args,-1,"Probe unavailable: "+e.GetType().Name));}
            }
            var owners=new JsonObject();var errors=new List<string>();
            try
            {
                var gpus=inventory!=null?await inventory():await GpuInventory.Observe();
                foreach(var gpu in gpus)
                {
                    try{owners[gpu.Pci]=JsonSerializer.SerializeToNode(observeOwners!=null?await observeOwners(gpu):await GpuOwnership.Observe(gpu,includeIdentity:true),new JsonSerializerOptions(JsonSerializerDefaults.Web));}
                    catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
                    {errors.Add(gpu.Pci+": "+Redaction.Logs(e.Message));}
                }
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {errors.Add("GPU inventory: "+Redaction.Logs(e.Message));}
            var report=JsonSerializer.SerializeToNode(new{schema=1,capturedAt=DateTimeOffset.UtcNow,bundle=ApplicationIdentity.Id,probes,errors},new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
            report["gpuOwners"]=owners;
            return report;
        }
        finally{collectionGate.Release();}
    }

    // Drain both pipes while retaining only the first 256 KiB, even for a huge journal entry.
    sealed class ProbeCapture:Stream
    {
        readonly MemoryStream buffer=new();const int Limit=256*1024;
        public bool Truncated {get;private set;}
        public string Text=>Encoding.UTF8.GetString(buffer.GetBuffer(),0,(int)buffer.Length)+(Truncated?"\n[truncated]":"");
        public override void Write(byte[] bytes,int offset,int count)
        {
            var take=Math.Min(count,Limit-(int)buffer.Length);buffer.Write(bytes,offset,take);
            Truncated|=take<count;
        }
        public override void Flush(){}
        public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;
        public override long Length=>buffer.Length;
        public override long Position{get=>buffer.Position;set=>throw new NotSupportedException();}
        public override int Read(byte[] bytes,int offset,int count)=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing){if(disposing)buffer.Dispose();base.Dispose(disposing);}
    }
}
