using Xur.Agent;
using Xur.Domain;
static class StorageUsageTests
{
    public static async Task Run(Action<bool,string> check)
    {
        const string nvme="""{"nvme_smart_health_information_log":{"percentage_used":20,"data_units_written":100000000,"available_spare":100}}""";
        var e=StorageEnduranceReader.Parse(nvme);
        check(e.RemainingPercent==80 && e.RemainingBytes==null,"NVMe reported wear gives remaining percent without extrapolating a byte budget");
        e=StorageEnduranceReader.Parse("""{"endurance_used":{"current_percent":25},"host_writes":{"units":1000000,"bytes_per_unit":33554432}}""");
        check(e.RemainingPercent==75 && e.RemainingBytes==null,"ATA lifetime host writes are not a reported remaining byte budget");
        e=StorageEnduranceReader.Parse("""{"nvme_smart_health_information_log":{"percentage_used":0,"data_units_written":1000}}""");
        check(e.RemainingPercent==100 && e.RemainingBytes==null,"Zero used wear preserves 100 percent without inventing a byte budget");
        e=StorageEnduranceReader.Parse("""{"nvme_smart_health_information_log":{"percentage_used":150,"data_units_written":1000}}""");
        check(e.RemainingPercent==0 && e.RemainingBytes==null,"Exhausted endurance never reports negative remaining writes");
        e=StorageEnduranceReader.Parse("""{"scsi_percentage_used_endurance_indicator":14}""");
        check(e.RemainingPercent==86 && e.RemainingBytes==null,"Older SCSI wear percentages remain usable without write counters");
        e=StorageEnduranceReader.Parse("""{"endurance_used":{"current_percent":10},"nvme_smart_health_information_log":{"percentage_used":20},"host_writes":{"units_s":"9007199254740993","units":9007199254740992,"bytes_per_unit":512}}""");
        check(e.RemainingPercent==90 && e.RemainingBytes==null,"Endurance reader prefers normalized wear and never derives bytes from wide write counters");
        e=StorageEnduranceReader.Parse("""{"endurance_used":{"current_percent":1},"host_writes":{"units_s":"79228162514264337593543950335","bytes_per_unit":512000}}""");
        check(e.RemainingPercent==99 && e.RemainingBytes==null,"Unused write counters cannot overflow or hide the valid wear percentage");
        foreach(var json in new[]{"{}","""{"nvme_smart_health_information_log":{"available_spare":100}}""","""{"endurance_used":{"current_percent":-1}}""","""{"endurance_used":{"current_percent":256}}""","""{"ata_smart_attributes":{"table":[{"id":177,"value":99}]}}"""})
            check(StorageEnduranceReader.Parse(json).RemainingPercent==null,"Missing, invalid and unrecognized vendor wear is unavailable: "+json);
        var calls=0;
        Task<ProcessResult> Run(string exe,string[] args)
        {
            calls++;check(exe=="smartctl" && args.SequenceEqual(new[]{"--json","--info","--attributes","--nocheck=standby,2","/dev/nvme0n1"}),"Endurance reads are read-only SMART queries with standby checks");
            return Task.FromResult(new ProcessResult(8,nvme+"warning"){StandardOutput=nvme,StandardError="warning"});
        }
        e=await StorageEnduranceReader.Read("/dev/nvme0n1",false,Run);
        check(e.RemainingPercent==80 && calls==1,"SMART health exit bits and stderr do not discard valid stdout readings");
        await StorageEnduranceReader.Read("/dev/sda",true,Run);await StorageEnduranceReader.Read("/dev/sdb",null,Run);
        check(calls==1,"Rotating and unknown media never receive endurance probes");
        e=await StorageEnduranceReader.Read("/dev/sdc",false,(_,_)=>Task.FromResult(new ProcessResult(2,"{}")));
        check(e.RemainingPercent==null && e.UnavailableReason!.Contains("asleep"),"Unsupported or sleeping devices retain an explicit unavailable reason");
        e=await StorageEnduranceReader.Read("/dev/sdc",false,(_,_)=>throw new System.ComponentModel.Win32Exception());
        check(e.UnavailableReason=="SMART tool unavailable","A missing host smartctl tool does not fail the storage scan");
        e=await StorageEnduranceReader.Read("/dev/sdc",false,(_,_)=>Task.FromResult(new ProcessResult(0,"bad json")));
        check(e.RemainingPercent==null && e.UnavailableReason!=null,"Malformed SMART data stays visibly unavailable");
        StorageFilesystem root=new("/dev/mapper/root","btrfs",["/","/var"],1000,400,500),boot=new("/dev/sda1","ext4",["/boot"],100,40,50),other=new("/dev/loop0","ext4",["/other"],100,0,100);
        StorageDiskUsage a=new("/dev/sda","SSD A",2000,["/dev/dm-0","/dev/sda1"],["/","/var","/boot"]),b=new("/dev/sdb","SSD B",2000,["/dev/dm-0"],["/","/var"]),c=new("/dev/sdc","Unused",500,[],[]);
        var rows=StorageUsageRow.Build(new(null,false,[root,boot,other],[a,b,c],[]));
        check(rows.Length==4 && rows[0].Disks.SequenceEqual(new[]{a,b}) && rows[1].Disks.SequenceEqual(new[]{a}),"Unified rows match mapper aliases and retain all observed disks for a shared filesystem");
        check(rows.Count(r=>r.Filesystem==root)==1 && rows.Sum(r=>r.Filesystem?.Bytes??0)==1200,"Shared filesystem capacity is not duplicated across physical disks");
        check(rows[2].Disks.Length==0 && rows[2].Filesystem==other && rows[3].Filesystem==null && rows[3].Disks[0]==c,"Unmapped filesystems and unmounted disks both remain visible");
    }
}
