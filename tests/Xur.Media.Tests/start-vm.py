#!/usr/bin/env python3
"""UEFI ISO test. Only file-backed disposable disks are ever attached."""
import argparse, hashlib, json, os, pathlib, shutil, subprocess, socket, time
p=argparse.ArgumentParser();p.add_argument('iso',type=pathlib.Path);p.add_argument('--name',default='install');p.add_argument('--no-disks',action='store_true');p.add_argument('--extra-disk',type=pathlib.Path,action='append',default=[]);p.add_argument('--usb-boot',action='store_true');p.add_argument('--file-copy-usb',type=pathlib.Path);p.add_argument('--second-display',action='store_true');p.add_argument('--offline',action='store_true',help='Block guest outbound traffic while retaining DHCP and forwarded test APIs');p.add_argument('--memory-mib',type=int,default=24576);a=p.parse_args()
assert 2048 <= a.memory_mib <= 32768
repo=pathlib.Path(__file__).resolve().parents[2];root=repo/'.build/vms'/a.name
os.umask(0o077);root.mkdir(parents=True,exist_ok=True)
q=pathlib.Path.home()/'.local/share/xur-build/qemu/usr'
env=dict(os.environ,LD_LIBRARY_PATH=str(q/'lib/x86_64-linux-gnu'))
pidfile=root/'qemu.pid'
if pidfile.exists():
    try:os.kill(int(pidfile.read_text()),0)
    except ProcessLookupError:pidfile.unlink()
    else:raise SystemExit('VM is already running')
variables=root/'OVMF_VARS.fd'
if not variables.exists():shutil.copyfile(q/'share/OVMF/OVMF_VARS_4M.fd',variables)
args=[str(q/'bin/qemu-system-x86_64'),'-name','xur-'+a.name,'-machine','q35,accel=kvm','-cpu','host','-smp','4','-m',str(a.memory_mib),'-nodefaults',
    '-L',str(q/'share/qemu'),'-display','none','-device',f'VGA,romfile={q}/share/seabios/vgabios-stdvga.bin','-vnc','127.0.0.1:11',
    '-drive',f'if=pflash,format=raw,readonly=on,file={q}/share/OVMF/OVMF_CODE_4M.fd',
    '-drive',f'if=pflash,format=raw,file={variables}',
    '-netdev',('user,restrict=on,' if a.offline else 'user,')+'id=lan0,net=10.71.1.0/24,ipv6-net=fd71:1::/64,hostfwd=tcp:127.0.0.1:18081-:8080',
    '-device','virtio-net-pci,netdev=lan0,romfile=',
    '-netdev',('user,restrict=on,' if a.offline else 'user,')+'id=lan1,net=10.71.2.0/24,ipv6-net=fd71:2::/64,hostfwd=tcp:127.0.0.1:18082-:8080',
    '-device','e1000e,netdev=lan1,romfile=',
    '-device','virtio-serial-pci','-chardev',f'socket,path={root}/qga.sock,server=on,wait=off,id=qga','-device','virtserialport,chardev=qga,name=org.qemu.guest_agent.0',
    '-serial',f'file:{root}/console.private.log','-qmp',f'unix:{root}/qmp.sock,server=on,wait=off','-pidfile',str(pidfile),'-daemonize']
if a.second_display:args+=['-device','bochs-display,id=secondary,romfile=']
if a.usb_boot or a.file_copy_usb:
    media=a.file_copy_usb.resolve() if a.file_copy_usb else root/'installer-usb.raw'
    if a.file_copy_usb:
        assert media.is_relative_to(repo/'.build') and media.is_file()
        receipt=json.loads(media.with_suffix('.json').read_text())
        with a.iso.open('rb') as f:assert receipt['isoSha256']==hashlib.file_digest(f,'sha256').hexdigest()
    elif not media.exists():shutil.copyfile(a.iso.resolve(),media)
    args+=['-device','qemu-xhci,id=xhci','-drive',f'if=none,id=usbmedia,file={media},format=raw','-device','usb-storage,drive=usbmedia,serial=XUR-BOOT-USB001,bootindex=2']
else:args+=['-drive',f'file={a.iso.resolve()},media=cdrom,readonly=on,format=raw']
for index,disk in enumerate(a.extra_disk):
    disk=disk.resolve()
    if not disk.is_relative_to(repo/'.build') or not disk.is_file():raise SystemExit('Extra disk must be a disposable .build file')
    args+=['-drive',f'if=none,id=extra{index},file={disk},format=raw','-device',f'virtio-blk-pci,drive=extra{index},serial=XUR-CONFIG-{index:06d}']
if not a.no_disks:
    target=root/'target.raw';data=root/'data.raw'
    if not target.exists():
        with target.open('wb') as f:f.truncate(64*1024**3)
    if not data.exists():
        with data.open('wb') as f:f.truncate(128*1024**2)
        files=root/'data-files';files.mkdir(exist_ok=True);(files/'preserve.txt').write_text('Xur non-target preservation evidence\n'*1000)
        subprocess.run(['mkfs.ext4','-q','-F','-d',str(files),str(data)],check=True)
        with data.open('rb') as f:digest=hashlib.file_digest(f,'sha256').hexdigest()
        (root/'data-before.sha256').write_text(digest+'\n')
    args+=['-drive',f'if=none,id=target,file={target},format=raw','-device','virtio-blk-pci,drive=target,serial=XUR-TARGET-000001,bootindex=1',
           '-drive',f'if=none,id=data,file={data},format=raw','-device','virtio-blk-pci,drive=data,serial=XUR-DATA-KEEP001']
for attempt in range(30):
    try:
        for port in (5911,18081,18082):
            with socket.socket() as probe:
                probe.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1)
                probe.bind(('127.0.0.1',port))
        break
    except OSError:
        if attempt==29:raise SystemExit('Test ports are still in use; stop the preceding test VM')
        time.sleep(1)
subprocess.run(args,env=env,check=True)
with a.iso.open('rb') as f:iso_hash=hashlib.file_digest(f,'sha256').hexdigest()
(root/'vm-manifest.json').write_text(json.dumps({'isoSha256':iso_hash,'architecture':'x86_64','firmware':'UEFI OVMF','memoryMiB':a.memory_mib,'vcpus':4,'usbBoot':a.usb_boot or bool(a.file_copy_usb),'fileCopyUsb':receipt if a.file_copy_usb else None,'noDisks':a.no_disks,'outboundNetworkBlocked':a.offline},indent=2)+'\n')
print(json.dumps({'vm':a.name,'web':['http://127.0.0.1:18081/','http://127.0.0.1:18082/'],'serial':'Private; contains bootstrap identity and potentially QR. Excluded from release.'}))
