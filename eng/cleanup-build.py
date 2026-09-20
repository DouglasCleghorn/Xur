#!/usr/bin/env python3
"""Prune obsolete generated media. Dry-run unless --apply; never touches source or keys."""
import argparse, fcntl, json, os, pathlib, re, shutil, time
GIB=1024**3

def candidates(repo, keep_vms=(), now=None):
    now=time.time() if now is None else now
    build=repo/'.build'
    def safe(p):
        return p.is_file() and not any(x.is_symlink() for x in [p,*p.parents] if x!=repo.parent) and p.resolve().is_relative_to(build.resolve())
    def old(p,days=3):return safe(p) and now-p.stat().st_mtime>days*86400
    result=[]
    vms=build/'vms'
    # Raw media only: never remove a backing image while an overlay might depend on it.
    overlays=any(vms.rglob('*.qcow2'))
    dirs=[d for d in vms.glob('*') if d.is_dir() and not d.is_symlink()]
    recent=sorted([d for d in dirs if (d/'target.raw').exists()],key=lambda d:(d/'target.raw').stat().st_mtime,reverse=True)[:3]
    for d in dirs:
        if overlays or d.name in keep_vms or d in recent or (d/'.keep').exists():continue
        try:meta=json.loads((d/'vm-manifest.json').read_text())
        except (OSError,ValueError):continue
        if meta.get('developmentVm'):continue
        try:os.kill(int((d/'qemu.pid').read_text()),0);continue
        except (FileNotFoundError,ProcessLookupError):pass
        except (ValueError,PermissionError):continue
        media=list(d.glob('*.raw'))
        if media and all(old(p) for p in media):result.extend(media)
    snapshots=sorted([d for d in (build/'releases').glob('*') if d.is_dir() and not d.is_symlink()],key=lambda d:d.stat().st_mtime,reverse=True)
    for d in [*snapshots[2:],build/'previous-release']:
        if (d/'.keep').exists():continue
        result.extend(p for p in d.glob('*.iso') if old(p))
    # Retain all published bundles for clients/rollback. Only prune redundant staging archives.
    stage=build/'update-staging';latest=(stage/'latest').read_text().strip() if (stage/'latest').is_file() else ''
    for p in stage.glob('*.tar.gz'):
        identity=p.name.removesuffix('.tar.gz')
        if re.fullmatch('[a-f0-9]{64}',identity) and identity!=latest and old(p):result.append(p)
    return sorted(set(result))

def in_use(paths):
    """Conservative process/loop-device protection, including command-line media references."""
    used=set();texts=[]
    for proc in pathlib.Path('/proc').glob('[0-9]*'):
        try:texts.append((proc/'cmdline').read_bytes().decode(errors='replace'))
        except (OSError,ProcessLookupError):pass
        try:
            for fd in (proc/'fd').iterdir():
                try:used.add(fd.resolve())
                except OSError:pass
        except OSError:pass
    for backing in pathlib.Path('/sys/block').glob('loop*/loop/backing_file'):
        try:used.add(pathlib.Path('/'+backing.read_text().strip().lstrip('/')).resolve())
        except OSError:pass
    return {p for p in paths if p.resolve() in used or any(str(p) in t for t in texts)}

def cleanup(repo,apply=False,keep_vms=(),minimum_gib=0):
    before=shutil.disk_usage(repo).free
    paths=candidates(repo,keep_vms);busy=in_use(paths)
    removed=[];bytes_removed=0
    for path in paths:
        if path in busy:continue
        size=path.stat().st_blocks*512
        if apply:path.unlink()
        removed.append(str(path.relative_to(repo)));bytes_removed+=size
    after=shutil.disk_usage(repo).free
    report={'mode':'apply' if apply else 'dry-run','files':removed,'skippedInUse':[str(p.relative_to(repo)) for p in sorted(busy)],'candidateAllocatedGiB':round(bytes_removed/GIB,2),'freeBeforeGiB':round(before/GIB,2),'freeAfterGiB':round(after/GIB,2)}
    print(json.dumps(report,indent=2),flush=True)
    if apply:
        (repo/'.build/cleanup-last.json').write_text(json.dumps(report,indent=2)+'\n')
    if minimum_gib and after<minimum_gib*GIB:raise RuntimeError(f'Only {after/GIB:.1f} GiB free; release requires {minimum_gib} GiB. See eng/cleanup-build.py. Builder and development VM disks are intentionally preserved.')
    return report

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--apply',action='store_true');p.add_argument('--keep-vm',action='append',default=[]);p.add_argument('--minimum-free-gib',type=int,default=0);a=p.parse_args()
    repo=pathlib.Path(__file__).resolve().parents[1];(repo/'.build').mkdir(exist_ok=True)
    with (repo/'.build/package-update.lock').open('a') as lock:
        fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
        cleanup(repo,a.apply,a.keep_vm,a.minimum_free_gib)
