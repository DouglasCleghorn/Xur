#!/usr/bin/env python3
"""Exercise native implementations with fixture commands; leave host state untouched."""
import os,pathlib,shutil,subprocess
repo=pathlib.Path(__file__).resolve().parents[2]
sdk=os.environ.get('XUR_DOTNET') or shutil.which('dotnet') or str(pathlib.Path.home()/'.local/share/xur-build/dotnet/dotnet')
subprocess.run([sdk,'run','--project',str(repo/'tests/Xur.Util.Tests'),'-c','Release','--','--display'],cwd=repo,check=True)
