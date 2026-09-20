#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p .build
exec flock --exclusive --close .build/package-update.lock python3 eng/package-update.py "$@"
