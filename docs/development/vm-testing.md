# Automated installer VM qualification

`eng/test-vm.py` tests an unchanged release or candidate ISO using disposable
UEFI/OVMF VMs accelerated by KVM. It boots the ISO as read-only USB media, including
its own GRUB menu, kernel, initrd and live filesystem. It does not rebuild the
installer, replace its application, or enable a guest root shell.

## Coverage

The default run creates six fresh VMs in sequence:

| Scenario | Required checks |
| --- | --- |
| `offline` | Outbound networking blocked; local console and authenticated diagnostics start; server naming and disk selection work; no installation starts. |
| `no-disks` | No eligible installation target; configuration/data media cannot be selected; no installation starts. |
| `read-only` | Diagnostic status and console reads work; `allowControl: false` rejects navigation, text and explicit erase consent; the screen does not change. |
| `conflicting-answers` | Both `xur.yml` and `xur.yaml` present; discovery reports ambiguity; disk selection stays blocked. |
| `cancel` | Canceling review and declining erase confirmation return to disk selection; retired screen revisions cannot approve installation. |
| `install` | Normal console name/network/disk/review flow; exact target identity and Btrfs plan; missing erase consent and stale screen rejected; online Anaconda/bootc install; installed boot with USB still attached; HTTPS administrator creation; profile save/load; a second product reboot; persistent login/profile/bundle; installer diagnostics gone. |

Every scenario denies anonymous, incorrect-credential and browser-origin
diagnostic requests. Every scenario hashes the non-target disk and configuration
media before and after execution. Non-installation scenarios also prove that
the unapproved target retains its original size and contains only sparse holes,
so even unexpected zero-filled writes fail preservation. This requires a host
filesystem with `SEEK_DATA` support. The runner verifies the input ISO hash
before and after the whole run, including failures and interruption.
Installation failures are terminal; approval and reboot requests are never
automatically retried.

An auxiliary read-only configuration ISO supplies a random bootstrap code and
per-run diagnostic credential using the existing `xur.yml` and
`xur-diagnostics.yml` formats. The runner drives the supported console diagnostic
API with current screen revisions and explicit `confirmErase: true`. Those
files never authorize installation by themselves. Only newly created file-backed
disks under `.build/vm-tests/` are writable; no physical disk is attached.

## Run locally

Install QEMU for x86-64, OVMF and genisoimage. The account needs KVM read/write
access. The runner uses `/usr` or the existing
`~/.local/share/xur-build/qemu/usr` tool tree; `--qemu-root` selects another tree.
The default VM has four vCPUs, 8 GiB RAM and an 80 GiB sparse target disk. Q35 uses
KVM's clock with UTC RTC and HPET disabled. With local QEMU 10.2.1 and the
`26.10.007` release kernel, enabling HPET caused `hwclock --show` to time out;
disabling it allowed the unchanged ISO to pass clock synchronization. This is a
recorded virtual hardware choice; the installer still writes and verifies the
RTC normally. Reserve
at least 40 GiB free host storage for media and installation. Bazzite installation
requires working internet access; PXE or an ISO alone does not make it offline.

Download an immutable installer release and its matching `xur-update.json`, then:

```bash
python3 eng/test-vm.py .build/vm-input/xur-nightly-26.10.007-x86_64.iso \
  --descriptor .build/vm-input/xur-update.json
```

The descriptor is verified against Xur's checked-in public signing key and must
authenticate the ISO's byte length and SHA-256. For an unsigned inspected build,
use `--sha256` with the digest from its trusted `installer-build.json` instead.

Use `--scenario smoke` for the five boot, permission and approval scenarios
without installing, or select any individual case from the table above with
`--scenario <name>`. Only `--scenario all` can produce a full qualification.
Each run has a unique ID; `--run-id` must name a fresh run.
Default boot and install deadlines are five and thirty minutes. All VMs are
stopped on success, failure or interruption; failed disks remain for diagnosis.

Some hosts revoke transient KVM access when a VM exits. On such a host,
`--refresh-kvm-access` explicitly refreshes only the invoking user's `/dev/kvm`
ACL with passwordless `sudo setfacl` before each VM. Without that option the
runner does not change device permissions. CI opts in on its disposable runner.

## CI and evidence

**Installer VM qualification** supports manual dispatch with a versioned
`*-installer` release tag. It downloads that ISO and signed descriptor without
building or publishing a release. Select `all` (default), `smoke`, or `install`;
partial runs explicitly report missing scenarios and cannot claim full
qualification.

When **Build and publish release** is explicitly dispatched with `build_iso`,
the reusable installer workflow runs qualification after uploading the inspected
candidate. It checks the candidate commit, byte length and SHA-256 first.
Failure blocks installer publication. Application publication remains independent
of the optional installer build. PR source checks exercise the VM harness's
isolation, identity, timeout, console approval and cleanup regression tests;
they do not provision an installer VM.

Credential-free results live at
`.build/evidence/vm-tests/<run-id>/receipt.json` and are retained as CI artifacts
for fourteen days. The receipt identifies the ISO, test harness commit, VM
configuration (including timer settings, QEMU version/binary hash and OVMF
template hashes), scenario outcomes, final ISO hash and
checks that were not run. The signed
installer build receipt still describes embedded inspection; the VM result is
separate evidence, not an assertion that physical hardware passed.

Credentials, console output, diagnostic logs, firmware variables and VM disks
stay in `.build/vm-tests/<run-id>/`. CI deliberately does not upload them. No
screenshots are captured by this suite.

This initial suite covers installation and installed-management persistence.
It does not yet qualify upgrades from a previous release, update interruption
and rollback, real inference, GPU acceleration, physical USB/audio isolation,
Secure Boot, or network boot. Those checks are explicitly listed as absent or
outside its coverage; a successful VM run does not establish hardware support.

The next hardware-testing stage is described in the
[PXE test server design](pxe-test-server.md), which keeps the release ISO intact.
