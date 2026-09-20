#!/usr/bin/env python3
import hashlib,json,pathlib,subprocess
repo=pathlib.Path(__file__).resolve().parents[2];root=repo/'.build/fixtures';root.mkdir(parents=True,exist_ok=True)
receipts=[]
for name in ('answer-one','answer-two'):
    disk=root/(name+'.raw');fs=root/(name+'.ext4');files=root/(name+'-files');files.mkdir(exist_ok=True)
    if disk.exists():raise SystemExit('Fixture exists; refusing to overwrite')
    (files/'xur.yaml').write_text('schemaVersion: 1\n# Discovery fixture; never authorizes installation.\n')
    with fs.open('wb') as f:f.truncate(64*1024**2)
    subprocess.run(['mkfs.ext4','-q','-F','-d',str(files),str(fs)],check=True)
    with disk.open('wb') as f:f.truncate(128*1024**2)
    subprocess.run(['sfdisk','--no-reread',str(disk)],input=b'label: gpt\nstart=2048,size=131072,type=0FC63DAF-8483-4772-8E79-3D69D8477DE4\n',check=True,stdout=subprocess.DEVNULL)
    with disk.open('r+b') as out,fs.open('rb') as source:
        out.seek(1024**2)
        while block:=source.read(1024**2):out.write(block)
    with disk.open('rb') as f:digest=hashlib.file_digest(f,'sha256').hexdigest()
    receipts.append({'file':str(disk.relative_to(repo)),'sha256':digest})
(root/'answer-hashes.json').write_text(json.dumps(receipts,indent=2)+'\n')
print(json.dumps({'fixturesCreated':len(receipts),'layout':'GPT disk with root-level xur.yaml on ext4 child partition'}))
