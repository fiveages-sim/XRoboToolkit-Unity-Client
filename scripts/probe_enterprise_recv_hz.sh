#!/usr/bin/env bash
# Thin launcher: prefers fa-py-libraries SDK without modifying that repo.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export FA_PY_LIBRARIES="${FA_PY_LIBRARIES:-$HOME/fa-py-libraries}"
if [[ ! -d "$FA_PY_LIBRARIES" && -d /home/fiveages/fa-py-libraries ]]; then
  export FA_PY_LIBRARIES=/home/fiveages/fa-py-libraries
fi
exec python3 "$ROOT/scripts/probe_enterprise_recv_hz.py" "$@"
