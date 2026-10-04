#!/usr/bin/env python3
"""Startup implementation fixtures run in C# without host systemd or disks."""
import os,pathlib,shutil,subprocess
repo=pathlib.Path(__file__).resolve().parents[2]
sdk=os.environ.get('XUR_DOTNET') or shutil.which('dotnet') or str(pathlib.Path.home()/'.local/share/xur-build/dotnet/dotnet')
subprocess.run([sdk,'run','--project',str(repo/'tests/Xur.Util.Tests'),'-c','Release','--','--boot'],cwd=repo,check=True)
