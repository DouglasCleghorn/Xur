import hashlib,json,pathlib,subprocess,datetime,sys
out=pathlib.Path(sys.argv[1]);out.mkdir(parents=True,exist_ok=True)
images={}
for role in ['host','installer']:
 ref=f'localhost/xur-{role}:x86_64'
 raw=subprocess.check_output(['skopeo','inspect','--raw','containers-storage:'+ref])
 data=json.loads(subprocess.check_output(['podman','image','inspect',ref]))[0]
 rpms=subprocess.check_output(['podman','run','--rm',ref,'rpm','-qa','--qf','%{NAME}-%{EPOCHNUM}:%{VERSION}-%{RELEASE}.%{ARCH}\n']).decode().splitlines()
 (out/(role+'-rpm-packages.txt')).write_text('\n'.join(sorted(rpms))+'\n')
 images[role]={'localReference':ref,'manifestDigest':'sha256:'+hashlib.sha256(raw).hexdigest(),'configDigest':data['Id'],'architecture':data['Architecture'],'os':data['Os'],'created':data['Created'],'rpmCount':len(rpms)}
(out/'images.json').write_text(json.dumps(images,indent=2)+'\n')
print(json.dumps(images))
