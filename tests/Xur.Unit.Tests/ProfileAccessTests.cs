using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xur.Control;
using Xur.Domain;

static class ProfileAccessTests
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(".build/evidence/pa-"+Guid.NewGuid().ToString("N")[..8]);Directory.CreateDirectory(root);
        var oldMode=Environment.GetEnvironmentVariable("XUR_MODE");Environment.SetEnvironmentVariable("XUR_MODE","Installed");
        try
        {
            var access=new ProfileAccessSettings(root+"/access.json");
            check(ProfileSwitcherTransport.SocketPath("/run/xur")==ProfileSwitcherTransport.DefaultSocket&&ProfileSwitcherTransport.SocketPath(root).StartsWith(root+"/profile-switcher/"),"Isolated hosts keep desktop transport inside their configured run directory");
            check(access.ConsoleAllowed&&access.WorkstationsAllowed,"Profile access defaults to all local surfaces");
            access.Save(ProfileAccessSettings.Console);
            check(new ProfileAccessSettings(root+"/access.json").Mode==ProfileAccessSettings.Console&&access.ConsoleAllowed&&!access.WorkstationsAllowed,"Console-only policy persists and excludes workstations");
            check((File.GetUnixFileMode(root+"/access.json")&(UnixFileMode)63)==0,"Profile access settings are private to the manager");
            await Denied(()=>Task.Run(()=>access.Save("anything")),check,"Invalid access options cannot be saved");
            File.WriteAllText(root+"/access.json","broken");check(!access.ConsoleAllowed&&!access.WorkstationsAllowed,"Malformed access settings fail closed to web management");
            access.Save(ProfileAccessSettings.Workstations);
            using var store=new ProfileStore(root+"/profiles");var runtime=new Runtime();var events=new List<ProfileSwitchEvent>();var manager=new ProfileManager(store,runtime,new Gateway(),audit:events.Add);
            var recipe=new Recipe("test","Assistant","mirror.gcr.io/example/engine@sha256:"+new string('a',64),["private-command"],8080,"/health","CPU",0,0,"private-description");
            await manager.Save(new("p","Desk and models",0,[new("model","Assistant",recipe,[],"assistant")]));
            var seat="desk-a";var active=true;bool maintenance=false,revokeInEntry=false;var enters=0;var exits=0;
            Task<ProfileSwitcherSession?> Session(int uid,CancellationToken _) =>Task.FromResult(active&&uid>=1000?(ProfileSwitcherSession?)new(uid,"local-user",seat,"seat-a"):null);
            await using var broker=new ProfileSwitcherBroker(manager,Session,()=>{enters++;if(revokeInEntry)access.Save(ProfileAccessSettings.Web);return !maintenance;},()=>exits++,access);
            var state=JsonSerializer.Serialize(await broker.Handle(1000,new("state")),Json);
            check(state.Contains("Assistant")&&!state.Contains("private-command")&&!state.Contains("private-description")&&!state.Contains("sha256"),"Desktop transport returns display data without private recipes or commands");
            await Denied(()=>broker.Handle(0,new("state")),check,"Privileged and non-workstation users cannot use desktop transport");
            active=false;await Denied(()=>broker.Handle(1000,new("state")),check,"Inactive workstation users cannot switch profiles");active=true;
            var displayCalls=new List<(ProfileSwitcherSession Actor,SwitcherRequest Request)>();
            await using(var displayBroker=new ProfileSwitcherBroker(manager,Session,access:access,displayPower:(actor,request,_)=>{displayCalls.Add((actor,request));return Task.FromResult<object>(new DisplayPowerResult("Acknowledged",1));}))
            {
                access.Save(ProfileAccessSettings.Web);await displayBroker.Handle(1000,new("display-off","display-1"));
                check(displayCalls.Single().Actor.WorkloadId=="desk-a"&&displayCalls.Single().Actor.Uid==1000,"Workstation CEC controls retain the authenticated seat independently of profile-loading permission");
                active=false;await Denied(()=>displayBroker.Handle(1000,new("display-wake")),check,"Inactive users cannot invoke workstation CEC wake");active=true;
                await Denied(()=>displayBroker.Handle(0,new("display-on","display-1")),check,"CEC desktop controls require an active unprivileged workstation user");
                await Denied(()=>displayBroker.Handle(1000,new("display-raw","cec0")),check,"CEC broker rejects arbitrary command actions");
                access.Save(ProfileAccessSettings.Workstations);
            }
            await Denied(()=>broker.Handle(1000,new("delete", "p")),check,"Desktop transport rejects management and arbitrary actions");
            var plan=await Preview(broker);
            await Denied(()=>broker.Handle(1001,new("apply",plan.Id,plan.Digest,"controller")),check,"Reviewed plans belong to the requesting UID");
            seat="desk-b";await Denied(()=>broker.Handle(1000,new("apply",plan.Id,plan.Digest,"controller")),check,"Reviewed plans cannot move to another workstation");seat="desk-a";
            await Denied(()=>broker.Handle(1000,new("apply",plan.Id,plan.Digest,"api")),check,"Desktop audit triggers use a fixed allowlist");
            await Denied(()=>broker.Handle(1000,new("apply",plan.Id,"wrong","keyboard")),check,"Desktop approvals require the exact reviewed digest");
            maintenance=true;await Denied(()=>broker.Handle(1000,new("apply",plan.Id,plan.Digest,"keyboard")),check,"Application maintenance blocks desktop approvals");maintenance=false;
            access.Save(ProfileAccessSettings.Console);await Denied(()=>broker.Handle(1000,new("apply",plan.Id,plan.Digest,"controller")),check,"Disabling workstation access revokes an open review");
            access.Save(ProfileAccessSettings.Workstations);revokeInEntry=true;await Denied(()=>broker.Handle(1000,new("apply",plan.Id,plan.Digest,"controller")),check,"Desktop approval rechecks policy after entering maintenance");revokeInEntry=false;
            check(events.Count==0&&runtime.Instances.Count==0,"Rejected approvals never start workloads or log accepted switches");access.Save(ProfileAccessSettings.Workstations);
            await broker.Handle(1000,new("apply",plan.Id,plan.Digest,"controller"));await manager.Wait();
            check(events.Select(e=>e.Result).SequenceEqual(["accepted","complete"])&&events.All(e=>e.Origin==new ProfileSwitchOrigin("controller","local-user",1000,"desk-a","seat-a")),"Accepted and completed desktop switches record their Linux user, workstation and trigger");
            check(store.Get<Journal>("journal","current")?.Origin==events[0].Origin&&exits==enters-1,"Switch provenance persists in the transition journal and maintenance exits balance successful entries");
            var unload=await Preview(broker,true);await broker.Handle(1000,new("apply",unload.Id,unload.Digest,"keyboard"));await manager.Wait();
            check(runtime.Instances.Count==0&&(await manager.State()).Active==null&&events.TakeLast(2).All(e=>e.Action=="unload"&&e.Origin.Trigger=="keyboard"),"Unload all stops every running workload and records its trigger");
            runtime.Fail=true;plan=await Preview(broker);await broker.Handle(1000,new("apply",plan.Id,plan.Digest,"plasma-menu"));await manager.Wait();
            check(events.Last().Result=="failed","Failed profile loads retain their audit result");runtime.Fail=false;await manager.Resume();await manager.Wait();
            check(events.TakeLast(2).Select(e=>e.Result).SequenceEqual(["resumed","complete"])&&events.TakeLast(2).All(e=>e.Origin.Trigger=="plasma-menu"),"Resumed loads preserve the original switch trigger");
            await Framing(broker,root,check);
            await Console(manager,access,events,check);
            await Replacing(manager,broker,runtime,access,recipe,check);
            LocalConsole.Show("Status","");check(LocalConsole.SelectLine("10")=='f',"Serial console option ten opens profile switching");
        }
        finally{Environment.SetEnvironmentVariable("XUR_MODE",oldMode);Directory.Delete(root,true);}
    }
    static async Task<(string Id,string Digest)> Preview(ProfileSwitcherBroker broker,bool unload=false)
    {var data=JsonSerializer.SerializeToElement(await broker.Handle(1000,new(unload?"preview-unload":"preview",unload?null:"p")),Json);return(data.GetProperty("id").GetString()!,data.GetProperty("digest").GetString()!);}
    static async Task Denied(Func<Task> action,Action<bool,string> check,string name)
    {bool denied=false;try{await action();}catch(InvalidOperationException){denied=true;}check(denied,name);}
    static async Task Framing(ProfileSwitcherBroker broker,string root,Action<bool,string> check)
    {
        var path=root+"/switcher.sock";broker.Start(path,CancellationToken.None);
        using var socket=new Socket(AddressFamily.Unix,SocketType.Stream,ProtocolType.Unspecified);await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
        using var stream=new NetworkStream(socket,false);var body=JsonSerializer.SerializeToUtf8Bytes(new SwitcherRequest("state"),Json);var header=new byte[4];BinaryPrimitives.WriteUInt32BigEndian(header,(uint)body.Length);
        await stream.WriteAsync(header);await stream.WriteAsync(body.AsMemory(0,3));await stream.WriteAsync(body.AsMemory(3));await stream.ReadExactlyAsync(header);
        var response=new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];await stream.ReadExactlyAsync(response);var reply=JsonSerializer.Deserialize<SwitcherReply>(response,Json)!;
        check(reply.Ok==(geteuid()>=1000),"Framed Unix transport authorizes the kernel UID without a web login or bearer token");
        check(File.GetUnixFileMode(path)==(UnixFileMode)438,"Desktop socket permits local connections while typed actions enforce policy");
        using var oversized=new Socket(AddressFamily.Unix,SocketType.Stream,ProtocolType.Unspecified);await oversized.ConnectAsync(new UnixDomainSocketEndPoint(path));BinaryPrimitives.WriteUInt32BigEndian(header,16385);await oversized.SendAsync(header);
        check(await oversized.ReceiveAsync(header,SocketFlags.None)==0,"Oversized local requests are closed before parsing");
    }
    static async Task Console(ProfileManager manager,ProfileAccessSettings access,List<ProfileSwitchEvent> events,Action<bool,string> check)
    {
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));await using var app=builder.Build();var device=new Appliance();using var agent=device.Agent;
        app.MapConsoleProfiles(device,manager,access);await app.StartAsync();
        try
        {
            using var client=new HttpClient{BaseAddress=new Uri(app.Urls.Single())};var menu=new ConsoleProfiles(client){Trigger="console-controller"};
            foreach(var mode in new[]{ProfileAccessSettings.Web,ProfileAccessSettings.Console,ProfileAccessSettings.Workstations})
            {access.Save(mode);using var response=await client.GetAsync("/local/profiles");check(response.IsSuccessStatusCode==(mode!=ProfileAccessSettings.Web),"Server console access follows "+mode+" policy");}
            var before=events.Count;await menu.Open();check(menu.Screen.Options.Any(o=>o.Key=='u'&&o.Enabled)&&events.Count==before,"Opening console picker offers Unload all without applying");
            await menu.Select('u');check(menu.Screen.Title=="Review unload all"&&menu.Screen.Options[0].Key=='0'&&events.Count==before,"Console unload review starts on Back and requires separate confirmation");
            await menu.Select('0');check(events.Count==before&&menu.Screen.Id=="profiles","Backing out of console review performs no action");
            await menu.Select('u');access.Save(ProfileAccessSettings.Web);await menu.Select('y');
            check(menu.Screen.Body.Contains("disabled")&&events.Count==before&&runtimeRunning(await manager.State()),"Web-only access revokes an open console approval");
            access.Save(ProfileAccessSettings.Console);await menu.Open();await menu.Select('u');await menu.Select('y');await manager.Wait();
            check(events.TakeLast(2).All(e=>e.Action=="unload"&&e.Origin.Trigger=="console-controller"&&e.Origin.User=="server-console")&&!runtimeRunning(await manager.State()),"Explicit console confirmation unloads all and audits controller origin");
        }
        finally{await app.StopAsync();}
        static bool runtimeRunning(ProfileState state)=>state.Runtime.Instances.Length>0;
    }
    static async Task Replacing(ProfileManager manager,ProfileSwitcherBroker broker,Runtime runtime,ProfileAccessSettings access,Recipe recipe,Action<bool,string> check)
    {
        access.Save(ProfileAccessSettings.Workstations);
        await manager.Save(new("replacement","Replacement",0,[new("replacement-model","Replacement model",recipe,[],"replacement")]));
        runtime.Reached=new(TaskCreationOptions.RunContinuationsAsynchronously);runtime.Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var plan=await Preview(broker);await broker.Handle(1000,new("apply",plan.Id,plan.Digest,"controller"));
        await runtime.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var state=JsonSerializer.SerializeToElement(await broker.Handle(1000,new("state")),Json);
        check(state.GetProperty("busy").GetBoolean()&&state.GetProperty("operation").GetProperty("id").GetString()==plan.Id,"Desktop state identifies the running operation while a start is pending");
        await Denied(()=>broker.Handle(1000,new("cancel","stale-operation",Trigger:"controller")),check,"Desktop interruption cannot cancel a different operation");
        await Denied(()=>broker.Handle(1000,new("cancel",Trigger:"controller")),check,"Desktop interruption requires the displayed operation ID");
        await Denied(()=>broker.Handle(1000,new("cancel",plan.Id,Trigger:"api")),check,"Desktop interruption validates its input trigger");
        access.Save(ProfileAccessSettings.Web);
        await Denied(()=>broker.Handle(1000,new("cancel",plan.Id,Trigger:"controller")),check,"Disabling workstation controls also revokes interruption");
        access.Save(ProfileAccessSettings.Workstations);
        await broker.Handle(1000,new("cancel",plan.Id,Trigger:"controller")).WaitAsync(TimeSpan.FromSeconds(2));
        await broker.Handle(1000,new("cancel",plan.Id,Trigger:"controller"));
        check((await manager.State()).Operation?.Stage=="Cancelling","Desktop interruption is prompt and idempotent while the running action finishes");
        await Denied(()=>broker.Handle(1000,new("preview","replacement")),check,"A replacement cannot race the cancelled action before it reaches its safe boundary");
        runtime.Release.SetResult();await manager.Wait();runtime.Reached=null;runtime.Release=null;
        var replacement=JsonSerializer.SerializeToElement(await broker.Handle(1000,new("preview","replacement")),Json);
        await broker.Handle(1000,new("apply",replacement.GetProperty("id").GetString(),replacement.GetProperty("digest").GetString(),"controller"));await manager.Wait();
        check((await manager.State()).Active?.Id=="replacement"&&runtime.Instances.Keys.SequenceEqual(["replacement-model"]),"An exact reviewed replacement loads after interruption and removes the prior workload");
        // Completion can race the picker reading state and sending interruption.
        var completed=(await manager.State()).Operation!.Id;
        await broker.Handle(1000,new("cancel",completed,Trigger:"controller"));
        check((await manager.State()).Operation?.Stage=="Complete","Desktop interruption treats the same operation completing as idle without undoing it");
        await Denied(()=>manager.Cancel(completed),check,"Other cancellation callers retain the completed-operation rejection");
    }
    [DllImport("libc")]static extern uint geteuid();
    sealed class Runtime:IWorkloadRuntime
    {
        public readonly Dictionary<string,RuntimeInstance> Instances=new();public bool Fail;
        public TaskCompletionSource? Reached,Release;
        public Task<RuntimeObservation> Observe()=>Task.FromResult(new RuntimeObservation("generation",[],Instances.Values.ToArray()));
        public async Task<RuntimeInstance> Start(Workload w){if(Fail)throw new InvalidOperationException("Fixture start failed");var i=new RuntimeInstance(w.Id,w.Fingerprint,w.Id,42,"boot","http://fixture/","running",[]);Instances[w.Id]=i;Reached?.TrySetResult();if(Release!=null)await Release.Task;return i;}
        public Task Stop(RuntimeStop request){Instances.Remove(request.Id);return Task.CompletedTask;}
    }
    sealed class Gateway:IWorkloadGateway{public Task Drain(string id)=>Task.CompletedTask;public Task Publish(BackendRoute[] routes)=>Task.CompletedTask;}
}
