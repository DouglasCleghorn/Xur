#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
case "${1:-start}" in
start)
  python3 - <<'PY'
from pathlib import Path
repo=Path.cwd();target=Path.home()/'.config/systemd/user/xur-update-repository.service';target.parent.mkdir(parents=True,exist_ok=True)
def quote(path):return '"'+str(path).replace('\\','\\\\').replace('"','\\"').replace('%','%%')+'"'
target.write_text('[Unit]\nDescription=Xur LAN application update repository\nAfter=network.target\n\n[Service]\nExecStart=/usr/bin/python3 '+quote(repo/'eng/update-repository.py')+' serve --port 8088\nWorkingDirectory='+str(repo).replace('%','%%')+'\nRestart=on-failure\nUMask=0022\nNoNewPrivileges=true\n\n[Install]\nWantedBy=default.target\n')
PY
  systemctl --user daemon-reload
  systemctl --user enable --now xur-update-repository.service
  ;;
stop) systemctl --user stop xur-update-repository.service ;;
status) systemctl --user status xur-update-repository.service ;;
*) printf 'Usage: %s [start|stop|status]\n' "$0" >&2; exit 2 ;;
esac
