using Xur.Agent;
using Xur.Domain;

static class HostFilesystemTests
{
    public static async Task Run(Action<bool,string> check)
    {
        await HostFilesystem.RequireBtrfs((exe,args,timeout)=>
        {
            check(exe=="findmnt"&&args.SequenceEqual(new[]{"--noheadings","--output","FSTYPE","--target","/sysroot"}),"Btrfs requirement checks bootc's physical sysroot rather than the composefs root");
            return Task.FromResult(new ProcessResult(0,"btrfs\n"));
        });
        foreach(var filesystem in new[]{"ext4","xfs","overlay",""})
        {
            try{await HostFilesystem.RequireBtrfs((exe,args,timeout)=>Task.FromResult(new ProcessResult(0,filesystem)));throw new Exception("Unsupported root was accepted");}
            catch(InvalidOperationException e){check(e.Message.Contains("reinstall")&&e.Message.Contains("legacy"),"Unsupported root requires a backup and reinstall: "+filesystem);}
        }
        try{await HostFilesystem.RequireBtrfs((exe,args,timeout)=>Task.FromResult(new ProcessResult(1,"btrfs")));throw new Exception("Failed filesystem observation was accepted");}
        catch(InvalidOperationException){check(true,"Failed filesystem observation cannot satisfy the Btrfs requirement");}
        var disk=new Disk("/dev/nvme0n1","","","","",64L<<30,"",[],[]);
        var layout=new PlainRootLayout();var kickstart=layout.Kickstart(disk);
        check(layout.Actions.Any(a=>a.Contains("Btrfs root"))&&kickstart.Contains("part btrfs.01 --fstype=btrfs --size=8192 --grow --ondisk=nvme0n1\n")&&kickstart.Contains("btrfs / --label=xur-system btrfs.01\n"),"Disk review and Kickstart both select a Btrfs root on the approved disk");
        check(kickstart.Contains("part /boot --fstype=ext4")&&kickstart.Contains("part /boot/efi --fstype=efi"),"Btrfs root preserves the separate ext4 boot and EFI partitions");
        Directory.CreateDirectory(".build/evidence/steam-storage");
        File.WriteAllText(".build/evidence/steam-storage/layout.ks",kickstart);
    }
}
