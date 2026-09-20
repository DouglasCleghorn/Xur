"""Wait for the disposable Fedora builder without hiding cloud-init failures."""
import json
import subprocess
import sys


def wait_for_cloud_init(ssh):
    # Both commands need root: Fedora protects /run/cloud-init/cloud.cfg.
    waited = subprocess.run(ssh + ['sudo -n cloud-init status --wait'], check=False)
    status = subprocess.run(ssh + ['sudo -n cloud-init status --format json'],
                            check=False, capture_output=True, text=True)
    if status.stdout:
        print(status.stdout, flush=True)
    if status.stderr:
        print(status.stderr, file=sys.stderr, flush=True)
    # 2 means completed with recoverable errors; 1 is a fatal failure.
    # Never treat an SSH/sudo failure as successful initialization.
    if waited.returncode not in (0, 2) or status.returncode not in (0, 2):
        raise RuntimeError(f'Builder cloud-init failed (wait exit {waited.returncode}, '
                           f'status exit {status.returncode}); see status output above.')
    try:
        report = json.loads(status.stdout)
    except json.JSONDecodeError as error:
        raise RuntimeError('Builder cloud-init did not return valid JSON status.') from error
    if not isinstance(report, dict) or report.get('status') != 'done' or report.get('errors'):
        raise RuntimeError('Builder cloud-init did not finish successfully; see status output above.')
    if 2 in (waited.returncode, status.returncode) or report.get('recoverable_errors'):
        print('Builder cloud-init completed with recoverable warnings (reported above). '
              'Continuing with Fedora toolchain preparation and verification.', flush=True)
