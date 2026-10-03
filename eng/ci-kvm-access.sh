#!/usr/bin/env bash
# Refresh transient device access only on disposable GitHub-hosted runners.
set -euo pipefail
[[ "${GITHUB_ACTIONS:-}" == true && "${RUNNER_ENVIRONMENT:-}" == github-hosted ]]
[[ -c /dev/kvm ]]
sudo setfacl -m "u:$(id -un):rw" /dev/kvm
python3 - <<'PY'
import os
if not os.access('/dev/kvm', os.R_OK | os.W_OK):
    raise SystemExit('Hosted runner KVM access is unavailable after refreshing its ACL')
print('Hosted runner KVM read/write access verified')
PY
