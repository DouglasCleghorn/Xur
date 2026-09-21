#!/usr/bin/env python3
"""Label the live OS tree and omit its redundant boot initramfs copy.

The stock generic ISO pipeline labels its build root but does not label the
live os-tree. Container labels prevent PID 1 transitioning to init_t on boot.
The online installer omits the OS payload; media inspection checks FAT32 file sizes. The maintained builder still produces the BIOS/UEFI hybrid layout.
"""
import json,sys
manifest=json.load(open(sys.argv[1]))
trees=[p for p in manifest['pipelines'] if p['name']=='os-tree']
assert len(trees)==1 and trees[0]['stages'][0]['type']=='org.osbuild.container-deploy'
assert not any(s['type']=='org.osbuild.selinux' for s in trees[0]['stages'])
# No Bazzite payload is embedded in the online installer.
assert not any(s['type']=='org.osbuild.skopeo' for s in trees[0]['stages']), 'Unexpected embedded OS payload'
trees[0]['stages'].append({'type':'org.osbuild.selinux','options':{
    'file_contexts':'etc/selinux/targeted/contexts/files/file_contexts',
    'exclude_paths':['/sysroot'],
    'labels':{
        '/usr/libexec/xur-live-app':'system_u:object_r:bin_t:s0',
        '/usr/libexec/xur-installer-app':'system_u:object_r:bin_t:s0',
        '/usr/lib/xur/control/Xur.Control':'system_u:object_r:bin_t:s0',
        '/usr/lib/xur/agent/Xur.Agent':'system_u:object_r:bin_t:s0',
        '/usr/lib/xur/agent/console/kmscon':'system_u:object_r:bin_t:s0',
        '/usr/lib/xur/agent/console/client':'system_u:object_r:bin_t:s0',
        '/usr/libexec/xur-run-install':'system_u:object_r:install_exec_t:s0'
    }}})
# The bootiso-tree copy stage already puts this exact initramfs at
# images/pxeboot/initrd.img. It is needed there for BIOS/UEFI boot, not again
# inside the live root. Keep upstream compression, firmware and drivers.
squashfs=[s for p in manifest['pipelines'] for s in p['stages']
          if s['type']=='org.osbuild.squashfs' and s['options']['filename']=='LiveOS/squashfs.img']
assert len(squashfs)==1, 'Expected exactly one live filesystem compression stage'
squashfs[0]['options'].setdefault('exclude_paths',[]).append('usr/lib/modules/.*/initramfs[.]img')
with open(sys.argv[2],'w') as out:json.dump(manifest,out,indent=2);out.write('\n')
