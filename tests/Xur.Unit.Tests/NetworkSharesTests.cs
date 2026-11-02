using System.Text.Json;
using Xur.Agent;
using Xur.Control;
using Xur.Domain;
static class NetworkSharesTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.Combine(Directory.GetCurrentDirectory(),".build","network-share-tests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var calls=new List<(string Exe,string[] Args)>();bool mounted=false,offline=false,wrongSource=false,wrongUuid=false;
        SmbConnection? connection=null;
        Task<ProcessResult> Run(string exe,string[] args,int timeout)
        {
            calls.Add((exe,args));
            if(exe=="systemd-escape")return Task.FromResult(new ProcessResult(0,"var-mnt-xur\\x2dsmb-test.mount\n"));
            if(exe=="systemctl"&&args[0]=="start"&&args[1].EndsWith(".mount"))mounted=true;
            if(exe=="systemctl"&&args[0]=="stop")mounted=false;
            if(exe=="findmnt"&&args.Contains("--target"))return Task.FromResult(new ProcessResult(0,JsonSerializer.Serialize(new{filesystems=new[]{new{fstype="ext4",uuid=wrongUuid?"wrong-uuid":"fixture-uuid"}}})));
            if(exe=="findmnt")return Task.FromResult(new ProcessResult(mounted?0:1,mounted?JsonSerializer.Serialize(new{filesystems=new[]{new{source=wrongSource?"//other/wrong":"//nas.home/data",fstype="cifs"}}}):""));
            if(exe=="podman"&&args.Contains("smbclient")&&offline)return Task.FromResult(new ProcessResult(1,"private authentication diagnostic"));
            return Task.FromResult(new ProcessResult(0,""));
        }
        var shares=new NetworkShares(root,root+"/units",root+"/mounts",Run,path=>path);
        async Task Reject(Func<Task> action,string name)
        {
            try{await action();throw new Exception("Accepted: "+name);}catch(InvalidOperationException){check(true,name);}
        }
        var request=new SmbConnectionRequest("Datasets","nas.home","data","remote-user","secret-value",ReadOnly:false);
        await Reject(()=>shares.AddConnection(request with{Password="bad\nusername=root"}),"Credential directive injection is rejected");
        await Reject(()=>shares.AddConnection(request with{Server="nas\n[Mount]"}),"Server unit injection is rejected");
        await Reject(()=>shares.AddConnection(request with{Share="../data"}),"Share traversal is rejected");
        await Reject(()=>shares.AddConnection(request with{Uid=0}),"Remote mounts cannot impersonate root");
        connection=await shares.AddConnection(request);
        var credential=Path.Combine(root,"network-shares",connection.Id+".credentials");
        check(File.GetUnixFileMode(credential)==(UnixFileMode.UserRead|UnixFileMode.UserWrite),"SMB credentials are owner-only");
        check(!File.ReadAllText(root+"/network-shares/settings.json").Contains("secret-value"),"Public connection settings never contain passwords");
        check(!JsonSerializer.Serialize(await shares.Status()).Contains("secret-value"),"Status never returns passwords");
        var unit=File.ReadAllText(root+"/units/var-mnt-xur\\x2dsmb-test.mount");
        check(unit.Contains("vers=3,")&&unit.Contains(",seal")&&unit.Contains("nosuid,nodev,noexec")&&!unit.Contains("secret-value"),"Mount units require SMB3 encryption and reference protected credentials");
        check(File.GetUnixFileMode(shares.MountPath(connection.Id))==UnixFileMode.None,"Unmounted backing directories deny ordinary file writes");
        var recipe=new Recipe("test","Test","sha256:"+new string('a',64),[],0,"/","CPU",0,0,"Test",Kind:"Container",Engine:"Podman",Container:new([],[]));
        var ordinary=new Workload("test","Test",recipe,[],"test");
        var workload=ordinary with{NetworkShares=[new(connection.Id)]};
        var access=await shares.RequireMounts(workload);
        check(access.Arguments.Any(a=>a.Contains("bind-propagation=rprivate")&&a.Contains(",ro=true,")),"Assigned shares bind privately and read-only by default");
        check(access.Arguments.Contains("--group-add")&&access.Arguments.Contains("1000"),"Assigned containers receive the mount group without DAC override capabilities");
        check(calls.Any(c=>c.Exe=="podman"&&c.Args.Contains("smbclient")),"Startup verifies a fresh remote SMB session");
        check(NetworkShares.WorkloadUnitProperties("xur-workload-test",["a.mount","b.mount"]).Contains("--property=BindsTo=a.mount b.mount"),"All assigned mounts participate in workload lifetime dependencies");
        check(calls.All(c=>!string.Join(' ',c.Args).Contains("secret-value")),"Passwords never appear in process arguments");
        check(workload.Fingerprint!=ordinary.Fingerprint&&ordinary.Fingerprint==(ordinary with{NetworkShares=[]}).Fingerprint,"Share assignments affect identity without changing existing workload fingerprints");
        check(workload.Fingerprint!=(workload with{NetworkShares=[new(connection.Id,false)]}).Fingerprint,"Changing write permission restarts the workload");
        await Reject(()=>shares.ConnectionAction(new(connection.Id,"disconnect"),[workload]),"Assigned active or pending workloads prevent disconnect");
        await Reject(()=>shares.ConnectionAction(new(connection.Id,"remove"),[workload]),"Assigned active or pending workloads prevent removal");
        offline=true;
        await Reject(()=>shares.RequireMounts(workload),"Offline SMB servers block startup even when their mount is present");
        offline=false;wrongSource=true;
        await Reject(()=>shares.RequireMounts(workload),"A different filesystem source at the same path blocks startup");
        wrongSource=false;
        await Reject(()=>shares.RequireMounts(workload with{NetworkShares=[new("smb-"+new string('b',32))]}),"Deleted connection references fail closed");
        await Reject(()=>Task.Run(()=>NetworkSharePolicy.Validate(workload with{NetworkShares=[new(connection.Id),new(connection.Id)]})),"Duplicate network assignments are rejected");
        await Reject(()=>Task.Run(()=>NetworkSharePolicy.Validate(workload with{Recipe=recipe with{Kind="Workstation"}})),"Unsupported workload kinds reject network bind assignments");
        await Reject(()=>Task.Run(()=>NetworkSharePolicy.Validate(workload with{Recipe=recipe with{Container=new([new("volume","/shared")],[])}})),"Overlapping container volume destinations are rejected");
        var reread=await new NetworkShares(root,root+"/units",root+"/mounts",Run).Status();
        check(reread.Connections.Single().Connection.Id==connection.Id,"Connection identity persists across agent replacement");
        await shares.ConnectionAction(new(connection.Id,"remove"),[]);
        check(!File.Exists(credential)&&(await shares.Status()).Connections.Length==0,"Removing connections removes credentials and metadata");
        var local=root+"/local folder";Directory.CreateDirectory(local);
        var hostedSaved=await shares.AddHosted(new("data",local,"reader","hosting-secret",1000,1000));
        check(!File.Exists(root+"/network-shares/server/password"),"Hosting discards plaintext passwords after account creation");
        check(calls.All(c=>!string.Join(' ',c.Args).Contains("hosting-secret")),"Hosting passwords never enter process arguments");
        await shares.SetServer(new("192.0.2.10",true));
        var serverUnit=File.ReadAllText(root+"/units/xur-smb.service");
        check(serverUnit.Contains("192.0.2.10:445:445/tcp")&&!serverUnit.Contains("--network=host"),"Hosting publishes TCP 445 on the selected address only");
        check(serverUnit.Contains("bind-nonrecursive")&&serverUnit.Contains("ro=true")&&serverUnit.Contains("RequiresMountsFor="),"Hosting binds selected folders read-only without exporting nested mounts");
        check(serverUnit.Contains(root+"/network-shares/server:/state:rw")&&!serverUnit.Contains(root+"/network-shares:/state"),"Hosting never receives outgoing connection credentials");
        check((await Processes.Run("sh",["-n",root+"/network-shares/server/start.sh"])).ExitCode==0&&(await Processes.Run("sh",["-n",root+"/network-shares/server/verify.sh"])).ExitCode==0,"Generated hosting scripts pass shell syntax validation");
        check(File.ReadAllText(root+"/network-shares/server/verify.sh").Contains("fixture-uuid"),"Hosting checks the saved backing filesystem on each service start");
        await Reject(()=>shares.RemoveHosted(hostedSaved.Id),"Enabled hosting must stop before removing shares");
        await shares.SetServer(new("192.0.2.10",false));wrongUuid=true;
        await Reject(()=>shares.SetServer(new("192.0.2.10",true)),"A replacement or missing backing filesystem blocks hosting");wrongUuid=false;
        await shares.RemoveHosted(hostedSaved.Id);
        check((await shares.Status()).Hosted.Length==0&&Directory.Exists(local),"Removing a hosted share retains its files");
        var hosted=new SmbHostedShare("smb-test","data","/srv/data","reader",1000,1000,true,"uuid");
        var conf=NetworkShares.SambaConfiguration([hosted]);
        check(conf.Contains("server min protocol = SMB3_00")&&conf.Contains("server smb encrypt = required")&&conf.Contains("map to guest = Never")&&conf.Contains("valid users = reader")&&conf.Contains("follow symlinks = no"),"Hosting requires encrypted SMB3, explicit accounts and no symlink escapes");
        await Reject(()=>shares.SetServer(new("0.0.0.0",true)),"Hosting cannot bind to every interface");
        foreach(var path in new[]{"/etc","/srv/../etc","/var/lib/xur","/srv/foo\nbar"})
            await Reject(()=>Task.Run(()=>NetworkShares.ValidateHostedPath(path)),"Sensitive or malformed hosted paths are rejected: "+path.Replace('\n',' '));
        check(!ApiKeys.Allows("automation","POST","/api/storage/network/hosted")&&ApiKeys.Allows("automation","GET","/api/storage/network"),"API keys cannot mint SMB credentials or publish host folders");
        check(!ConfigExport.Read(root).ContainsKey("network-shares/settings.json"),"Ordinary configuration export excludes share secrets");
        File.WriteAllText(root+"/network-shares/settings.json","invalid");
        check((await shares.RequireMounts(ordinary)).Arguments.Length==0,"Unassigned workloads do not depend on network-share configuration health");
        File.SetUnixFileMode(shares.MountPath(connection.Id),UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        Directory.Delete(root,true);
    }
}
