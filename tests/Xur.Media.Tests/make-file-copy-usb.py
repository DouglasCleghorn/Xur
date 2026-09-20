#!/usr/bin/env python3
"""Build a Rufus-style GPT/FAT32 file-copy fixture in the disposable Fedora builder.

Uses the supplied EFI fallback loader. This is not a Windows Rufus GUI test.
Only newly created regular image files are formatted; no physical disk arguments.
"""
import argparse, hashlib, json, os, pathlib, shutil, subprocess, tempfile
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('iso',type=pathlib.Path);p.add_argument('output',type=pathlib.Path)
p.add_argument('--label',default='XUR_USB')
a=p.parse_args()
assert os.geteuid()==0,'Run in the disposable Fedora builder as root'
assert a.iso.is_file()
assert not a.output.exists() and a.output.suffix=='.raw'
for tool in ('mkfs.vfat','sfdisk','losetup','mount','umount','udevadm'):
    assert shutil.which(tool), 'Missing fixture tool: '+tool
assert a.label.isascii() and a.label.replace('_','').isalnum() and len(a.label)<=11
def sha(path):
    with path.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
def run(*args,**kw):return subprocess.run(list(map(str,args)),check=True,**kw)
def output(*args):return subprocess.check_output(list(map(str,args)),text=True).strip()
with a.output.open('xb') as f:f.truncate(10*1024**3)
# One FAT32 EFI partition, bootable using the supplied fallback loader.
run('sfdisk',a.output,input='label: gpt\nstart=2048,type=C12A7328-F81F-11D2-BA4B-00A0C93EC93B\n',text=True,stdout=subprocess.DEVNULL)
loop=output('losetup','--find','--show','--partscan',a.output)
with tempfile.TemporaryDirectory(prefix='xur-file-copy-') as temp:
    temp=pathlib.Path(temp);source=temp/'iso';dest=temp/'usb';source.mkdir();dest.mkdir()
    mounted=[]
    try:
        run('udevadm','settle')
        run('mkfs.vfat','-F','32','-n',a.label,loop+'p1',stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        run('mount','-o','loop,ro',a.iso,source);mounted.append(source)
        run('mount','-t','vfat',loop+'p1',dest);mounted.append(dest)
        run('cp','-rL','--no-preserve=mode,ownership,timestamps',str(source)+'/.',dest)
        verified=0;largest=0
        for file in source.rglob('*'):
            if file.is_file():
                assert sha(file)==sha(dest/file.relative_to(source)),str(file)
                largest=max(largest,file.stat().st_size);verified+=1
        # Match Rufus's replacement of ISO labels in GRUB configs, exercising a
        # USB label different from the ISO label without changing boot commands.
        for name in ('EFI/BOOT/grub.cfg','boot/grub2/grub.cfg'):
            file=dest/name;text=file.read_text();assert 'XUR_SETUP_A' in text
            file.write_text(text.replace('XUR_SETUP_A',a.label))
        assert (dest/'EFI/BOOT/BOOTX64.EFI').is_file()
        assert largest<=2**32-1
        run('sync','-f',dest)
    finally:
        for mount in reversed(mounted):run('umount',mount)
        run('losetup','--detach',loop)
receipt={'schema':1,'isoSha256':sha(a.iso),'usbSha256':sha(a.output),'filesystem':'FAT32','partitionTable':'GPT','label':a.label,'filesVerifiedBeforeLabelReplacement':verified,'largestFileBytes':largest,'loader':'Supplied EFI/BOOT/BOOTX64.EFI','windowsRufusGuiExecuted':False}
a.output.with_suffix('.json').write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps(receipt))
