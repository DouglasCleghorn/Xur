#!/usr/bin/env python3
"""Prepare the pinned VictoriaMetrics binary and license; never downloads at runtime."""
import hashlib,json,pathlib,shutil,tarfile,urllib.request
root=pathlib.Path(__file__).resolve().parents[1]
lock=json.loads((root/'eng/toolchain-lock.json').read_text())['victoriaMetrics']
cache=root/'.build/downloads';cache.mkdir(parents=True,exist_ok=True)
target=root/'.build/metrics-runtime';target.mkdir(parents=True,exist_ok=True)
def fetch(url,path,digest):
    if not path.exists() or hashlib.sha256(path.read_bytes()).hexdigest()!=digest:
        temp=path.with_suffix('.tmp')
        urllib.request.urlretrieve(url,temp)
        if hashlib.sha256(temp.read_bytes()).hexdigest()!=digest:
            temp.unlink();raise RuntimeError('VictoriaMetrics download checksum mismatch: '+url)
        temp.replace(path)
    return path
archive=fetch(lock['url'],cache/'victoriametrics.tar.gz',lock['sha256'])
with tarfile.open(archive) as tar:
    member=tar.getmember('victoria-metrics-prod')
    if not member.isfile():raise RuntimeError('VictoriaMetrics archive has no regular executable')
    with tar.extractfile(member) as source,(target/'victoria-metrics-prod').open('wb') as output:shutil.copyfileobj(source,output)
(target/'victoria-metrics-prod').chmod(0o755)
fetch(lock['licenseUrl'],target/'LICENSE',lock['licenseSha256'])
# Retain the vendored dependencies' license/copyright notices alongside the binary.
source=fetch(lock['sourceUrl'],cache/'victoriametrics-source.tar.gz',lock['sourceSha256'])
notices=target/'licenses'
if notices.exists():shutil.rmtree(notices)
with tarfile.open(source) as tar:
    count=0
    for member in tar:
        parts=pathlib.PurePosixPath(member.name).parts
        if not member.isfile() or len(parts)<3 or parts[1]!='vendor':continue
        if not parts[-1].lower().startswith(('license','copying','notice')):continue
        if '..' in parts:raise RuntimeError('Unsafe source notice path')
        output=notices.joinpath(*parts[2:]);output.parent.mkdir(parents=True,exist_ok=True)
        with tar.extractfile(member) as content,output.open('wb') as destination:shutil.copyfileobj(content,destination)
        count+=1
    if not count:raise RuntimeError('No vendored VictoriaMetrics notices found')
(target/'upstream.json').write_text(json.dumps(lock,indent=2)+'\n')
print(target)
