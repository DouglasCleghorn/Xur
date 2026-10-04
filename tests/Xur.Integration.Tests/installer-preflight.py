#!/usr/bin/env python3
"""Exercise native implementations with fixture commands; leave host state untouched."""
import os,pathlib,shutil,subprocess
repo=pathlib.Path(__file__).resolve().parents[2]
sdk=os.environ.get('XUR_DOTNET') or shutil.which('dotnet') or str(pathlib.Path.home()/'.local/share/xur-build/dotnet/dotnet')
subprocess.run([sdk,'run','--project',str(repo/'tests/Xur.Util.Tests'),'-c','Release','--','--preflight'],cwd=repo,check=True)
prepare=(repo/'eng/prepare-rootfs.py').read_text();container=(repo/'os/installer/Containerfile').read_text()
assert "('tailscale/tailscaled','usr/bin/tailscaled')" in prepare and "'usr/sbin/" not in prepare
assert '/usr/libexec/xurutil installer check-runtime' in container and 'ln -s /usr/bin/ldconfig' not in container
assert container.index('COPY rootfs/ /')<container.index('xurutil installer check-runtime')
