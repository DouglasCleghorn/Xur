"""Source-only release manifest, independent of Git availability and local artifacts."""
from pathlib import Path
import os

ROOTS=('src','tools','tests','catalog','os','eng','docs','.github')
TOP=('README.md','AGENTS.md','.gitignore','Directory.Build.props','global.json','LICENSE')
EXCLUDED={'bin','obj','node_modules','__pycache__','.pytest_cache','.venv','venv',
          '.build','dist','artifacts','evidence','TestResults','test-results','playwright-report','.git','.vs','.idea','.vscode'}
SUFFIXES=('.pyc','.pyo','.user','.suo','.key','.pfx','.p12','.pem','.iso','.qcow2','.vmdk','.vdi','.vhd','.vhdx','.raw','.img','.tar','.tar.gz','.tar.zst','.tgz','.core','.dmp')

def source_files(repo):
    repo=Path(repo).resolve()
    def include(path):
        name=path.name
        if path.is_symlink():
            raise ValueError('Source archives must not follow symlinks: '+str(path))
        if path.relative_to(repo).as_posix()=='os/bootc/application-update-key.pem':return True
        return (name not in {'.env','.DS_Store','Thumbs.db'} and
                (not name.startswith('.env.') or name=='.env.example') and
                '.private.' not in name and not name.endswith(SUFFIXES))
    files=[]
    for top in ROOTS:
        for base,dirs,names in os.walk(repo/top):
            dirs[:]=sorted(d for d in dirs if d not in EXCLUDED)
            files.extend(p for name in names if include(p:=Path(base)/name))
    files.extend(repo/name for name in TOP if (repo/name).is_file())
    return sorted(files)

if __name__=='__main__':
    import json
    root=Path(__file__).resolve().parents[1];files=source_files(root)
    print(json.dumps({'files':len(files),'bytes':sum(p.stat().st_size for p in files),
                      'paths':[str(p.relative_to(root)) for p in files]},indent=2))
