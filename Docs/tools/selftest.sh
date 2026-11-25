#!/usr/bin/env bash
# Measure Tracking send rate / bandwidth without a headset.
#
# Architecture:
#   tracking_probe.py  = PC listener (binds --port)
#   fake_headset.py    = pretends to be the Pico (connects out, sends Tracking)
#
# Usage:
#   Docs/tools/selftest.sh --help
#   Docs/tools/selftest.sh                         # designs: bundle vs changed vs split
#   Docs/tools/selftest.sh --suite sources         # solo + combo + all source matrix
#   Docs/tools/selftest.sh --suite solo            # Head / Controller / Hand / Body / Motion alone
#   Docs/tools/selftest.sh --suite combo           # common multi-source combinations
#   Docs/tools/selftest.sh --suite all-sources     # Head+Controller+Hand+Body and +Motion
#   Docs/tools/selftest.sh --suite everything      # designs + sources matrix
#   Docs/tools/selftest.sh --rates Head=300        # one-off custom rates
#   Docs/tools/selftest.sh --suite solo --detail   # also print per-source probe block
#
# Positional (compat): Docs/tools/selftest.sh [port] [seconds] [detail]
set -u

HERE="$(cd "$(dirname "$0")" && pwd)"
PORT=63902
SECS=6
DETAIL=""
SUITE="designs"
DESIGN="bundle"
CUSTOM_RATES=""
OUT=""

usage() {
  cat <<'EOF'
SIMULATION only — drives fake_headset.py. Panel CollectionRate is IGNORED.
Rates are hardcoded per case (default Head=300,Controller=90,Hand=90).

  Docs/tools/selftest.sh [options]
  Docs/tools/selftest.sh [port] [seconds] [detail]   # legacy positional form

For a REAL headset (panel 400Hz etc.), do NOT use this script:
  Docs/tools/selftest.sh --live [--port 63901] [--window 1]
  # same as: python3 Docs/tools/tracking_probe.py --port 63901
  # then toggle Head/Controller/Hand/Tracker + Send on the device.

Options:
  --live              real-device probe (ros2 topic hz style); ignores --suite
  --port N            TCP port (sim default 63902; live default 63901)
  --seconds N         send duration per sim case (default 6)
  --window N          live hz report interval seconds (default 1)
  --suite NAME        what to run (default: designs)
  --design NAME       packing design for source suites (default: bundle)
  --rates SPEC        run a single custom sim case, e.g. Head=400,Hand=90
                      aliases: Tracker=Motion
  --force-63901       allow sim on port 63901 (usually a mistake)
  --detail            print each probe's per-source summary block
  -h, --help          this help

Suites (all SIMULATED):
  designs       compare packing: bundle | bundle-changed | split
  solo          Head / Controller / Hand / Body / Motion alone
  combo         common multi-source combinations
  all-sources   H+C+Hand+Body and H+C+Hand+Motion
  sources       solo + combo + all-sources
  everything    designs + sources

Examples:
  Docs/tools/selftest.sh --suite designs --seconds 6
  Docs/tools/selftest.sh --suite solo --detail
  Docs/tools/selftest.sh --rates Head=400,Controller=90,Hand=90   # sim at 400
  Docs/tools/selftest.sh --live                                    # REAL device
EOF
}

LIVE=""
WINDOW=1
FORCE_63901=""
PORT_SET=""

while [ $# -gt 0 ]; do
  case "$1" in
    -h|--help) usage; exit 0 ;;
    --live) LIVE=1; shift ;;
    --port) PORT="${2:?}"; PORT_SET=1; shift 2 ;;
    --seconds) SECS="${2:?}"; shift 2 ;;
    --window) WINDOW="${2:?}"; shift 2 ;;
    --suite) SUITE="${2:?}"; shift 2 ;;
    --design) DESIGN="${2:?}"; shift 2 ;;
    --rates) CUSTOM_RATES="${2:?}"; shift 2 ;;
    --force-63901) FORCE_63901=1; shift ;;
    --detail) DETAIL=1; shift ;;
    detail) DETAIL=1; shift ;;
    [0-9]*)
      # legacy: port [seconds] [detail]
      PORT="$1"; PORT_SET=1; shift
      if [ $# -gt 0 ] && [[ "$1" =~ ^[0-9]+$ ]]; then SECS="$1"; shift; fi
      if [ $# -gt 0 ] && [ "$1" = "detail" ]; then DETAIL=1; shift; fi
      ;;
    *)
      echo "unknown arg: $1 (try --help)" >&2
      exit 2
      ;;
  esac
done

if [ -n "$LIVE" ]; then
  LIVE_PORT=63901
  if [ -n "$PORT_SET" ]; then
    LIVE_PORT="$PORT"
  fi
  echo "=== LIVE headset probe on :${LIVE_PORT} (panel CollectionRate applies) ==="
  echo "Stop PC service first if bind fails. Toggle Send + sources on device."
  exec python3 "$HERE/tracking_probe.py" --port "$LIVE_PORT" --window "$WINDOW"
fi

case "$DESIGN" in
  bundle|bundle-changed|split) ;;
  *) echo "bad --design $DESIGN (bundle|bundle-changed|split)" >&2; exit 2 ;;
esac

if [ "$PORT" = "63901" ] && [ -z "$FORCE_63901" ]; then
  cat >&2 <<EOF
