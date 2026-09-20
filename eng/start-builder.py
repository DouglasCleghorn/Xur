#!/usr/bin/env python3
"""Start a disposable UEFI Fedora build VM with user-local QEMU.

All writable disks are files beneath XUR_BUILD_ROOT. No physical block device
or host directory is passed through. SSH is forwarded on loopback only.
"""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess

repo = Path(__file__).resolve().parents[1]
root = Path(os.environ.get("XUR_BUILD_ROOT", Path.home() / ".local/share/xur-build"))
cache = Path(os.environ.get("XUR_BUILD_CACHE", Path.home() / ".cache/xur-build"))
vm = root / "vm"
vm.mkdir(parents=True, exist_ok=True, mode=0o700)
pidfile = vm / "qemu.pid"
if pidfile.exists():
    try:
        os.kill(int(pidfile.read_text()), 0)
    except ProcessLookupError:
        pidfile.unlink()
    else:
        raise SystemExit("Builder PID is still alive; refusing another instance")
qemu = root / "qemu/usr"
env = dict(os.environ, LD_LIBRARY_PATH=str(qemu / "lib/x86_64-linux-gnu"))
lock = json.loads((repo / "eng/toolchain-lock.json").read_text())
base = cache / "fedora-44.qcow2"
with base.open("rb") as source:
    actual = hashlib.file_digest(source, "sha256").hexdigest()
if actual != lock["builderCloudImage"]["sha256"]:
    raise SystemExit("Builder base checksum mismatch")

def run(*args):
    subprocess.run([str(a) for a in args], env=env, check=True)

key = vm / "builder_ed25519"
if not key.exists():
    run("ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", key)
seed = vm / "seed"
seed.mkdir(exist_ok=True, mode=0o700)
(seed / "meta-data").write_text("instance-id: xur-fedora44-builder\nlocal-hostname: xur-builder\n")
public_key = key.with_suffix(".pub").read_text().strip()
(seed / "user-data").write_text(f"""#cloud-config
users:
  - name: builder
    groups: [wheel]
    sudo: ALL=(ALL) NOPASSWD:ALL
    shell: /bin/bash
    ssh_authorized_keys:
      - {public_key}
ssh_pwauth: false
disable_root: true
packages:
  - podman
  - git
  - golang
  - libvirt-devel
  - make
  - osbuild
  - osbuild-depsolve-dnf
  - skopeo
  - jq
  - xorriso
  - squashfs-tools
  - policycoreutils
  - selinux-policy-targeted
final_message: XUR_BUILD_VM_CLOUD_INIT_FINISHED
""")
run(qemu / "bin/genisoimage", "-quiet", "-output", vm / "seed.iso",
    "-volid", "cidata", "-joliet", "-rock", seed)
disk = vm / "builder.qcow2"
if not disk.exists():
    run(qemu / "bin/qemu-img", "create", "-f", "qcow2", "-F", "qcow2",
        "-b", base, disk, "120G")
variables = vm / "OVMF_VARS.fd"
if not variables.exists():
    shutil.copyfile(qemu / "share/OVMF/OVMF_VARS_4M.fd", variables)
run(qemu / "bin/qemu-system-x86_64",
    "-name", "xur-disposable-builder", "-machine", "q35,accel=kvm",
    "-cpu", "host", "-smp", "8", "-m", "16384", "-nodefaults",
    "-L", qemu / "share/qemu", "-display", "none",
    "-drive", f"if=pflash,format=raw,readonly=on,file={qemu}/share/OVMF/OVMF_CODE_4M.fd",
    "-drive", f"if=pflash,format=raw,file={variables}",
    "-drive", f"if=virtio,format=qcow2,file={disk}",
    "-drive", f"if=virtio,format=raw,readonly=on,file={vm}/seed.iso",
    "-netdev", "user,id=net0,hostfwd=tcp:127.0.0.1:22220-:22",
    "-device", "virtio-net-pci,netdev=net0",
    "-serial", f"file:{vm}/console.log",
    "-qmp", f"unix:{vm}/qmp.sock,server=on,wait=off",
    "-pidfile", pidfile, "-daemonize")
print(f"Builder started. Key: {key}; SSH: builder@127.0.0.1 port 22220")
print(f"Local builder console: {vm / 'console.log'}")
