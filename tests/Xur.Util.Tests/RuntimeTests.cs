using System.Text;
using System.Text.Json.Nodes;
using Xur.IO;

namespace Xur.Util.Tests;

static class RuntimeTests
{
    static JsonObject Deployment(string digest) => new() { ["image"] = new JsonObject { ["version"] = digest, ["imageDigest"] = digest, ["image"] = new JsonObject { ["image"] = OsUpdate.Channel } } };
    static JsonObject Status(bool staged = false, bool available = true, bool rollback = false)
    {
        var booted = Deployment("old");if(available)booted["cachedUpdate"] = Deployment("new")["image"]!.DeepClone();
        return new() { ["status"] = new JsonObject { ["booted"] = booted, ["staged"] = staged ? Deployment("new") : null, ["rollback"] = Deployment("previous") }, ["spec"] = new JsonObject { ["bootOrder"] = rollback ? "rollback" : "default" } };
    }
    public static async Task OsUpdates()
    {
        foreach(var scenario in new[]{"switch-stages","upgrade-stages","noop","signature-fails","rollback","queued","disabled","check","check-fails"})
        {
            using var fixture=new Fixture();fixture.Write("installed","");fixture.Write("upstream.json",new JsonObject{["channel"]=OsUpdate.Channel}.ToJsonString());
            var state=fixture.PathOf("updates");Directory.CreateDirectory(state);
            var commands=new List<string[]>();var staged=scenario=="queued";var rolled=false;
            var runtime=new OsRuntime();OsUpdate? updater=null;
            runtime.OnRun=(command,seconds)=>Task.FromResult(Encoding.UTF8.GetBytes(Status(staged,scenario!="noop",rolled).ToJsonString()));
            runtime.Logged=(command,log,seconds,token)=>
            {
                commands.Add(command);
                Verify.That(updater!.PowerBlocked()==(command[1] is "switch" or "rollback" || command[1]=="upgrade"&&command.Length==2),"Only live deployment writes block power actions");
                if(scenario is "signature-fails" or "check-fails")throw new IOException("fixture failure");
                if(command[1]=="switch"&&scenario == "switch-stages")staged=true;
                if(command.SequenceEqual(new[]{"bootc","upgrade"})&&scenario!="noop")staged=true;
                if(command[1]=="rollback")rolled=true;
                return Task.CompletedTask;
            };
            updater=new(state,fixture.PathOf("upstream.json"),fixture.PathOf("installed"),runtime,new DurableFiles());
            if(scenario=="disabled")new DurableFiles().WriteJson(Path.Combine(state,"settings.json"),new JsonObject{["automatic"]=false});
            var action=scenario is "rollback"?"rollback":scenario is "queued" or "disabled"?"auto":scenario is "check" or "check-fails"?"check":"stage";
            if(scenario is "signature-fails" or "check-fails")await Verify.Reject(()=>updater.Execute(action,user:0),"Command failure propagates and is persisted");
            else await updater.Execute(action,user:0);
            Verify.That(!updater.PowerBlocked(),"Successful and failed deployments release the power guard");
            if(scenario is "queued" or "disabled")Verify.That(commands.Count==0,"Queued deployments and disabled automatic updates do not run commands");
            if(scenario=="switch-stages")Verify.That(commands.Count==1&&commands[0].SequenceEqual(new[]{"bootc","switch","--enforce-container-sigpolicy",OsUpdate.Channel}),"Switch persists signature enforcement and skips a redundant upgrade");
            if(scenario=="upgrade-stages")Verify.That(commands.Count==2&&commands[1].SequenceEqual(new[]{"bootc","upgrade"}),"An identical switch spec still runs a same-channel upgrade");
            if(scenario is "signature-fails" or "check-fails")Verify.That(JsonValues.Text(DurableFiles.ReadObject(Path.Combine(state,"operation.json"))["stage"])=="Failed","Failure state survives process exit");
            if(scenario is "switch-stages" or "upgrade-stages" or "rollback")Verify.That(JsonValues.Text(DurableFiles.ReadObject(Path.Combine(state,"operation.json"))["message"])=="Reboot to finish","Queued deployments report reboot completion without requesting a reboot");
            using(var operation=FileLease.TryAcquire(Path.Combine(state,"lock")))
            {
                runtime.OnRun=(_,_)=>throw new Exception("Busy status must not query bootc");
                if(File.Exists(Path.Combine(state,"deployment.json")))Verify.That(JsonValues.Boolean((await updater.Observe())["busy"]),"Busy progress uses its cached deployment rather than waiting for bootc");
                await Verify.Reject(()=>updater.Execute("stage",user:0),"Concurrent operations are rejected");
            }
            fixture.Write("updates/operation.json","{broken");
            Verify.That(!JsonValues.Boolean((await updater.Execute("power-status",user:0))!["blocked"]),"Power recovery ignores broken state and does not contact bootc");
        }
        await Verify.Reject(()=>Task.Run(()=>OsUpdate.VerifyStage(new(){["current"]=new JsonObject{["digest"]="old"},["available"]=new JsonObject{["digest"]="new"}})),"No-op staging cannot report a new deployment");
        await Verify.Reject(()=>Task.Run(()=>OsUpdate.VerifyStage(new(){["pending"]=new JsonObject{["image"]="wrong"}})),"Unexpected staged images are rejected");
        var malformed=Status();malformed["status"]!["booted"]!["image"]!["imageDigest"]=null;
        await Verify.Reject(()=>Task.Run(()=>OsUpdate.Snapshot(malformed)),"Incomplete bootc deployment metadata is rejected");
        using(var fixture=new Fixture())
        {
            var updater=new OsUpdate(fixture.Root,fixture.PathOf("missing"),fixture.PathOf("installed"),new FakeRuntime(),new DurableFiles());
            Verify.That(!JsonValues.Boolean((await updater.Execute("power-status",user:0))!["blocked"]),"Power guard works before installation with no configuration");
            await Verify.Reject(()=>updater.Execute("stage",user:0),"Installed marker is required before updates");
            await Verify.Reject(()=>updater.Execute("power-status",user:1000),"Non-root update access is rejected");
            using var first=FileLease.TryAcquire(fixture.PathOf("power.lock"));
            Verify.That(FileLease.Busy(fixture.PathOf("power.lock")),"A real held flock is observed as busy");
            using var cancel=new CancellationTokenSource(30);
            await Verify.Reject(()=>FileLease.Acquire(fixture.PathOf("power.lock"),cancel.Token),"Waiting for a deployment guard is cancellable");
            File.CreateSymbolicLink(fixture.PathOf("unsafe"),fixture.PathOf("power.lock"));
            await Verify.Reject(()=>Task.Run(()=>FileLease.TryAcquire(fixture.PathOf("unsafe"))),"Lock aliases are rejected");
            using var log=new MemoryStream();
            var result=await CommandRunner.RunLogged("/bin/sh",["-c","printf stdout; printf stderr >&2"],log,5);
            var text=Encoding.UTF8.GetString(log.ToArray());Verify.That(result==0&&text.Contains("stdout")&&text.Contains("stderr"),"Long-running command logs stream both pipes into one sink");
            using var brokenLog=new BrokenLog();var timer=System.Diagnostics.Stopwatch.StartNew();
            await Verify.Reject(()=>CommandRunner.RunLogged("/bin/sh",["-c","printf data; sleep 20"],brokenLog,10),"A failed log sink stops a command while stderr is still waiting");
            Verify.That(timer.Elapsed<TimeSpan.FromSeconds(5),"Log failure is reported promptly rather than waiting for the command deadline");
            using var deadline=new CancellationTokenSource(30);
            await Verify.Reject(()=>CommandRunner.RunLogged("/bin/sh",["-c","sleep 20"],log,30,deadline.Token),"Logged commands kill their process tree on cancellation");
        }
    }
    public static async Task Progress()
    {
        using var fixture=new Fixture();using var errors=new StringWriter();var path=fixture.PathOf("progress.json");var progress=new DownloadProgress(path,errors);
        progress.Feed("\x1b[2KFetching layers ▰▰▱ 32/128\r\n └ Fetching ▰ 8.00 MiB/16.00 MiB (2.00 MiB/s) chunk abc");
        Verify.That(DurableFiles.ReadObject(path).Count==1,"A split redraw does not publish unfinished byte counters");progress.Feed("\r\n");
        Verify.That(JsonValues.Text(DurableFiles.ReadObject(path)["byteProgress"])!.Contains("8.00 MiB"),"Byte counters survive UTF-8 terminal redraws");
        progress.Feed("\x1b[1A\r\x1b[2KFetching layers ▰▰▱ 33/128\r\n");
        Verify.That(DurableFiles.ReadObject(path).Count==1,"A new layer count clears old byte counters");
        progress.Feed("Fetching layers █ 129/128\nFetching layers █ 1/0\nFetching layers █ 1/1000001\n"+new string('x',10000));
        Verify.That(progress.Pending.Length==4096&&JsonValues.Text(DurableFiles.ReadObject(path)["layerProgress"])!.Contains("33/128"),"Invalid counters and incomplete input are bounded");
        Verify.That((File.GetUnixFileMode(path)&(UnixFileMode)63)==0,"Installer counters stay private");
        var unavailable=new DownloadProgress(fixture.PathOf("missing/progress.json"),errors);unavailable.Feed("Fetching layers █ 0/128\n");
        Verify.That(unavailable.Disabled,"Progress write errors disable reporting without failing installation");
        progress.Feed("\noriginal failure\n");Verify.That(errors.ToString().Contains("original failure"),"Ordinary stderr remains visible");
        fixture.Write("bundle.json",new JsonObject{["id"]=new string('a',64)}.ToJsonString());Verify.That(InstallerMetadata.BundleId(fixture.PathOf("bundle.json"))==new string('a',64),"Installer bundle identity is read without Python");
        fixture.Write("bundle.json","{\"id\":\"../escape\"}");await Verify.Reject(()=>Task.Run(()=>InstallerMetadata.BundleId(fixture.PathOf("bundle.json"))),"Malformed bundle identifiers are rejected");
        Directory.CreateDirectory(fixture.PathOf("var"));fixture.Write("channel","nightly\n");fixture.Write("release.json","{\"sequence\":42}");
        InstallerMetadata.Save(fixture.Root,fixture.PathOf("channel"),fixture.PathOf("release.json"),new DurableFiles());
        Verify.That(JsonValues.Text(DurableFiles.ReadObject(fixture.PathOf("etc/xur/application-updates.json"))["channel"])=="nightly"&&JsonValues.Integer(DurableFiles.ReadJson(fixture.PathOf("var/lib/xur/app/highest-sequence.json")))==42,"Installer persists channel and anti-replay sequence without Python");
        fixture.Write("release.json","{\"sequence\":-1}");await Verify.Reject(()=>Task.Run(()=>InstallerMetadata.Save(fixture.Root,fixture.PathOf("channel"),fixture.PathOf("release.json"),new DurableFiles())),"Invalid release sequence cannot overwrite selected metadata");
        fixture.Write("channel","invalid");await Verify.Reject(()=>Task.Run(()=>InstallerMetadata.Save(fixture.Root,fixture.PathOf("channel"),null,new DurableFiles())),"Unsupported installer channels are rejected");
        DeviceAccess.Check(["/dev/null"]);Verify.That(true,"Native device probe opens a permitted node read/write");
        await Verify.Reject(()=>Task.Run(()=>DeviceAccess.Check([fixture.PathOf("missing-device")])),"Native device probe reports missing devices");
        await Verify.Reject(()=>Task.Run(()=>DeviceAccess.Check(["relative"])),"Native device probe rejects relative device paths");
    }
    sealed class BrokenLog:MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken token=default)=>ValueTask.FromException(new IOException("Fixture disk full"));
    }
    sealed class OsRuntime:FakeRuntime
    {
        public Func<string[],Stream,int,CancellationToken,Task>? Logged;
        public override Task RunLogged(string[] command,Stream log,int seconds,CancellationToken token=default)=>Logged!(command,log,seconds,token);
    }
}
