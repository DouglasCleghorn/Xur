using Xur.Agent;
using Xur.Control;
using Xur.Domain;

public static class StationRecoveryTests
{
    public static async Task Run(Action<bool,string> check)
    {
        // Exercise production cleanup helpers through the real durable profile
        // transition. Host-bound StationRuntime.Stop/StationSeats.Remove are not
        // called: no host units, users, seats, ACLs, GPUs or devices are changed.
        await StationRevokeFaultTests.Run(check);
        foreach(var failure in new[]{"none","seat-file","getfacl","setfacl","stat"})
        {
            var root=Path.Combine(Path.GetTempPath(),"xur-station-recovery-"+Guid.NewGuid());
            Directory.CreateDirectory(root);
            try
            {
                using var store=new ProfileStore(Path.Combine(root,"profiles"));
                var runtime=new Runtime(root){Failure=failure};
                await runtime.Access.GrantNodes("old",1001,[runtime.Device]);
                // Establish an actual durable grant receipt using mocked commands,
                // then fail only revocation. Nothing touches a real device.
                runtime.Revoking=true;
                if(failure=="seat-file")Directory.CreateDirectory(runtime.SeatFile);
                var manager=new ProfileManager(store,runtime,new Gateway());
                await manager.Save(new("replacement","Replacement",0,[runtime.Next]));
                var plan=await manager.Preview("replacement");
                await manager.Apply(new(plan.Id,plan.Digest));await manager.Wait();
                var state=await manager.State();
                if(failure!="none")
                {
                    check(state.Operation?.Stage=="Failed" && runtime.Starts==0 && runtime.Preparations==0,
                        failure+": cleanup failure blocks workstation preparation and replacement");
                    check(runtime.Instances.Count==0 && File.Exists(runtime.Receipt),
                        failure+": stopped desktop retains its device receipt until teardown succeeds");
                    var journal=store.Get<Journal>("journal","current")!;
                    var stop=Array.FindIndex(plan.Steps,s=>s.Kind=="Stop");
                    check(!journal.Done().Contains(stop) && journal.StepErrors!.ContainsKey(stop),
                        failure+": durable journal keeps failed stop incomplete for resume");
                    if(failure=="seat-file")
                    {
                        check(runtime.CleanupError is IOException or UnauthorizedAccessException
                            && runtime.CleanupError.Message.Contains("old.json") && Directory.Exists(runtime.SeatFile),
                            "Filesystem cleanup preserves the original exception and refuses a directory masquerading as a seat file");
                        Directory.Delete(runtime.SeatRoot,true);
                    }
                    else
                    {
                        check(state.Operation!.Error!.Contains(failure) && state.Operation.Error.Contains(runtime.Device)
                            && state.Operation.Error.Contains("Injected revoke failure"),
                            failure+": device-release error retains command, node and diagnostic output");
                    }
                    runtime.Failure="none";
                    // Re-open the manager using the persisted journal; retry must
                    // work even though observation no longer shows the old desktop.
                    manager=new ProfileManager(store,runtime,new Gateway());
                    await manager.Resume();await manager.Wait();state=await manager.State();
                    check(runtime.Stops==2,failure+": resume retries cleanup of a desktop already stopped");
                }
                check(state.Operation?.Stage=="Complete" && state.Active?.Id=="replacement" && runtime.Starts==1
                    && runtime.Preparations==1 && !File.Exists(runtime.Receipt),
                    failure+": verified cleanup permits exactly one replacement start");
                check(!Directory.Exists(runtime.SeatRoot) && !Directory.Exists(runtime.PolicyRoot),
                    failure+": missing runtime directories stay absent rather than recreating stale state");
                check(runtime.Events.IndexOf("revoke-complete")<runtime.Events.IndexOf("prepare")
                    && runtime.Events.IndexOf("prepare")<runtime.Events.IndexOf("start"),
                    failure+": device revocation completes before new intent and workstation start");
            }
            finally { Directory.Delete(root,true); }
        }
    }

