using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Xur.Control;
using Xur.Domain;

if(args is ["--podman-smoke",var smokeDirectory,var smokeCatalog,var smokeGateway]) {await PodmanSmoke.Run(smokeDirectory,smokeCatalog,smokeGateway);return;}
if(args is ["--engine",var port])
{
    var b=WebApplication.CreateBuilder();b.Logging.ClearProviders();b.WebHost.UseUrls("http://127.0.0.1:"+port);var app=b.Build();
    app.MapGet("/health",()=>Results.Ok());app.MapGet("/pid",()=>Results.Json(new {pid=Environment.ProcessId}));
    app.MapGet("/stream",async(HttpContext c)=> {
        c.Response.ContentType="text/event-stream";
        for(int i=0;i<2000 && !c.RequestAborted.IsCancellationRequested;i++)
        {await c.Response.WriteAsync($"data: {Environment.ProcessId}:{i}\n\n",c.RequestAborted);await c.Response.Body.FlushAsync(c.RequestAborted);await Task.Delay(10,c.RequestAborted);}
    });await app.RunAsync();return;
}
Directory.SetCurrentDirectory(Path.GetFullPath("../../../../../",AppContext.BaseDirectory));
var root=Path.Combine(Path.GetTempPath(),"xur-profiles-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
var checks=new List<string>();void Check(bool ok,string name){if(!ok)throw new Exception(name);checks.Add(name);}
var sdk=Environment.GetEnvironmentVariable("XUR_DOTNET") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".local/share/xur-build/dotnet/dotnet");
var gatewayDll=Path.GetFullPath("src/Xur.Gateway/bin/Release/net10.0/Xur.Gateway.dll");
var info=new ProcessStartInfo(sdk){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add(gatewayDll);info.Environment["XUR_RUN"]=root;info.Environment["XUR_GATEWAY_STATE"]=root;
using var gatewayProcess=Process.Start(info)!;var go=gatewayProcess.StandardOutput.ReadToEndAsync();var ge=gatewayProcess.StandardError.ReadToEndAsync();
await using var runtime=new ChildRuntime(sdk);
try
{
    for(int n=0;n<100 && !File.Exists(root+"/gateway-admin.sock");n++){if(gatewayProcess.HasExited)throw new Exception(await ge);await Task.Delay(50);}
    using var admin=LocalClient.Create(root+"/gateway-admin.sock");using var data=LocalClient.Create(root+"/gateway.sock");data.Timeout=Timeout.InfiniteTimeSpan;
    for(int n=0;n<100;n++){try{(await admin.GetAsync("/routes")).EnsureSuccessStatusCode();break;}catch(Exception){if(n==99)throw;await Task.Delay(50);}}
    var gateway=new LocalWorkloadGateway(admin);
    var recipe=new Recipe("fixture","Test-only child HTTP engine","example.invalid/engine@sha256:"+new string('a',64),[],8080,"/health","CPU",0,0,"Test-only");
    var keep=new Workload("keep","Keep",recipe,[],"chat");var a=new Workload("a","A",recipe,[],"a");var b=new Workload("b","B",recipe,[],"b");
    using(var store=new ProfileStore(root+"/db"))
    {
        var manager=new ProfileManager(store,runtime,gateway);
        await manager.Save(new("a","Profile A",0,[keep,a]));await manager.Save(new("b","Profile B",0,[keep,b]));
        async Task Apply(string name)
        {var p=await manager.Preview(name);await manager.Apply(new(p.Id,p.Digest));await manager.Wait();var s=await manager.State();Check(s.Operation?.Stage=="Complete","Apply "+name+" completed");}
        var bad=await manager.Preview("a");bool rejected=false;try{await manager.Apply(new(bad.Id,"wrong"));}catch(InvalidOperationException){rejected=true;}Check(rejected && runtime.Created==0,"Wrong approval digest cannot start an engine");
        await Apply("a");var initial=(await runtime.Observe()).Instances.Single(i=>i.Id=="keep");
        using(var pinned=new HttpRequestMessage(HttpMethod.Get,"/chat/pid"))
        {pinned.Headers.Add("X-Xur-Expected-Workload","keep");pinned.Headers.Add("X-Xur-Expected-Endpoint",initial.Endpoint);Check((await data.SendAsync(pinned)).IsSuccessStatusCode,"Pinned model test requests reach the expected running workload");}
        using(var changed=new HttpRequestMessage(HttpMethod.Get,"/chat/pid"))
        {changed.Headers.Add("X-Xur-Expected-Workload","other");changed.Headers.Add("X-Xur-Expected-Endpoint",initial.Endpoint);Check((await data.SendAsync(changed)).StatusCode==HttpStatusCode.Conflict,"Gateway rejects benchmark requests after workload identity changes");}
        using(var changed=new HttpRequestMessage(HttpMethod.Get,"/chat/pid"))
        {changed.Headers.Add("X-Xur-Expected-Workload","keep");changed.Headers.Add("X-Xur-Expected-Endpoint","http://127.0.0.1:19999");Check((await data.SendAsync(changed)).StatusCode==HttpStatusCode.Conflict,"Gateway rejects benchmark requests after backend endpoint changes");}
        using var response=await data.GetAsync("/chat/stream",HttpCompletionOption.ResponseHeadersRead);response.EnsureSuccessStatusCode();
        using var stream=new StreamReader(await response.Content.ReadAsStreamAsync());Check((await stream.ReadLineAsync())=="data: "+initial.Pid+":0","Active request begins on original real child PID");
        for(int cycle=0;cycle<20;cycle++)
        {
            await Apply("b");await Apply("a");var now=(await runtime.Observe()).Instances.Single(i=>i.Id=="keep");
            Check(now.InstanceId==initial.InstanceId && now.Pid==initial.Pid && now.BootId==initial.BootId,"A↔B cycle "+(cycle+1)+" retains exact instance");
        }
        string? line;do {line=await stream.ReadLineAsync();}while(line=="");Check(line=="data: "+initial.Pid+":1","Streaming response remains intact across twenty cycles");
        var saved=(await manager.State()).Profiles.Single(p=>p.Id=="a");await manager.Save(saved with {Workloads=[keep with {Route="renamed"},a]});
        var routePlan=await manager.Preview("a");Check(routePlan.Steps.All(s=>s.Kind is "Keep" or "Publish"),"Route-only plan contains no start/stop actions");await Apply("a");
        Check((await data.GetAsync("/chat/pid")).StatusCode==HttpStatusCode.ServiceUnavailable && (await data.GetAsync("/renamed/pid")).IsSuccessStatusCode,"Gateway atomically swaps route names");
        Check((await runtime.Observe()).Instances.Single(i=>i.Id=="keep").Pid==initial.Pid,"Route change preserves backend PID and existing stream");
        var conflict=await manager.Preview("b");runtime.GenerationSuffix="external-change";rejected=false;try{await manager.Apply(new(conflict.Id,conflict.Digest));}catch(InvalidOperationException){rejected=true;}Check(rejected,"Observation change invalidates preview");runtime.GenerationSuffix="";
        response.Dispose();
        runtime.FailAfterStart="b";var failed=await manager.Preview("b");await manager.Apply(new(failed.Id,failed.Digest));await manager.Wait();
        Check((await manager.State()).Operation?.Stage=="Failed","Interrupted start records a resumable failed action");
        Check((await runtime.Observe()).Instances.Single(i=>i.Id=="keep").Pid==initial.Pid,"Failed transition leaves unaffected workload alive");
    }
    var stops=runtime.Stops;var created=runtime.Created;
    using(var recoveredStore=new ProfileStore(root+"/db"))
    {
        var recovered=new ProfileManager(recoveredStore,runtime,gateway);await recovered.Resume();await recovered.Wait();
        Check((await recovered.State()).Operation?.Stage=="Complete","New control instance resumes the durable SQLite journal");
        Check(runtime.Stops==stops && runtime.Created==created,"Resume does not repeat completed stops or duplicate an already-started child");
        var before=(await runtime.Observe()).Instances.Single(i=>i.Id=="keep");
        var p=(await recovered.State()).Profiles.Single(p=>p.Id=="b");await recovered.Save(p with {Workloads=[keep with {Recipe=recipe with {Command=["changed"]}},b]});
        var plan=await recovered.Preview("b");Check(plan.Steps.Any(s=>s.Kind=="Drain"&&s.WorkloadId=="keep") && plan.Steps.Any(s=>s.Kind=="Start"&&s.WorkloadId=="keep"),"Runtime fingerprint change drains and restarts only that workload");
        using var active=await data.GetAsync("/chat/stream",HttpCompletionOption.ResponseHeadersRead);
        var applying=recovered.Apply(new(plan.Id,plan.Digest));await applying;await Task.Delay(200);
        Check((await runtime.Observe()).Instances.Single(i=>i.Id=="keep").Pid==before.Pid,"Changed backend stays alive until its active request is released");active.Dispose();await recovered.Wait();
        Check((await recovered.State()).Operation?.Stage=="Complete" && (await runtime.Observe()).Instances.Single(i=>i.Id=="keep").Pid!=before.Pid,"Backend restarts after graceful drain");
        var gpu=new GpuDevice("0000:01:00.0","NVIDIA","Test GPU","nvidia","GPU-fixture",24576,[],[]);
        var gr=recipe with {Vendor="NVIDIA",GpuCount=1,MemoryMiB=20000};bool rejected=false;
        try {ProfilePolicy.Validate(new("conflict","Conflict",1,[keep with {Recipe=gr,Gpus=[gpu.Pci]},b with {Recipe=gr,Gpus=[gpu.Pci]}]),new("",[gpu],[]));}catch(InvalidOperationException){rejected=true;}
        Check(rejected,"Conflicting GPU assignments are rejected before apply");
        await recovered.Save(new("idle","Idle",0,[]));var idle=await recovered.Preview("idle");await recovered.Apply(new(idle.Id,idle.Digest));await recovered.Wait();Check((await runtime.Observe()).Instances.Length==0,"Empty profile drains and stops all workloads");
        runtime.FailAfterStart="a";var partial=await recovered.Preview("a");await recovered.Apply(new(partial.Id,partial.Digest));await recovered.Wait();
        Check((await recovered.State()).Operation?.Stage=="Failed","A partial start is retained for review");await recovered.Cancel();
        var cleanup=await recovered.Preview("idle");await recovered.Apply(new(cleanup.Id,cleanup.Digest));await recovered.Wait();
        Check((await recovered.State()).Operation?.Stage=="Complete" && (await runtime.Observe()).Instances.Length==0,"Cancelled failure can be replanned against real partial state");
    }
    var catalogDirectory=Path.Combine(root,"catalog");Directory.CreateDirectory(catalogDirectory);
    File.WriteAllText(Path.Combine(catalogDirectory,"fixture.json"),JsonSerializer.Serialize(recipe));
    var catalog=new RecipeCatalog(catalogDirectory);
    string firstId;
    using(var selectionStore=new ProfileStore(root+"/selection"))
    {
        var editor=new ProfileManager(selectionStore,runtime,gateway,catalog);
        var first=await editor.Create();firstId=first.Id;
        Check(first.Id=="1" && first.Name=="Profile 1" && first.Workloads.Length==0,"Create assigns an integer primary key and default name without asking for fields");
        var selected=await editor.SaveSelection(first.Id,first.Revision,[new(null,recipe.Id,[])]);
        var workload=selected.Workloads.Single();
        Check(workload.Id=="1" && workload.Name==recipe.Name && workload.Route=="workload-1","Recipe selection generates workload identity, name and route");
        var preview=await editor.Preview(first.Id);await editor.Apply(new(preview.Id,preview.Digest));await editor.Wait();
        var original=(await runtime.Observe()).Instances.Single();
        Check((await data.GetAsync("/workload-1/pid")).IsSuccessStatusCode,"Integer workload keys launch real child engines and gateway routes");
        var second=await editor.Create();var alternate=await editor.SaveSelection(second.Id,second.Revision,[new(null,recipe.Id,[])]);
        Check(alternate.Workloads.Single().Id==workload.Id,"Selecting the same recipe in another profile reuses the stable workload");
        var transition=await editor.Preview(second.Id);await editor.Apply(new(transition.Id,transition.Digest));await editor.Wait();
        Check((await runtime.Observe()).Instances.Single().Pid==original.Pid,"Selector-only profile switching preserves an unchanged real process");
        var copied=await editor.Create(first.Id);
        Check(copied.Id!=first.Id && copied.Workloads.Single().Id==workload.Id,"Duplicate generates a profile key while retaining workload keys");
        bool stale=false;try{await editor.SaveSelection(first.Id,first.Revision,[]);}catch(InvalidOperationException){stale=true;}
        Check(stale,"Stale editor revision cannot overwrite a saved profile");
        var obsolete=await editor.Preview(copied.Id);
        bool activeDelete=false;try{await editor.Delete(second.Id,alternate.Revision);}catch(InvalidOperationException){activeDelete=true;}
        Check(activeDelete,"Loaded profile cannot be deleted");
        bool staleDelete=false;try{await editor.Delete(copied.Id,copied.Revision-1);}catch(InvalidOperationException){staleDelete=true;}
        Check(staleDelete,"Delete rejects stale revisions");
        await editor.Delete(copied.Id,copied.Revision);
        Check((await editor.State()).Profiles.All(p=>p.Id!=copied.Id) && (await runtime.Observe()).Instances.Single().Pid==original.Pid,"Deleting an inactive shared profile leaves the running model intact");
        bool stalePlan=false;try{await editor.Apply(new(obsolete.Id,obsolete.Digest));}catch(InvalidOperationException){stalePlan=true;}
        Check(stalePlan,"Deleted profile cannot be applied through an old preview");
        bool resurrect=false;try{await editor.Save(copied with {Revision=0});}catch(InvalidOperationException){resurrect=true;}
        Check(resurrect,"Stale API save cannot resurrect a deleted profile identity");
        var unloading=await editor.PreviewUnload();
        Check(unloading.Unload && unloading.Steps.All(s=>s.Kind is "Drain" or "Stop" or "Publish"),"Unload uses the durable drain/stop journal without creating a saved profile");
        using(var activeStream=await data.GetAsync("/workload-1/stream",HttpCompletionOption.ResponseHeadersRead))
        {
            await editor.Apply(new(unloading.Id,unloading.Digest));await Task.Delay(200);
            Check((await runtime.Observe()).Instances.Single().Pid==original.Pid,"Unload keeps the model alive while its active response drains");
        }
        await editor.Wait();var unloaded=await editor.State();
        Check(unloaded.Active==null && unloaded.Runtime.Instances.Length==0 && unloaded.Operation is {Stage:"Complete",Unload:true},"Unload stops the real child and clears loaded state");
        Check(unloaded.Profiles.Length==2 && unloaded.Profiles.All(p=>p.Workloads.Length==1),"Unload preserves all saved workload definitions");
        Check((await data.GetAsync("/workload-1/pid")).StatusCode==HttpStatusCode.ServiceUnavailable,"Unload removes gateway routes");
        bool noProfile=false;try{await editor.PreviewUnload();}catch(InvalidOperationException){noProfile=true;}
        Check(noProfile,"Idle unload reports no profile loaded");
        var reload=await editor.Preview(second.Id);await editor.Apply(new(reload.Id,reload.Digest));await editor.Wait();
        var oldUnload=await editor.PreviewUnload();var switchBack=await editor.Preview(first.Id);await editor.Apply(new(switchBack.Id,switchBack.Digest));await editor.Wait();
        bool staleUnload=false;try{await editor.Apply(new(oldUnload.Id,oldUnload.Digest));}catch(InvalidOperationException){staleUnload=true;}
        Check(staleUnload,"An unload approval cannot stop a subsequently loaded profile");
        runtime.FailAfterStop=workload.Id;var interruptedUnload=await editor.PreviewUnload();await editor.Apply(new(interruptedUnload.Id,interruptedUnload.Digest));await editor.Wait();
        Check((await editor.State()).Operation is {Stage:"Failed",Unload:true},"Interrupted unload records its durable operation type");
        var stoppedCount=runtime.Stops;
        var resumedEditor=new ProfileManager(selectionStore,runtime,gateway,catalog);await resumedEditor.Resume();await resumedEditor.Wait();
        Check((await resumedEditor.State()).Active==null && (await resumedEditor.State()).Operation?.Stage=="Complete" && runtime.Stops==stoppedCount,"Resumed unload does not repeat an already completed stop");
        copied=await editor.Create(first.Id);
        var empty=await editor.SaveSelection(copied.Id,copied.Revision,[]);
        var stop=await editor.Preview(empty.Id);await editor.Apply(new(stop.Id,stop.Digest));await editor.Wait();
        Check((await runtime.Observe()).Instances.Length==0,"Removing all dropdown rows produces a real stop-all profile");
    }
    using(var reopened=new ProfileStore(root+"/selection"))
    {
        var editor=new ProfileManager(reopened,runtime,gateway,catalog);var next=await editor.Create();
        Check(long.Parse(next.Id)>long.Parse(firstId) && (await editor.State()).Profiles.Any(p=>p.Id==firstId),"Integer keys and saved profiles survive reopening SQLite");
    }
    await CancellationTests.Run(root,runtime,gateway,data,recipe,Check);
    await ParallelUnloadTests.Run(root,runtime,gateway,data,recipe,Check);
    await RecipeRepairTests.Run(root,runtime,gateway,recipe,Check);
    var receipt=new {suite="ProfileContinuity",result="Passed",cycles=20,realChildProcesses=true,realGatewayProcess=true,sqliteRecovery=true,checks};
    Directory.CreateDirectory(".build/evidence");File.WriteAllText(".build/evidence/profile-process-tests.json",JsonSerializer.Serialize(receipt,new JsonSerializerOptions{WriteIndented=true})+"\n");Console.WriteLine(JsonSerializer.Serialize(receipt));
}
finally {if(!gatewayProcess.HasExited){gatewayProcess.Kill(true);await gatewayProcess.WaitForExitAsync();}Console.Error.WriteLine(await ge);Console.Error.WriteLine(await go);Directory.Delete(root,true);}

sealed class ChildRuntime(string sdk):IWorkloadRuntime,IAsyncDisposable
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<string,(Workload W,RuntimeInstance I,Process P)> children=new();
    public string? PauseAfterStart;public TaskCompletionSource? StartReached,ReleaseStart;
    public int Created,Stops;public string? FailAfterStart,FailAfterStop;public string GenerationSuffix="";
    public Task<RuntimeObservation> Observe()
    {var all=children.Values.Select(v=>v.I).OrderBy(i=>i.Id).ToArray();return Task.FromResult(new RuntimeObservation(Canonical.Hash(all)+GenerationSuffix,[],all));}
    public async Task<RuntimeInstance> Start(Workload w)
    {
        if(children.TryGetValue(w.Id,out var old)){if(old.W.Fingerprint!=w.Fingerprint)throw new Exception("Missing stop");return old.I;}
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        var info=new ProcessStartInfo(sdk){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);info.ArgumentList.Add("--engine");info.ArgumentList.Add(port.ToString());
        var process=Process.Start(info)!;_ = process.StandardOutput.ReadToEndAsync();_ = process.StandardError.ReadToEndAsync();
        var instance=new RuntimeInstance(w.Id,w.Fingerprint,Guid.NewGuid().ToString("N"),process.Id,File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim(),"http://127.0.0.1:"+port+"/","running",w.Gpus);
        if(!children.TryAdd(w.Id,(w,instance,process)))throw new Exception("Duplicate child");Interlocked.Increment(ref Created);
        using var http=new HttpClient();bool ready=false;for(int n=0;n<100;n++){try {if((await http.GetAsync(instance.Endpoint+"health")).IsSuccessStatusCode){ready=true;break;}}catch(HttpRequestException){}await Task.Delay(20);}if(!ready)throw new Exception("Child startup failed");
        if(PauseAfterStart==w.Id){StartReached!.SetResult();await ReleaseStart!.Task;}
        if(FailAfterStart==w.Id){FailAfterStart=null;throw new IOException("Test-only crash after side effect");}return instance;
    }
    public async Task Stop(RuntimeStop r)
    {if(!children.TryGetValue(r.Id,out var c))return;if(c.I.InstanceId!=r.InstanceId)throw new Exception("Identity changed");c.P.Kill(true);await c.P.WaitForExitAsync();c.P.Dispose();children.TryRemove(r.Id,out _);Interlocked.Increment(ref Stops);if(FailAfterStop==r.Id){FailAfterStop=null;throw new IOException("Test-only crash after stop side effect");}}
    public async ValueTask DisposeAsync(){foreach(var c in children.Values){if(!c.P.HasExited){c.P.Kill(true);await c.P.WaitForExitAsync();}c.P.Dispose();}}
}
