#!/usr/bin/env bash
set -euo pipefail
cd "${1:?Build directory}"
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --parallel 2
rm -rf output
mkdir -p output/lib output/plugins output/licenses output/fonts
install -m755 build/xur-profile-switcher output/xur-profile-switcher
install -m755 launch output/launch
cp README.md output/
# Plugin paths belong to this runtime; do not load another bundle's Qt build.
for directory in platforms tls wayland-shell-integration wayland-decoration-client wayland-graphics-integration-client; do
    if test -d "/usr/lib64/qt6/plugins/$directory"; then
        cp -a "/usr/lib64/qt6/plugins/$directory" output/plugins/
    fi
done
python3 - <<'PY'
import hashlib,json,pathlib,re,shutil,subprocess
root=pathlib.Path('output');libraries=set()
upstream=json.loads(pathlib.Path('upstream-lock.json').read_text())
for binary in [root/'xur-profile-switcher',*root.glob('plugins/**/*.so')]:
    output=subprocess.check_output(['ldd',str(binary)],text=True)
    if 'not found' in output:raise RuntimeError(output)
    for path in re.findall(r'=> (/\S+)',output):
        p=pathlib.Path(path)
        # The supported Fedora/Bazzite host supplies its own libc and loader.
        if re.match(r'lib(c|m|pthread|dl|rt)\.so',p.name):continue
        libraries.add(p)
packages=set()
for path in sorted(libraries):
    shutil.copy2(path,root/'lib'/path.name)
    packages.add(subprocess.check_output(['rpm','-qf','--qf','%{NAME}',str(path.resolve())],text=True).strip())
records=[]
installed={}
for line in subprocess.check_output(['rpm','-qa','--qf','%{NAME}\t%{SOURCERPM}\n'],text=True).splitlines():
    name,source=line.split('\t');installed.setdefault(source,set()).add(name)
checked=set()
for package in sorted(packages):
    record=subprocess.check_output(['rpm','-q','--qf','%{NAME}\t%{VERSION}-%{RELEASE}\t%{LICENSE}\t%{SOURCERPM}',package],text=True).split('\t')
    records.append(dict(zip(['package','version','license','sourceRpm'],record)))
    source=record[3]
    if source in checked:continue
    checked.add(source);count=0
    # Fedora subpackages often share a versioned notice directory owned by a
    # sibling RPM. Use RPM's actual license-file flags and source relationship.
    for provider in sorted(installed.get(source,{package})):
        listing=subprocess.check_output(['rpm','-q','--qf','[%{FILENAMES}\t%{FILEFLAGS:fflags}\n]',provider],text=True)
        for line in listing.splitlines():
            name,flags=line.split('\t');notice=pathlib.Path(name)
            if not notice.is_file():continue
            licensed_header=notice.suffix in ('.h','.hpp') and re.search(r'Permission is (hereby )?granted|GNU (Lesser |Library )?General Public License',notice.read_text(errors='replace')[:16000])
            if not ('l' in flags or notice.name.upper().startswith(('COPYING','LICENSE','COPYRIGHT','NOTICE')) or licensed_header):continue
            target=root/'licenses'/source/notice.relative_to('/')
            target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(notice,target);count+=1
    if not count and package in upstream:
        # Fedora omits XCB keysyms' COPYING; retain the matching upstream notice.
        locked=upstream[package];notice=pathlib.Path(locked['notice'])
        if not record[1].startswith(locked['version']+'-') or hashlib.sha256(notice.read_bytes()).hexdigest()!=locked['noticeSha256']:
            raise RuntimeError('Update the upstream license notice for '+source)
        target=root/'licenses'/source/'COPYING';target.parent.mkdir(parents=True,exist_ok=True)
        shutil.copy2(notice,target);(target.parent/'upstream.json').write_text(json.dumps(locked,indent=2)+'\n');count+=1
    if not count:raise RuntimeError('Missing upstream license notices for '+source)
(root/'dependencies.json').write_text(json.dumps(records,indent=2)+'\n')
PY
tar -czf profile-switcher-runtime.tar.gz -C output .