    sealed class Runtime:IWorkloadRuntime
    {
        public readonly string SeatRoot,SeatFile,PolicyRoot,Receipt,Device;
        public readonly StationDeviceAccess Access;
        public readonly List<string> Events=[];
        public readonly Dictionary<string,RuntimeInstance> Instances=new();
        public readonly Workload Next;
        public string Failure="none";public bool Revoking;
        public int Starts,Stops,Preparations;public Exception? CleanupError;
        readonly GpuDevice gpu=new("0000:01:00.0","NVIDIA","Fixture GPU","nvidia","fixture",24576,[],[],["/dev/dri/card1"]);
        public Runtime(string root)
        {
            SeatRoot=Path.Combine(root,"run","xur","seats");SeatFile=Path.Combine(SeatRoot,"old.json");
            PolicyRoot=Path.Combine(root,"run","xur","station-network");
            var grants=Path.Combine(root,"grants");Receipt=Path.Combine(grants,"old.json");
            var boot=Path.Combine(root,"boot");File.WriteAllText(boot,"fixture-boot");
            Device=Path.Combine(root,"device");File.WriteAllText(Device,"");
            Access=new StationDeviceAccess(grants,Command,boot);
            var recipe=new Recipe("gaming-workstation","Desktop","host:plasma",[],0,"","Display",1,0,"",Kind:"Workstation",Engine:"Plasma");
            Next=new("next","Next desktop",recipe,[gpu.Pci],"next",new("nextuser",1002));
            var old=Next with{Id="old",User=new("olduser",1001)};
            Instances[old.Id]=new(old.Id,old.Fingerprint,"old-instance",1,"fixture-boot","","running",old.Gpus);
        }
        Task<ProcessResult> Command(string exe,string[] args,int seconds)
        {
            if(exe is not ("stat" or "getfacl" or "setfacl"))throw new Exception("Unexpected command: "+exe);
            if(Revoking && exe==Failure)return Task.FromResult(new ProcessResult(1,"Injected revoke failure"));
            return Task.FromResult(new ProcessResult(0,exe=="stat"?"e2:1:123\n":exe=="getfacl"?
                "user::rw-\nuser:1001:rw-\ngroup::---\nmask::rw-\nother::---\n":""));
        }
        public Task<RuntimeObservation> Observe()=>Task.FromResult(new RuntimeObservation("fixture",[gpu],Instances.Values.ToArray()));
        public async Task Stop(RuntimeStop request)
        {
            if(request.Id!="old" || request.InstanceId!="old-instance")throw new Exception("Unexpected stop identity");
            Stops++;Instances.Remove(request.Id);Events.Add("desktop-stopped");
            try
            {
                new StationNetworkPolicy(PolicyRoot).Remove(request.Id);
                FileCleanup.DeleteIfPresent(SeatFile);
                FileCleanup.DeleteIfPresent(Path.Combine(SeatRoot,"old.status"));
                await Access.Revoke(request.Id);Events.Add("revoke-complete");
            }
            catch(Exception error){CleanupError=error;throw;}
        }
        public Task Prepare(Workload[] workloads)
        {Preparations++;Events.Add("prepare");return Task.CompletedTask;}
        public Task<RuntimeInstance> Start(Workload workload)
        {
            if(File.Exists(Receipt))throw new Exception("Replacement attempted before device cleanup");
            Starts++;Events.Add("start");
            var instance=new RuntimeInstance(workload.Id,workload.Fingerprint,"next-instance",2,"fixture-boot","","running",workload.Gpus);
            Instances[workload.Id]=instance;return Task.FromResult(instance);
        }
    }
    sealed class Gateway:IWorkloadGateway
    {public Task Drain(string id)=>Task.CompletedTask;public Task Publish(BackendRoute[] routes)=>Task.CompletedTask;}
}
