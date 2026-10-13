#!/usr/bin/env bash
# Build CameraRecord_<sessionId>.mp4 from a for4U api-frames session.
# Usage: for4u_frames_to_mp4.sh <download_or_session_parent_dir> <sessionId>
set -euo pipefail

if [[ $# -lt 2 ]]; then
  echo "Usage: $0 <dir> <sessionId>" >&2
  echo "  expects: <dir>/frames_<sessionId>/000000.jpg and recording_meta_<sessionId>.json" >&2
  exit 1
fi

DIR="${1%/}"
SID="$2"
FRAMES_DIR="${DIR}/frames_${SID}"
META="${DIR}/recording_meta_${SID}.json"
OUT="${DIR}/CameraRecord_${SID}.mp4"

if [[ ! -d "$FRAMES_DIR" ]]; then
  echo "Missing frames dir: $FRAMES_DIR" >&2
  exit 1
fi
if [[ ! -f "${FRAMES_DIR}/000000.jpg" ]]; then
  echo "Missing first frame: ${FRAMES_DIR}/000000.jpg" >&2
  exit 1
fi

FPS=30
if [[ -f "$META" ]] && command -v python3 >/dev/null 2>&1; then
  FPS="$(python3 - <<PY
import json
try:
    m=json.load(open("$META"))
    print(int(m.get("requested_fps") or m.get("configuredFps") or 30))
except Exception:
    print(30)
PY
)"
fi

if ! command -v ffmpeg >/dev/null 2>&1; then
  echo "ffmpeg not found in PATH" >&2
  exit 1
fi

echo "Encoding session=$SID fps=$FPS -> $OUT"
# Prefer libx264; fall back to mpeg4 if the build has no x264.
if ffmpeg -hide_banner -encoders 2>/dev/null | grep -q 'libx264'; then
  VCODEC=( -c:v libx264 -pix_fmt yuv420p -crf 18 )
else
  echo "libx264 unavailable; using mpeg4" >&2
  VCODEC=( -c:v mpeg4 -q:v 5 )
fi

ffmpeg -y -hide_banner -loglevel error \
  -framerate "$FPS" \
  -i "${FRAMES_DIR}/%06d.jpg" \
  "${VCODEC[@]}" \
  "$OUT"

echo "Wrote $OUT ($(wc -c <"$OUT") bytes)"