refusing sim on port 63901 — that is the real-headset port.
  Your last run was SIMULATED with hardcoded Head=300; panel 400Hz was ignored.

  Real device (ros2 topic hz style):
    $0 --live
  Or simulate at 400Hz:
    $0 --rates Head=400,Controller=90,Hand=90 --port 63902
  To force sim on 63901 anyway: add --force-63901
EOF
  exit 2
fi

if ss -lnt 2>/dev/null | grep -q ":${PORT} "; then
  echo "port ${PORT} is already in use; pass another: $0 --port 63903" >&2
  exit 1
fi

OUT="$(mktemp -d)"

echo "*** SIMULATED (fake_headset) — panel CollectionRate IGNORED ***"
echo "*** Rates below are scripted, not from the Pico UI. For real device: $0 --live ***"
echo

# case_label rates_spec design
run_case() {
  local label="$1"
  local rates="$2"
  local design="$3"
  local safe
  safe=$(printf '%s' "$label" | tr -c 'A-Za-z0-9._+=' '_')

  python3 "$HERE/tracking_probe.py" --port "$PORT" --duration "$((SECS + 3))" --quiet \
    > "$OUT/probe_$safe.txt" 2>&1 &
  local probe=$!
  sleep 1
  python3 "$HERE/fake_headset.py" --port "$PORT" --seconds "$SECS" \
    --design "$design" --rates "$rates" \
    > "$OUT/fake_$safe.txt" 2>&1
  wait "$probe" 2>/dev/null || true

  local rate bw payload redundant
  rate=$(sed -n 's/^duration .*avg \([0-9.]*\)\/s.*/\1/p' "$OUT/probe_$safe.txt")
  bw=$(sed -n 's/^duration .* \([0-9.]*\) KiB\/s$/\1/p' "$OUT/probe_$safe.txt")
  payload=$(sed -n 's/^  payload \([0-9.]*\)KiB\/s.*/\1/p' "$OUT/probe_$safe.txt")
  redundant=$(grep -o '([0-9.]*% redundant)' "$OUT/probe_$safe.txt" | head -1 | tr -d '()')
  printf '%-36s %8s %10s %10s %10s\n' \
    "$label" "${rate:-?}" "${bw:-?}" "${payload:-?}" "${redundant:-0%}"
  if [ -n "$DETAIL" ]; then
    sed -n '/=== summary ===/,/packet gap/p' "$OUT/probe_$safe.txt" | sed '1d;$d' | sed 's/^/    /'
  fi
}

print_header() {
  printf '%-36s %8s %10s %10s %10s\n' case packets/s "KiB/s" payload redundant
}

run_designs() {
  echo "=== packing designs (Head=300,Controller=90,Hand=90) ==="
  print_header
  local rates="Head=300,Controller=90,Hand=90"
  for design in bundle bundle-changed split; do
    run_case "$design" "$rates" "$design"
  done
}

run_solo() {
  echo "=== solo send (design=$DESIGN) ==="
  print_header
  run_case "Head"            "Head=300"        "$DESIGN"
  run_case "Controller"      "Controller=90"   "$DESIGN"
  run_case "Hand"            "Hand=90"         "$DESIGN"
  run_case "Body"            "Body=90"         "$DESIGN"
  run_case "Motion(Tracker)" "Motion=90"       "$DESIGN"
}

run_combo() {
  echo "=== combo send (design=$DESIGN) ==="
  print_header
  run_case "Head+Controller"           "Head=300,Controller=90"                "$DESIGN"
  run_case "Head+Hand"                 "Head=300,Hand=90"                      "$DESIGN"
  run_case "Controller+Hand"           "Controller=90,Hand=90"                 "$DESIGN"
  run_case "Head+Controller+Hand"      "Head=300,Controller=90,Hand=90"        "$DESIGN"
  run_case "Head+Motion"               "Head=300,Motion=90"                    "$DESIGN"
  run_case "Head+Controller+Motion"    "Head=300,Controller=90,Motion=90"      "$DESIGN"
}

run_all_sources() {
  echo "=== all-sources send (design=$DESIGN; Body | Motion exclusive) ==="
  print_header
  run_case "H+C+Hand+Body"   "Head=300,Controller=90,Hand=90,Body=90"   "$DESIGN"
  run_case "H+C+Hand+Motion" "Head=300,Controller=90,Hand=90,Motion=90" "$DESIGN"
}

run_sources() {
  run_solo
  echo
  run_combo
  echo
  run_all_sources
}

echo "port=$PORT seconds=$SECS design=$DESIGN logs=$OUT"
echo

if [ -n "$CUSTOM_RATES" ]; then
  echo "=== custom rates (design=$DESIGN) ==="
  print_header
  run_case "$CUSTOM_RATES" "$CUSTOM_RATES" "$DESIGN"
else
  case "$SUITE" in
    designs)      run_designs ;;
    solo)         run_solo ;;
    combo)        run_combo ;;
    all-sources)  run_all_sources ;;
    sources)      run_sources ;;
    everything)
      run_designs
      echo
      run_sources
      ;;
    *)
      echo "bad --suite $SUITE (designs|solo|combo|all-sources|sources|everything)" >&2
      exit 2
      ;;
  esac
fi

echo
echo "raw logs: $OUT"
echo "note: SIMULATED. 'bundle' = firmware today; packet rate follows the fastest"
echo "      enabled source (Head pulls Controller/Hand/Tracker along)."
echo "      Real device: $0 --live"
