using System.Text.Json;
using Xur.Agent;
using Xur.Domain;
static class UsbLogExportTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"xur-usb-logs-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try
        {
            string serial="USB-1",uuid="usb-uuid",transport="usb",fs="vfat",rootFs="",mounted="";bool readOnly=false,readOnlyMount=false,failUnmount=false,changedAtMount=false,failSync=false;int mounts=0,unmounts=0;
            Task<JsonElement[]> Nodes()
            {
                return Task.FromResult(new[]{JsonSerializer.SerializeToElement(new{path="/dev/sdz",type="disk",tran=transport,serial,fstype=rootFs,size=32000000000L,ro=false,model="Test USB",children=new[]{new{path="/dev/sdz1",type="part",fstype=fs,uuid,partuuid="part-id",label="XUR",ro=readOnly,mountpoints=new string?[]{mounted.Length==0?null:mounted}}}})});
            }
            Task<ProcessResult> Run(string exe,string[] args)
            {
                if(exe=="findmnt")return Task.FromResult(new ProcessResult(0,readOnlyMount?"ro,nosuid":"rw,nosuid"));
                if(exe=="mount")
                {
                    check(args.Contains("rw,nosuid,nodev,noexec,umask=077")&&args.Contains("/dev/sdz1"),"USB log export mounts only the selected filesystem with restricted options");
                    mounted=args[^1];mounts++;if(changedAtMount)uuid="replacement";
                }
                if(exe=="sync"){check(File.Exists(args[^1]),"USB logs are flushed after the report is written");if(failSync)return Task.FromResult(new ProcessResult(1,"flush failed"));}
                if(exe=="umount")
                {
                    unmounts++;if(failUnmount)return Task.FromResult(new ProcessResult(1,"busy"));
                    // Simulate the unmounted USB files disappearing from the mount directory.
                    foreach(var file in Directory.GetFiles(mounted))File.Move(file,Path.Combine(root,Path.GetFileName(file)));
                    mounted="";
                }
                return Task.FromResult(new ProcessResult(0,""));
            }
            var exporter=new UsbLogExport(root,Run,Nodes,"inst.stage2=hd:LABEL=XUR");
            var volume=(await exporter.List()).Single();check(volume.InstallerMedia,"USB log export recognizes the installer by boot-source ancestry");
            var receipt=await exporter.Save(volume.Id,"Error: fixture\npassword=usb-secret");
            var report=Directory.GetFiles(root,"xur-diagnostics-*.txt").Single();
            check(receipt.Message.Contains(Path.GetFileName(report))&&File.ReadAllText(report).Contains("Error: fixture")&&!File.ReadAllText(report).Contains("usb-secret")&&mounts==1&&unmounts==1,"USB export writes a redacted report and unmounts the temporary filesystem");
            await exporter.Save(volume.Id,"Second report");check(Directory.GetFiles(root,"xur-diagnostics-*.txt").Length==2,"Repeated USB exports retain previous report files");
            File.WriteAllText(root+"/approved.ks","ignoredisk --only-use=sdz\n");check((await exporter.List()).Length==0,"The approved installation disk is excluded even if connected by USB");File.Delete(root+"/approved.ks");
            readOnly=true;check((await exporter.List()).Length==0,"Read-only USB block devices are never unlocked for log export");readOnly=false;
            transport="nvme";check((await exporter.List()).Length==0,"Internal disks cannot receive USB log exports");transport="usb";
            rootFs="iso9660";check((await exporter.List()).Length==0,"Embedded writable-looking EFI partitions inside raw ISO media are never used for log export");rootFs="";
            fs="iso9660";check((await exporter.List()).Length==0,"Raw ISO filesystems cannot receive log exports");fs="vfat";
            mounted=root+"/existing";Directory.CreateDirectory(mounted);readOnlyMount=true;
            check((await exporter.List()).Length==0,"Read-only boot mounts are not remounted writable for logs");readOnlyMount=false;
            var before=mounts;var beforeUnmounts=unmounts;await exporter.Save(volume.Id,"Existing mount report");
            check(mounts==before&&unmounts==beforeUnmounts&&Directory.GetFiles(mounted).Length==1,"Existing writable USB mounts are preserved after export");mounted="";
            serial="replacement";bool rejected=false;try{await exporter.Save(volume.Id,"must not write");}catch(InvalidOperationException){rejected=true;}
            check(rejected&&mounts==before,"Changing USB identity invalidates a previously selected volume");serial="USB-1";
            changedAtMount=true;rejected=false;try{await exporter.Save(volume.Id,"must not write");}catch(InvalidOperationException){rejected=true;}
            check(rejected&&unmounts==beforeUnmounts+1,"USB identity is rechecked after mount and failure still unmounts the volume");changedAtMount=false;uuid="usb-uuid";
            failSync=true;rejected=false;beforeUnmounts=unmounts;try{await exporter.Save(volume.Id,"Flush failure");}catch(IOException e){rejected=e.Message.Contains("flushing failed");}
            check(rejected&&unmounts==beforeUnmounts+1,"Failed USB flush still cleans up its mount and cannot report export success");failSync=false;
            failUnmount=true;rejected=false;try{await exporter.Save(volume.Id,"Unmount failure");}catch(IOException e){rejected=e.Message.Contains("unmount failed");}
            check(rejected,"USB unmount errors cannot be reported as safe export completion");
        }
        finally{Directory.Delete(root,true);}
    }
}
