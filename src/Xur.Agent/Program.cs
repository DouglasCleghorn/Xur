using Xur.Agent;
using Xur.Domain;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
var run = Environment.GetEnvironmentVariable("XUR_RUN") ?? "/run/xur";
Directory.CreateDirectory(run);
File.SetUnixFileMode(run, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
var socket = Path.Combine(run, "agent.sock"); File.Delete(socket);
builder.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(socket));
var app = builder.Build();
_=ApplicationIdentity.Id;
var storage = new Storage();
bool installer = File.ReadAllText("/proc/cmdline").Split(' ').Contains("xur.installer=1");
var stateDir = installer ? run : "/var/lib/xur";
_=Task.Run(async()=>{try{if((await Processes.Run("systemctl",["is-active","firewalld"],5)).ExitCode==0)await Processes.Run("firewall-cmd",["--add-port=8443/tcp"],10);}catch{}});
Directory.CreateDirectory(stateDir);
RegistryMirror.Ensure();
var displayConsoles=new DisplayConsoles(Path.Combine(stateDir,"workloads"),run);
var operationFile = Path.Combine(stateDir,"install-operation.json");
Operation? operation = File.Exists(operationFile) ? JsonSerializer.Deserialize<Operation>(File.ReadAllText(operationFile)) : null;
var gate = new SemaphoreSlim(1, 1);
var plans = new Dictionary<string, InstallPlan>();
var layout = new PlainRootLayout();
void SaveOperation(Operation value)
{
    operation = value;
    var temp = operationFile + ".tmp";
    using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
    { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
    File.Move(temp, operationFile, true);
}
if (!installer && operation != null && File.Exists(Path.Combine(stateDir, "installed")))
    SaveOperation(operation with { Stage = "Complete", Message = "Installed deployment booted", Updated = DateTimeOffset.UtcNow });
var serverPower=new ServerPower();
if(!installer)_=Task.Run(()=>serverPower.Run(app.Lifetime.ApplicationStopping));
app.MapGet("/power/status",async()=>Results.Json(await serverPower.Status()));
var hfCredentials=new HuggingFaceCredentials(stateDir);
app.MapGet("/huggingface",()=>new {configured=hfCredentials.Configured});
app.MapPost("/huggingface",IResult(HuggingFaceTokenRequest request)=>{try{hfCredentials.Save(request.Token);return Results.Ok(new{configured=hfCredentials.Configured});}catch(InvalidOperationException e){return Results.BadRequest(new{error=e.Message});}});
var ntpSettings=new NtpSettings();
app.MapGet("/ntp",async Task<IResult>()=> {try{return Results.Json(await ntpSettings.Read());}catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}});
app.MapPost("/ntp",async Task<IResult>(NtpRequest request)=> {try{return Results.Json(await ntpSettings.Set(request));}catch(InvalidOperationException e){return Results.BadRequest(new{error=e.Message});}});
var timezoneSettings=new TimezoneSettings(stateDir);
if(!installer){_=Task.Run(async()=>{try{await ntpSettings.Initialize();}catch(Exception e){Console.Error.WriteLine("NTP default setup: "+e.Message);}});_=Task.Run(()=>timezoneSettings.Initialize(app.Lifetime.ApplicationStopping));}
app.MapPost("/timezone/refresh",async Task<IResult>()=>{try{return Results.Json(await timezoneSettings.Refresh());}catch(InvalidOperationException e){return Results.BadRequest(new{error=e.Message});}});
app.MapGet("/timezone",async Task<IResult>()=> {try{return Results.Json(await timezoneSettings.Read());}catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}});
app.MapPost("/timezone",async Task<IResult>(TimezoneRequest request)=> {try{return Results.Json(await timezoneSettings.Set(request));}catch(InvalidOperationException e){return Results.BadRequest(new{error=e.Message});}});
app.MapGet("/configuration/export",async Task<IResult>()=>{
    try{var zone=await timezoneSettings.Read();var ntp=await ntpSettings.Read();return Results.Json(new{timezone=new{zone.Current,zone.Automatic},ntp=new{ntp.Enabled,ntp.Servers},files=ConfigExport.Read(stateDir),workstationUsers=StationAccounts.Read(includeTemporary:true)});}
    catch(Exception e) when(e is InvalidOperationException or IOException or JsonException){return Results.Conflict(new{error="Configuration export could not read all settings. Retry after resolving the settings error."});}
});
app.MapGet("/status", () => Results.Json(new { installer, scan = storage.Scan, operation }));
// Root-private Unix socket only; the token never appears in public status or logs.
app.MapGet("/bootstrap-config", () => Results.Json(new { state=storage.Scan.State, token=storage.BootstrapToken }));
app.MapGet("/disks", async () => await storage.Observe());
app.MapGet("/logs",async()=> {
    var logs=await Processes.Run("journalctl",["--no-pager","-n","400","-u","xur-control.service","-u","xur-agent.service","-u","xur-install.service"],10);
    var output = logs.Output;
    if (installer)
        foreach (var path in new[] { "/tmp/anaconda.log", "/tmp/storage.log", "/tmp/program.log", "/tmp/packaging.log" })
            if (File.Exists(path)) output += "\n" + Path.GetFileName(path) + "\n" + string.Join('\n', File.ReadLines(path).TakeLast(400));
    return Results.Text(Redaction.Logs(output));
});
app.MapGet("/console-logs",async()=> {
    var result=await Processes.Run("journalctl",["--boot","--no-pager","-n","100"],10);
    return Results.Text(Redaction.Logs(result.Output));
});
app.MapGet("/diagnostics/display",async()=>{var report=await DisplayDiagnostics.Collect();report["displayConsoleError"]=displayConsoles.Error;report["stationAllocations"]=JsonSerializer.SerializeToNode(StationSeats.Status(),new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var gpus=await GpuInventory.Observe();var owners=new Dictionary<string,object>();
    foreach(var gpu in gpus)try{owners[gpu.Pci]=await GpuOwnership.Observe(gpu);}catch(Exception e){owners[gpu.Pci]=new{error=e.Message};}
    report["owners"]=JsonSerializer.SerializeToNode(owners,new JsonSerializerOptions(JsonSerializerDefaults.Web));
    report["topology"]=JsonSerializer.SerializeToNode(await NvLinkTopology.Observe(gpus),new JsonSerializerOptions(JsonSerializerDefaults.Web));return Results.Json(report);});
app.MapGet("/hardware", async () => {
    var pci = await Processes.Run("lspci", ["-Dnnk"]);
    return Results.Json(new { pci = pci.Output });
});
app.MapPost("/plan", async (PlanRequest request) => {
    await gate.WaitAsync();
    try
    {
        if (!installer || !storage.CanPlan || operation != null)
            return Results.Conflict(new { error = "Installer is locked or an operation already exists" });
        var inventory = await storage.Observe();
        var disk = inventory.Disks.SingleOrDefault(d => d.Path == request.Path);
        if (disk == null || disk.Blocked.Length != 0) return Results.BadRequest(new { error = "Whole disk is blocked or absent" });
        var plan = new InstallPlan(Guid.NewGuid().ToString("N"), "", inventory.Generation, disk,
            inventory.Disks.Where(d => d.Path != disk.Path).ToArray(), layout.Actions, DateTimeOffset.UtcNow.AddMinutes(5));
        plan = plan with { Digest = Canonical.Hash(plan) };
        plans.Clear(); plans.Add(plan.Id, plan);
        return Results.Ok(plan);
    }
    finally { gate.Release(); }
});
app.MapPost("/approve", async (Approval approval) => {
    await gate.WaitAsync();
    try
    {
        if (!installer || !storage.CanPlan || operation != null)
            return Results.Conflict(new { error = "Installer is locked" });
        if (!plans.TryGetValue(approval.Id, out var plan) || plan.Digest != approval.Digest || plan.Expires <= DateTimeOffset.UtcNow)
            return Results.Conflict(new { error = "Plan is absent, changed or expired; review again" });
        if(Directory.Exists("/run/xur/app") && !File.Exists("/run/xur/installer-app-ready"))
            return Results.Conflict(new {error="Installer app health check is still running. Retry shortly."});
        var template = File.ReadAllText("/usr/share/xur/install-template.ks");
        if (template.Contains("clearpart") || template.Contains("ignoredisk") || template.Contains("part /"))
            return Results.Conflict(new { error = "Unsafe installer template" });
        var inventory = await storage.Observe();
        if (plan.Expires <= DateTimeOffset.UtcNow || inventory.Generation != plan.Generation || inventory.Disks.SingleOrDefault(d => d.Path == plan.Target.Path) is not { Blocked.Length: 0 })
            return Results.Conflict(new { error = "Storage identity, layout or inventory changed; review again" });
        // Only this approval path can emit partition instructions or start Anaconda.
        var fragment = template + "\n" + layout.Kickstart(plan.Target);
        using (var file = new FileStream(Path.Combine(run,"approved.ks.tmp"), FileMode.CreateNew, FileAccess.Write))
        { file.Write(System.Text.Encoding.UTF8.GetBytes(fragment)); file.Flush(true); }
        File.Move(Path.Combine(run,"approved.ks.tmp"), Path.Combine(run,"approved.ks"));
        SaveOperation(new(plan.Id,"Approved","Exact disk identity revalidated; releasing Anaconda",DateTimeOffset.UtcNow));
        plans.Clear();
        try
        {
            await storage.RestoreReadOnlyStates();
            var start = await Processes.Run("systemctl", ["start", "--no-block", "xur-install.service"]);
            if (start.ExitCode != 0) throw new IOException();
            SaveOperation(operation! with { Stage = "Installing", Message = "Anaconda is installing the approved disk", Updated = DateTimeOffset.UtcNow });
        }
        catch { SaveOperation(operation! with { Stage = "Failed", Message = "Installer release failed; manual review required", Updated = DateTimeOffset.UtcNow }); }
        return Results.Ok(operation);
    }
    finally { gate.Release(); }
});
app.MapGet("/display-consoles",()=>Results.Json(new{error=displayConsoles.Error}));
var workloads=new WorkloadRuntime(Path.Combine(stateDir,"workloads"),new RecipeCatalog(Environment.GetEnvironmentVariable("XUR_CATALOG") ?? "/usr/share/xur/catalog",Path.Combine(stateDir,"catalog-selected")),displayConsoles);
var containers=new ContainerLibrary(stateDir);
app.MapGet("/container-jobs",()=>installer?Results.Conflict():Results.Json(containers.Jobs()));
app.MapGet("/container-volumes",async()=>installer?Results.Conflict():Results.Json(await ContainerLibrary.Volumes()));
app.MapPost("/container-jobs",(ContainerPrepareRequest request)=> {
    if(installer || File.Exists("/var/lib/xur/app/maintenance"))return Results.Conflict(new {error="Container preparation is unavailable during setup or an app update."});
    try {return Results.Json(containers.Prepare(request));}catch(InvalidOperationException e){return Results.Conflict(new {error=e.Message});}
});
var modelCatalog=new ModelCatalog(stateDir);
app.MapGet("/workstations/{id}/graphics",async Task<IResult>(string id)=>{if(installer)return Results.Conflict();try{return Results.Json(await workloads.Graphics(id));}catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}});
app.MapGet("/workstations/{id}/display",async Task<IResult>(string id)=>{try{return Results.Json(await workloads.Display(id,null));}catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}});
app.MapPost("/workstations/{id}/display",async Task<IResult>(string id,StationDisplayRequest request)=>{if(installer)return Results.Conflict();try{return Results.Json(await workloads.Display(id,request));}catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}});
app.MapGet("/workstations",async Task<IResult>()=>installer?Results.Conflict():Results.Json(await workloads.StreamingStatus()));
app.MapGet("/workstations/{id}/pairings",async Task<IResult>(string id)=>{try{return Results.Json(await workloads.Pairings(id));}catch(Exception e) when(e is InvalidOperationException or HttpRequestException or IOException or JsonException or TaskCanceledException){return Results.Conflict(new{error="Load the workstation and start streaming before pairing."});}});
app.MapPost("/workstations/{id}/pair",async Task<IResult>(string id,StationPairRequest request)=>{if(installer)return Results.Conflict();try{await workloads.Pair(id,request);return Results.Ok();}catch(Exception e) when(e is InvalidOperationException or HttpRequestException or IOException or JsonException or TaskCanceledException){return Results.Conflict(new{error=e is InvalidOperationException?e.Message:"Pairing service is unavailable."});}});
app.MapPost("/workstations/{id}/stream",async Task<IResult>(string id)=>{if(installer)return Results.Conflict();try{await workloads.StartStreaming(id);return Results.Ok();}catch(Exception e) when(e is InvalidOperationException or IOException){return Results.Conflict(new{error=e.Message});}});
app.MapPost("/workstations/{id}/stream/restart",async Task<IResult>(string id)=>{if(installer)return Results.Conflict();try{await workloads.StartStreaming(id,restart:true);return Results.Ok();}catch(Exception e) when(e is InvalidOperationException or IOException){return Results.Conflict(new{error=e.Message});}});
app.MapGet("/station-allocations",()=>Results.Json(StationSeats.Status()));
app.MapGet("/station-devices",async Task<IResult>()=>installer?Results.Conflict():Results.Json(StationDeviceInventoryReader.Read(await GpuInventory.Observe())));
StationFiles.Map(app,installer);
var stationAccounts=new StationAccounts();
app.MapGet("/station-users",()=>installer?Results.Conflict():Results.Json(StationAccounts.Read()));
app.MapPost("/station-users",async Task<IResult>(StationUserCreate request)=>{
    if(installer)return Results.Conflict();
    try{return Results.Json(await stationAccounts.Create(request.Name));}catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}
});
if(!installer){_=Task.Run(workloads.RestoreStations);_=Task.Run(()=>StationSeats.Watch(app.Lifetime.ApplicationStopping));}
async Task<IResult> CatalogCall(Func<Task<object>> action)
{
    if(installer)return Results.Conflict();
    try{return Results.Json(await action());}
    catch(Exception e) when(e is InvalidOperationException or HttpRequestException or JsonException){return Results.Conflict(new{error=Redaction.Logs(e.Message)});}
}
app.MapGet("/catalog/search",(string? q,string? engine)=>CatalogCall(async()=>await modelCatalog.Search(q,engine??"llama.cpp")));
app.MapGet("/catalog/options",(string model,string? engine)=>CatalogCall(async()=>await modelCatalog.Options(model,engine??"llama.cpp")));
app.MapPost("/catalog/resolve",(ModelSelection selection)=>CatalogCall(async()=>await modelCatalog.Resolve(selection)));
app.MapGet("/workloads",async()=>Results.Json(await workloads.Observe()));
app.MapGet("/workloads/{id}/logs",async(string id)=>Results.Text(await workloads.Logs(id)));
app.MapPost("/workloads/prepare",async (Workload[] request)=>{if(installer)return Results.Conflict();try{await workloads.Prepare(request);return Results.Ok();}catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}});
app.MapPost("/workloads/start",async(RuntimeStart request)=> {
    if(installer)return Results.Conflict();
    try {return Results.Json(await workloads.Start(request.Workload));}catch(InvalidOperationException e){return Results.Conflict(new {error=e.Message});}
});
app.MapPost("/workloads/stop",async(RuntimeStop request)=> {
    if(installer)return Results.Conflict();
    try {await workloads.Stop(request);return Results.Ok();}catch(InvalidOperationException e){return Results.Conflict(new {error=e.Message});}
});
var appUpdates=new ApplicationUpdates();
app.MapGet("/application-health",()=>Results.Json(new {id=ApplicationIdentity.Id,busy=containers.Busy}));
app.MapGet("/application-updates",async Task<IResult>()=> {
    if(installer)return Results.Conflict();
    try{return Results.Content(await appUpdates.Status(),"application/json");}catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}
});
app.MapPost("/application-updates",async Task<IResult>(ApplicationUpdateRequest request)=> {
    if(installer)return Results.Conflict();
    try{await appUpdates.Act(request);return Results.Accepted();}catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}
});
var gpuPower=new GpuPower(stateDir);
if(!installer)_=Task.Run(()=>gpuPower.Run(app.Lifetime.ApplicationStopping));
app.MapGet("/gpu-power",async Task<IResult>()=>installer?Results.Conflict():Results.Json(await gpuPower.Status()));
app.MapPost("/gpu-power",async Task<IResult>(GpuPowerRequest request)=> {
    if(installer)return Results.Conflict();
    try{await gpuPower.Set(request);return Results.Ok();}
    catch(Exception e) when(e is InvalidOperationException or IOException or UnauthorizedAccessException){return Results.Conflict(new {error=e is InvalidOperationException?e.Message:"Could not save the GPU power setting."});}
});
var gpuMonitor=new GpuMonitor(stateDir);
if(!installer)_=Task.Run(()=>gpuMonitor.Run(app.Lifetime.ApplicationStopping));
app.MapGet("/gpu-telemetry/sample",async()=>installer?Results.Conflict():Results.Json(await gpuMonitor.Sample()));
app.MapGet("/gpu-telemetry",(int? minutes)=>installer?Results.Conflict():Results.Json(gpuMonitor.Status(minutes??15)));
new StorageExplorer().Map(app,installer);
var storageTrim=new StorageTrim(stateDir);
app.MapGet("/storage/trim",async Task<IResult>()=>{if(installer)return Results.Conflict();try{return Results.Json(await storageTrim.Read());}catch(InvalidOperationException e){return Results.BadRequest(new{error=e.Message});}});
app.MapPost("/storage/trim",async Task<IResult>(TrimRequest request)=>{if(installer)return Results.Conflict();try{return Results.Json(await storageTrim.Start(request.Id),statusCode:202);}catch(InvalidOperationException e){return Results.BadRequest(new{error=e.Message});}});
var storageUsage=new StorageUsage();
app.MapGet("/storage-usage",()=>installer?Results.Conflict():Results.Json(storageUsage.Status()));
app.MapPost("/storage-usage/refresh",()=>installer?Results.Conflict():Results.Json(storageUsage.Status(true)));
var modelLibrary=new ModelLibrary(stateDir);
if(!installer)modelLibrary.Load();
app.MapGet("/models",()=>installer?Results.Conflict():Results.Json(modelLibrary.Status()));
app.MapPost("/models/scan",()=>{if(installer)return Results.Conflict();modelLibrary.Start(true);return Results.Accepted();});
var networkUsage=new NetworkUsage();if(!installer)_=Task.Run(()=>networkUsage.Run(app.Lifetime.ApplicationStopping));
app.MapGet("/network-usage",(int? minutes)=>Results.Json(networkUsage.Status(minutes??15)));
var monitor=new SystemMonitor();
var updates=new OsUpdates();
var updateAll=new UpdateAll();
app.MapGet("/update-all",async Task<IResult>()=> {
    if(installer)return Results.Conflict();
    try{return Results.Json(await updateAll.Status());}catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}
});
app.MapPost("/update-all",async Task<IResult>()=> {
    if(installer)return Results.Conflict();
    try{await updateAll.Start();return Results.Accepted();}catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}
});
var toolUpdates=new ToolUpdateInventory();
app.MapGet("/tool-updates",async Task<IResult>()=>installer?Results.Conflict():Results.Json(await toolUpdates.Read()));
app.MapGet("/updates",async Task<IResult>()=> {
    if(installer)return Results.Conflict();
    try{return Results.Json(await updates.Status());}
    catch(Exception e) when(e is InvalidOperationException or IOException or JsonException){return Results.Problem("OS update status is unavailable.");}
});
app.MapPost("/updates",async Task<IResult>(OsUpdateAction request)=> {
    if(installer)return Results.Conflict();
    try{await updates.Start(request.Action);return Results.Accepted();}
    catch(InvalidOperationException e){return Results.Conflict(new {error=e.Message});}
});
app.MapGet("/system",async()=>Results.Json(await monitor.Observe()));
app.MapPost("/runtime/action",async(ServiceAction request)=> {
    if(installer || !SystemMonitor.Managed(request.Name) || request.Action is not ("start" or "stop" or "restart"))return Results.BadRequest();
    var observed=await monitor.Observe();
    if(!observed.Services.Any(s=>s.Name==request.Name && s.Managed))return Results.NotFound();
    var result=await Processes.Run("systemctl",[request.Action,request.Name],60);
    return result.ExitCode==0 ? Results.Ok() : Results.Problem("Service action failed. Check the system logs.");
});
app.MapPost("/power/{action}", async (string action) => {
    if (action is not ("reboot" or "poweroff")) return Results.BadRequest();
    if (operation?.Stage == "Installing") return Results.Conflict();
    if(!installer && await UpdateAll.Running())return Results.Conflict();
    if(!installer && File.Exists("/var/lib/xur/app/current/host/os-update") && (await updates.Status()).Busy)return Results.Conflict();
    await Processes.Run("systemctl", [action]); return Results.Accepted();
});
await app.StartAsync();
_=Task.Run(()=>displayConsoles.Run(app.Lifetime.ApplicationStopping));
File.SetUnixFileMode(socket, UnixFileMode.UserRead | UnixFileMode.UserWrite);
if (installer) await storage.DiscoverAnswers();
_ = Task.Run(async () => {
    while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
        await Task.Delay(2000);
        if (!installer || operation?.Stage != "Installing") continue;
        try
        {
            var result = await Processes.Run("systemctl", ["show", "xur-install.service", "--property=ActiveState,Result"]);
            if (result.Output.Contains("ActiveState=failed")) SaveOperation(operation with { Stage = "Failed", Message = "Anaconda failed; no automatic retry", Updated = DateTimeOffset.UtcNow });
            if (File.Exists("/run/xur/install-failed")) SaveOperation(operation with { Stage = "Failed", Message = "Anaconda reported an installation error; no automatic retry", Updated = DateTimeOffset.UtcNow });
            if (File.Exists("/run/xur/install-complete")) SaveOperation(operation with { Stage = "Complete", Message = "Installation completed. Reboot from the installed disk.", Updated = DateTimeOffset.UtcNow });
        }
        catch { /* Poll failure is not installation success. */ }
    }
});
await app.WaitForShutdownAsync();
record PlanRequest(string Path);
