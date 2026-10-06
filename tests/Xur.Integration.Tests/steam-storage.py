#!/usr/bin/env python3
"""Native fixtures; --filesystem PATH optionally exercises real multi-user Btrfs/XFS sharing."""
import argparse,fcntl,json,os,pathlib,shutil,struct,subprocess,tempfile,types
repo=pathlib.Path(__file__).resolve().parents[2]
parser=argparse.ArgumentParser();parser.add_argument('--filesystem',type=pathlib.Path);args=parser.parse_args()
evidence=repo/'.build/evidence/steam-storage';evidence.mkdir(parents=True,exist_ok=True)
sdk=os.environ.get('XUR_DOTNET') or shutil.which('dotnet') or str(pathlib.Path.home()/'.local/share/xur-build/dotnet/dotnet')
if not args.filesystem: subprocess.run([sdk,'run','--project',str(repo/'tests/Xur.Util.Tests'),'-c','Release','--','--steam'],cwd=repo,check=True)
class Sharing:
    def __init__(self,database,minimum_age=0):self.database=database
    def run(self,accounts):
        native=os.environ.get('XUR_UTIL_TESTS',str(repo/'.build/xurutil-tests/Xur.Util.Tests'))
        result=subprocess.run([native,'--steam-fixture',self.database,json.dumps(accounts)],check=True,capture_output=True,text=True)
        return json.loads(result.stdout)
module=types.SimpleNamespace(CHUNK=1024*1024,Sharing=Sharing)
def account(home,uid):return dict(uid=uid,home=str(home))
def fixture(home):
    common=home/'.local/share/Steam/steamapps/common';(common/'Example Game').mkdir(parents=True);return common

def extents(path):
    # FIEMAP, Linux UAPI linux/fiemap.h. SYNC ensures disk extents are allocated.
    data = bytearray(struct.pack('=QQIIII', 0, 2**64-1, 1, 0, 128, 0) + bytes(56*128))
    with path.open('rb') as file:
        fcntl.ioctl(file.fileno(), 0xC020660B, data, True)
    count = struct.unpack_from('=I', data, 20)[0]
    return [struct.unpack_from('=QQQQQIIII', data, 32 + 56*i) for i in range(count)]


real = False
if args.filesystem:
    assert os.geteuid() == 0, 'Real multi-user fixture requires root to assign distinct UIDs'
    with tempfile.TemporaryDirectory(dir=args.filesystem) as temporary:
        root = pathlib.Path(temporary)
        payload = os.urandom(3*module.CHUNK + 123)
        accounts, files = [], []
        for i in range(4):
            home = root/f'user{i}'
            common = fixture(home)
            file = common/'Example Game/payload.bin'
            file.write_bytes(payload)
            (common/'Example Game/version.bin').write_bytes(bytes([i if i == 3 else 0])*module.CHUNK)
            accounts.append(account(home, 11001+i))
            files.append(file)
            for path in [home, *home.rglob('*')]:
                os.chown(path, 11001+i, 11001+i)
            home.chmod(0o700)
        before = [extents(file) for file in files]
        assert len({e[0][1] for e in before}) == 4, before
        inode_numbers = [file.stat().st_ino for file in files]
        report = module.Sharing(str(root/'pairs.sqlite'), minimum_age=0).run(accounts)
        assert report['errors'] == 0 and report['differentRanges'] > 0, report
        assert report['sharedBytes'] >= 3*len(payload), report
        assert [file.stat().st_ino for file in files] == inode_numbers
        shared = [extents(file) for file in files]
        assert len({e[0][1] for e in shared}) == 1, shared
        assert all(e[0][5] & 0x2000 for e in shared), shared
        again = module.Sharing(str(root/'pairs.sqlite'), minimum_age=0).run(accounts)
        assert again['cachedPairs'] > 0, again
        with files[1].open('r+b') as output:
            output.write(b'Z'*4096)
            output.flush()
            os.fsync(output.fileno())
        assert files[1].read_bytes() == b'Z'*4096 + payload[4096:]
        assert all(files[i].read_bytes() == payload for i in (0, 2, 3))
        assert extents(files[1])[0][1] != extents(files[0])[0][1]
        changed = module.Sharing(str(root/'pairs.sqlite'), minimum_age=0).run(accounts)
        assert changed['differentRanges'] > 0 and changed['errors'] == 0, changed
        shutil.rmtree(root/'user0')
        assert files[2].read_bytes() == payload and files[3].read_bytes() == payload
        mounted=files[2].parent/'mounted';mounted.mkdir()
        subprocess.run(['mount','--bind',str(root/'user3'),str(mounted)],check=True)
        try:
            native=os.environ.get('XUR_UTIL_TESTS',str(repo/'.build/xurutil-tests/Xur.Util.Tests'))
            rejected=subprocess.run([native,'--tree-fixture',str(files[2].parent),'mounted'],capture_output=True,text=True)
            assert rejected.returncode!=0 and 'nested mounts' in rejected.stderr,rejected.stderr
        finally:subprocess.run(['umount',str(mounted)],check=True)
        real = True

print(json.dumps(dict(suite='SteamStorage',result='Passed',realExtentSharing=real)))
