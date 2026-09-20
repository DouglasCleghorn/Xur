#!/usr/bin/env python3
"""Cleanup boundary checks, using generated files in a temporary tree."""
import importlib.util,json,os,pathlib,tempfile,time
repo=pathlib.Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location('cleanup',repo/'eng/cleanup-build.py');m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
with tempfile.TemporaryDirectory() as tmp:
 root=pathlib.Path(tmp);build=root/'.build';vms=build/'vms';now=time.time()
 def file(p,old=True):
  p.parent.mkdir(parents=True,exist_ok=True);p.write_text('fixture')
  if old:os.utime(p,(now-10*86400,now-10*86400))
  return p
 for n in range(7):
  d=vms/str(n);file(d/'target.raw');(d/'vm-manifest.json').write_text('{}')
 # Three most recent disks are protected regardless of age.
 for n in (0,1,2):os.utime(vms/str(n)/'target.raw',(now-4*86400,now-4*86400))
 (vms/'3'/'.keep').touch();(vms/'4'/'vm-manifest.json').write_text('{"developmentVm":true}')
 secret=file(vms/'6'/'account.private.json');source=file(root/'src'/'keep.raw')
 outside=file(root/'outside.raw');(vms/'escape').mkdir();(vms/'escape'/'vm-manifest.json').write_text('{}');(vms/'escape'/'target.raw').symlink_to(outside)
 chosen=m.candidates(root,keep_vms=['5'],now=now)
 assert chosen==[vms/'6'/'target.raw'],chosen
 with chosen[0].open() as handle:assert chosen[0] in m.in_use(chosen)
 m.cleanup(root,keep_vms=['5']);assert chosen[0].exists()
 m.cleanup(root,apply=True,keep_vms=['5']);assert not chosen[0].exists()
 assert secret.exists() and source.exists() and outside.exists()
 file(vms/'6'/'target.raw');file(vms/'other'/'overlay.qcow2')
 assert not m.candidates(root,keep_vms=['5'],now=now)
print('Cleanup preservation, dry-run, symlink, open-file and overlay checks passed.')
