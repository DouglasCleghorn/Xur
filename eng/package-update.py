#!/usr/bin/env python3
"""Build, test, sign, verify in a disposable VM, and publish one app update."""
import argparse,importlib.util,json,os,pathlib,re,shutil,subprocess,time
repo=pathlib.Path(__file__).resolve().parents[1];os.chdir(repo)
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--version',default=time.strftime('%Y.%m.%d.%H%M%S',time.gmtime()))
p.add_argument('--vm',default='editor-dev',help='Running disposable installed VM with existing downloaded models')
p.add_argument('--build-only',action='store_true',help='Build and publish without running tests; record that VM validation was skipped')
a=p.parse_args()
assert re.fullmatch(r'[0-9]+(?:\.[0-9]+)+',a.version),'Version must contain numeric dot-separated components'
assert re.fullmatch(r'[a-z0-9-]+',a.vm),'Invalid VM name'
vm=repo/'.build/vms'/a.vm
if not a.build_only:
 assert json.loads((vm/'vm-manifest.json').read_text()).get('developmentVm'),'Only disposable development VMs may be used'
 os.kill(int((vm/'qemu.pid').read_text()),0)
assert not (repo/'dist/updates'/a.version).exists(),'Choose a new version; packaged releases are immutable'
cleanup_spec=importlib.util.spec_from_file_location('cleanup_build',repo/'eng/cleanup-build.py')
cleanup_module=importlib.util.module_from_spec(cleanup_spec);cleanup_spec.loader.exec_module(cleanup_module)
# package-update.sh already holds the release lock. Preserve the selected test VM.
cleanup_module.cleanup(repo,apply=True,keep_vms=[a.vm],minimum_gib=12)
evidence=repo/'.build/evidence/updates'/a.version;evidence.mkdir(parents=True,exist_ok=True)
def run(*args):
 print('Running: '+' '.join(args),flush=True)
 subprocess.run(args,check=True)
run('bash','eng/publish.sh' if a.build_only else 'eng/test-fast.sh')
run('python3','eng/context-receipt.py','verify','.build/context')
spec=importlib.util.spec_from_file_location('repository',repo/'eng/update-repository.py');module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
module.PUBLIC=repo/'.build/update-staging';module.publish(a.version)
if a.build_only:
 run('python3','eng/package-app-update.py','--version',a.version,'--skip-vm-checks')
 cleanup_module.cleanup(repo,apply=True,keep_vms=[a.vm])
 print(f'Published {a.version} (build only; tests skipped). Package and checksums: dist/updates/{a.version}/',flush=True)
 raise SystemExit(0)
run('python3','tests/Xur.Media.Tests/check-release-update.py','--name',a.vm,'--evidence',str(evidence))
run('python3','tests/Xur.Media.Tests/check-model-persistence.py','--name',a.vm)
shutil.copy2(vm/'workstation-evidence/model-persistence.json',evidence/'model-persistence.json')
run('node','tests/Xur.Media.Tests/check-diagnostics-download.cjs',a.vm,str(evidence))
run('node','tests/Xur.Media.Tests/check-files-time.cjs',a.vm,str(evidence))
shutil.copytree(repo/'.build/fast',evidence/'fast',dirs_exist_ok=True,ignore=shutil.ignore_patterns('publish.log'))
run('python3','eng/package-app-update.py','--version',a.version)
cleanup_module.cleanup(repo,apply=True,keep_vms=[a.vm])
print(f'Published {a.version}. Package and checksums: dist/updates/{a.version}/',flush=True)
