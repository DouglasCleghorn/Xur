using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xur.Domain;

namespace Xur.Agent;

// Only fixed setup commands run, as the container's unprivileged user. Login
// processes and one-time codes are memory-only and bound to an immutable container.
public sealed class T3Setup(WorkloadRuntime workloads):IDisposable
{
    static readonly JsonSerializerOptions json=new(JsonSerializerDefaults.Web);
    static readonly string script=ReadScript();
    readonly SemaphoreSlim gate=new(1);
    readonly Dictionary<string,Flow> flows=[];
    sealed class Flow(string id,string provider,string action,string container,Process process)
    {
        public readonly string Id=id,Provider=provider,Action=action,Container=container;
        public readonly Process Process=process;
        public T3SetupState State=new("Starting","Starting setup…",id,Provider:provider);
        public volatile bool Cancelled;
        public readonly TaskCompletionSource<T3SetupState> Done=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    static string ReadScript()
    {
        using var stream=typeof(T3Setup).Assembly.GetManifestResourceStream("Xur.T3Setup")!;
        using var reader=new StreamReader(stream);return reader.ReadToEnd();
    }
    static bool Active(Flow flow)=>!flow.Done.Task.IsCompleted;
    public async Task<T3SetupState> Execute(T3SetupRequest request)
    {
        if(request.Provider is not ("codex" or "claude" or "opencode"))throw new InvalidOperationException("Choose Codex, Claude or OpenCode.");
        if(request.Action is not ("status" or "install" or "login" or "respond" or "cancel" or "connect" or "connection-status"))throw new InvalidOperationException("Unsupported setup action.");
        if(request.Action=="login" && request.Provider=="opencode")throw new InvalidOperationException("Connect an Xur model to OpenCode.");
        if(request.Action is "connect" or "connection-status" && (request.Model==null || request.Provider=="claude"))throw new InvalidOperationException("Choose a local model and compatible harness.");
        await gate.WaitAsync();
        Flow? started=null;
        try
        {
            if(request.FlowId!=null)
            {
                if(!flows.TryGetValue(request.FlowId,out var flow)||flow.Provider!=request.Provider)throw new InvalidOperationException("This setup session expired. Start again.");
                var same=await workloads.WithT3Code(id=>Task.FromResult(id==flow.Container));
                if(!same){Close(flow);throw new InvalidOperationException("T3's container changed. Start setup again.");}
                if(request.Action=="respond")
                {
                    var waiting=flow.State;
                    if(!Active(flow)||!waiting.NeedsCode||request.Code==null||!Regex.IsMatch(request.Code,@"\A[A-Za-z0-9_#.-]{1,2048}\z"))throw new InvalidOperationException("Paste the current one-time Claude sign-in code.");
                    var verifying=waiting with{State="Verifying",Message="Checking sign-in…",NeedsCode=false,Url=null,DeviceCode=null};
                    if(!ReferenceEquals(Interlocked.CompareExchange(ref flow.State,verifying,waiting),waiting))throw new InvalidOperationException("Sign-in state changed. Refresh setup before responding.");
                    await flow.Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{code=request.Code},json));
                    await flow.Process.StandardInput.FlushAsync();
                }
                else if(request.Action=="cancel")
                {
                    if(flow.Action!="login")throw new InvalidOperationException("Wait for this setup operation to complete.");
                    Close(flow);
                }
                else if(request.Action!="status")throw new InvalidOperationException("Invalid setup session action.");
                return flow.State;
            }
            if(request.Action is "respond" or "cancel")throw new InvalidOperationException("Choose the current sign-in session.");
            if(request.Action=="install"&&flows.Values.Any(f=>f.Action=="install"&&Active(f)))throw new InvalidOperationException("Wait for the current harness installation to finish.");
            var current=flows.Values.FirstOrDefault(f=>f.Provider==request.Provider&&Active(f));
            if(current!=null)
            {
                if(request.Action=="status")
                {
                    if(!await workloads.WithT3Code(id=>Task.FromResult(id==current.Container))){Close(current);throw new InvalidOperationException("T3's container changed. Start setup again.");}
                    return current.State;
                }
                throw new InvalidOperationException("Finish or cancel the current setup for this harness first.");
            }
            // Retain at most one finished session per harness; no credential history.
            foreach(var old in flows.Values.Where(f=>f.Provider==request.Provider&&!Active(f)).ToArray())
            {old.Process.Dispose();flows.Remove(old.Id);}
            started=await workloads.WithT3Code(async container=>
            {
                var info=new ProcessStartInfo("podman"){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
                foreach(var arg in new[]{"exec","--interactive","--user","node","--workdir",T3CodeWorkload.Home,container,"python3","-u","-c",script})info.ArgumentList.Add(arg);
                var process=Process.Start(info)??throw new InvalidOperationException("Could not start T3 setup.");
                var flow=new Flow(Guid.NewGuid().ToString("N"),request.Provider,request.Action,container,process);
                flows.Add(flow.Id,flow);
                _=Read(flow);
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request,json));await process.StandardInput.FlushAsync();
                return flow;
            });
        }
        finally{gate.Release();}
        if(request.Action is "status" or "connect" or "connection-status")return await started!.Done.Task.WaitAsync(TimeSpan.FromSeconds(request.Action=="connect"?280:30));
        return started!.State;
    }
    async Task Read(Flow flow)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(16));
        // Discard stderr; provider/process diagnostics can contain credentials.
        async Task Drain(){var buffer=new char[4096];while(await flow.Process.StandardError.ReadAsync(buffer,timeout.Token)>0){}}
        var drain=Drain();
        try
        {
            var buffer=new char[2048];var pending="";int count;
            while((count=await flow.Process.StandardOutput.ReadAsync(buffer,timeout.Token))>0)
            {
                pending+=new string(buffer,0,count);
                if(pending.Length>65536)throw new IOException("Setup response exceeds its limit.");
                int newline;
                while((newline=pending.IndexOf('\n'))>=0)
                {
                    var line=pending[..newline];pending=pending[(newline+1)..];
                    var state=JsonSerializer.Deserialize<T3SetupState>(line,json)??throw new IOException();
                    if(!flow.Cancelled)flow.State=state with{FlowId=flow.Id,Provider=flow.Provider};
                }
            }
            await flow.Process.WaitForExitAsync(timeout.Token);await drain;
            if(flow.Process.ExitCode!=0 || flow.State.State is "Starting" or "Waiting" or "Verifying" or "Checking" or "Installing")
                flow.State=new("Failed","Setup interrupted. Check the T3 workload and retry.",flow.Id,Provider:flow.Provider);
        }
        catch
        {
            flow.State=new("Failed","Setup interrupted or timed out. Start again.",flow.Id,Provider:flow.Provider);
            try{flow.Process.StandardInput.Close();if(!flow.Process.HasExited)flow.Process.Kill(true);}catch{}
            try{await drain;}catch{}
        }
        finally
        {
            if(flow.Cancelled)flow.State=new("Cancelled","Sign-in cancelled.",flow.Id,Provider:flow.Provider);
            if(flow.State.State is not ("Waiting" or "Verifying"))flow.State=flow.State with{Url=null,DeviceCode=null,NeedsCode=false};
            flow.Done.TrySetResult(flow.State);
        }
    }
    static void Close(Flow flow)
    {
        if(!Active(flow))return;
        flow.Cancelled=true;
        flow.State=new("Cancelled","Sign-in cancelled.",flow.Id,Provider:flow.Provider);
        try{flow.Process.StandardInput.WriteLine("{\"action\":\"cancel\"}");flow.Process.StandardInput.Flush();flow.Process.StandardInput.Close();}catch{}
    }
    public void Dispose(){foreach(var flow in flows.Values)try{flow.Process.StandardInput.Close();}catch{}}
}
