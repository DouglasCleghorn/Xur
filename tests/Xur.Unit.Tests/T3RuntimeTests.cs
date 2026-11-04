using System.Text.Json;
using Xur.Agent;
using Xur.Domain;

static class T3RuntimeTests
{
    static async Task Done(T3RuntimeMode modes)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(modes.Busy)await Task.Delay(10,timeout.Token);
    }
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(".build/evidence/t3-runtime-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var prepared=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var preparing=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var applied=new List<string>();T3RuntimeMode modes=null!;
            modes=new(root,async _=>{preparing.TrySetResult();await prepared.Task;},()=>{applied.Add(modes.Read().Mode);return Task.CompletedTask;});
            check(modes.Read() is {Mode:"Container",Stage:"Ready"},"Existing installations retain container mode until explicitly changed");
            modes.Change("Native");await preparing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(modes.Read() is {Mode:"Container",Target:"Native",Stage:"Preparing"}&&applied.Count==0,"Target prerequisites prepare before publishing a new runtime or stopping the current connection");
            var conflict=false;try{modes.Change("Container");}catch(InvalidOperationException){conflict=true;}
            check(conflict,"Concurrent runtime switches cannot race on the same T3 home");
            prepared.SetResult();await Done(modes);
            check(modes.Read() is {Mode:"Native",Stage:"Ready",Target:null}&&applied.SequenceEqual(["Native"]),"A prepared runtime switch applies once and persists the selected mode");
            modes=new(root,_=>Task.CompletedTask,()=>Task.CompletedTask);
            check(modes.Read().Mode=="Native","Native runtime selection survives an agent restart");
            modes.Change("Container");await Done(modes);
            check(modes.Read() is {Mode:"Container",Stage:"Ready"},"Switching back retains the same configuration identity");
            var editing=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishEdit=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var edit=modes.ChangeSettings(async()=>{editing.SetResult();await finishEdit.Task;return true;});
            await editing.Task;
            var editConflict=false;try{modes.Change("Native");}catch(InvalidOperationException){editConflict=true;}
            finishEdit.SetResult();await edit;
            check(editConflict&&modes.Read().Mode=="Container","Access settings and runtime switches cannot interleave during a handoff");
            foreach(var invalid in new[]{"Docker","native","","../Native"})
            {var rejected=false;try{modes.Change(invalid);}catch(InvalidOperationException){rejected=true;}check(rejected&&modes.Read().Mode=="Container","Invalid runtime modes are rejected without changing the saved selection");}

            var failures=root+"/prepare-failure";int starts=0;
            modes=new(failures,_=>throw new IOException("Offline fixture"),()=>{starts++;return Task.CompletedTask;});
            modes.Change("Native");await Done(modes);
            check(starts==0&&modes.Read() is {Mode:"Container",Stage:"Failed"},"Failed preflight leaves the current T3 server untouched");
            applied.Clear();
            modes=new(root+"/rollback",_=>Task.CompletedTask,()=>{
                applied.Add(modes.Read().Mode);
                if(modes.Read().Mode=="Native")throw new IOException("Fixture startup failure");return Task.CompletedTask;
            });
            modes.Change("Native");await Done(modes);
            check(applied.SequenceEqual(["Native","Container"])&&modes.Read() is {Mode:"Container",Stage:"Failed"},"Failed target startup restores the previous runtime using the original home and connection identity");

            var recovery=root+"/recovery";Directory.CreateDirectory(recovery);
            await File.WriteAllTextAsync(recovery+"/t3-runtime.json","""{"mode":"Native","target":"Native","previous":"Container","stage":"Switching"}""");
            starts=0;modes=new(recovery,_=>throw new Exception("Must not reprepare"),()=>{starts++;return Task.CompletedTask;});modes.Resume();await Done(modes);
            check(starts==1&&modes.Read() is {Mode:"Native",Stage:"Ready"},"An agent restart resumes an interrupted handoff without creating a second installation");
            await File.WriteAllTextAsync(recovery+"/t3-runtime.json","""{"mode":"Container","target":"Native","previous":"Container","stage":"RollingBack","error":"Fixture interrupted switch"}""");
            starts=0;modes=new(recovery,_=>Task.CompletedTask,()=>{starts++;return Task.CompletedTask;});modes.Resume();await Done(modes);
            check(starts==1&&modes.Read() is {Mode:"Container",Stage:"Failed"},"An interrupted rollback restores the previous runtime after restart");
            await Lifecycle(check,root+"/lifecycle");
            await Native(check,root+"/native");
        }
        finally{Directory.Delete(root,true);}
    }
    sealed class Runtime:IWorkloadRuntime
    {
        public Func<string> Selected=()=>"Container";
        public string Actual="Container";
        public RuntimeInstance? Current;
        public int Starts,Stops;
        public Task<RuntimeObservation> Observe()=>Task.FromResult(new RuntimeObservation("g",[],Current==null?[]:[Current with{Fingerprint=Actual==Selected()?Current.Fingerprint:"old-mode"}]));
        public Task<RuntimeInstance> Start(Workload w)
        {Starts++;Actual=Selected();Current=new(w.Id,w.Fingerprint,"same-home-"+Starts,Starts,"boot","http://127.0.0.1:3773/","running",[]);return Task.FromResult(Current);}
        public Task Stop(RuntimeStop request){Stops++;Current=null;return Task.CompletedTask;}
    }
    static async Task Lifecycle(Action<bool,string> check,string root)
    {
        var runtime=new Runtime();var compute=new PersistedCompute(root,runtime);var workload=new Workload("same-t3","T3",T3CodeTests.Recipe(),[],"coding");
        compute.Register("t3-code",workload);await compute.Reconcile();T3RuntimeMode modes=null!;
        modes=new(root,_=>Task.CompletedTask,async()=>{compute.Retry("t3-code");await compute.Reconcile();if(compute.Services().Single().Error is {} error)throw new IOException(error);});
        runtime.Selected=()=>modes.Read().Mode;
        modes.Change("Native");await Done(modes);
        check(runtime.Starts==2&&runtime.Stops==1&&runtime.Actual=="Native"&&runtime.Current?.Endpoint=="http://127.0.0.1:3773/"&&compute.Services().Single().Workload==workload,
            "Persisted Compute switches one server at a time without changing the saved workload, volume or connection address");
        compute.SetEnabled("t3-code",false);await compute.Reconcile();modes.Change("Container");await Done(modes);
        check(runtime.Starts==2&&runtime.Current==null&&!compute.Services().Single().Enabled&&modes.Read().Mode=="Container",
            "Changing runtime while T3 is disabled preserves disabled intent and starts no server");
        compute.SetEnabled("t3-code",true);await compute.Reconcile();
        check(runtime.Starts==3&&runtime.Actual=="Container"&&compute.Services().Single().Workload.Recipe.Container!.Mounts[0].Volume==workload.Recipe.Container!.Mounts[0].Volume,
            "Re-enabling T3 after a disabled mode switch adopts the selected runtime and original home");
    }
    static async Task Native(Action<bool,string> check,string root)
    {
        Directory.CreateDirectory(root);var home=root+"/existing-home";Directory.CreateDirectory(home);
        Directory.CreateDirectory(home+"/.t3");await File.WriteAllTextAsync(home+"/.t3/credentials","saved fixture pairing");
        var original=await File.ReadAllBytesAsync(home+"/.t3/credentials");
        var storage=new T3Storage(root,new DataStorage(root));storage.Save(new(false,false));
        var workload=new Workload("same-t3","T3",T3CodeTests.Recipe(),[],"coding");var mode="Native";var invocation=new string('a',32);var active=false;var pid=123;var unitState="active";var stopCount=0;string description="";
        Task<ProcessResult> Execute(string exe,string[] args,int seconds)
        {
            if(exe=="podman")return Task.FromResult(new ProcessResult(0,JsonSerializer.Serialize(new[]{new{Mountpoint=home,Labels=new Dictionary<string,string>{{"io.xur.volume",workload.Recipe.Container!.Mounts[0].Volume}}}})));
            if(args[0]=="show")return Task.FromResult(new ProcessResult(0,active?$"LoadState=loaded\nActiveState={unitState}\nMainPID={pid}\nInvocationID={invocation}\nDescription={description}\n":"LoadState=not-found\n"));
            if(args[0]=="stop"){active=false;stopCount++;return Task.FromResult(new ProcessResult(0,""));}
            return Task.FromResult(new ProcessResult(0,""));
        }
        var native=new T3NativeRuntime(root,storage,()=>mode,Execute,()=>Task.FromResult<string[]>(["--group-add","44"]));
        check(await native.Home(workload)==home,"Native mode resolves the verified existing named volume and never creates a fresh home");
        var args=await native.Arguments(workload,home);description=args.Single(a=>a.StartsWith("--property=Description="))[23..];
        check(args.Contains("--property=User=1000")&&args.Contains("--property=Group=1000")&&args.Contains("--property=SupplementaryGroups=44"),"Native service retains home ownership and host GPU permission groups");
        check(args.Single(a=>a.StartsWith("--property=BindPaths=")).Contains(home+":/home/node")&&args.Contains("--property=Environment=HOME=/home/node PATH="+T3NativeRuntime.ToolPath+" XUR_T3_DOCKER=0"),"Native service preserves the exact home path and disables the container daemon");
        check(args.Contains("--property=RootDirectory="+root+"/t3-native-root")&&args.Contains(T3CodeWorkload.NativeLauncher)&&!args.Contains("--privileged")&&!args.Contains("podman"),"Native service uses host binaries and filesystem aliases without an OCI container");
        active=true;var current=(await native.Inspect(workload))!;
        check(current.Fingerprint==workload.Fingerprint&&current.Endpoint=="http://127.0.0.1:3773/","Native inspection preserves the workload identity and the paired client's connection address");
        pid=0;unitState="deactivating";
        check((await native.Inspect(workload)) is {State:"stopping"},"An exiting native service remains observable until its cgroup has stopped, even after its main PID exits");
        pid=123;unitState="active";mode="Container";
        check(await native.Inspect(workload)!=null,"A running native server is found after switching intent even if its ownership marker was lost");
        NetworkShares.Write(root+"/t3-native-owner",workload.Fingerprint);
        check((await native.Inspect(workload))!.Fingerprint!=workload.Fingerprint,"Switching modes detects the running native service for an orderly handoff");
        var command=T3CodeRuntime.Exec(current.InstanceId,["python3","-c","fixture"],true);
        check(command[0]=="nsenter"&&command.Contains("--root")&&command.Contains("--reuid=1000")&&command.Contains("HOME=/home/node")&&!command.Contains("podman"),"Native account setup and pairing enter the inspected service namespace and drop root");
        var container=T3CodeRuntime.Exec("container-id",["python3","-c","fixture"],true);
        check(container[0]=="podman"&&container.Contains("--interactive")&&container.Contains("1000:1000"),"Container setup retains interactive access as the same unprivileged user");
        var refused=false;try{await native.Stop(workload,new(workload.Id,"native:123:"+new string('b',32),123,current.BootId));}catch(InvalidOperationException){refused=true;}
        check(refused&&stopCount==0,"A stale native instance identity cannot stop a replacement service");
        await native.Stop(workload,new(workload.Id,current.InstanceId,123,current.BootId));
        check(stopCount==1&&await native.Inspect(workload)==null&&(await File.ReadAllBytesAsync(home+"/.t3/credentials")).SequenceEqual(original),"Native stop verifies shutdown and retains pairing credentials byte for byte");
    }
}
