#!/usr/bin/env bash
# Analyze Vision / for4U Surface / JPEG sessions against tracking.
#
# Usage:
#   ./Docs/analyze_encode_timestamps.sh <download_dir> <sessionId>
#   ./Docs/analyze_encode_timestamps.sh auto|vision|surface|jpeg <download_dir> <sessionId> [b0|b1]
#
# Examples:
#   ./Docs/analyze_encode_timestamps.sh ./pull/Download 20261014_111332
#   ./Docs/analyze_encode_timestamps.sh surface ./pull/Download 20261014_111332
#   ./Docs/analyze_encode_timestamps.sh vision  ./pull/Download 20261014_110422
#   ./Docs/analyze_encode_timestamps.sh jpeg    ./pull/Download 20261013_180208
#   ./Docs/analyze_encode_timestamps.sh surface ./pull/Download 20261013_xxxxxx b0
set -euo pipefail

usage() {
  echo "Usage: $0 <download_dir> <sessionId>" >&2
  echo "       $0 auto|vision|surface|jpeg <download_dir> <sessionId> [b0|b1]" >&2
  echo "  auto    — pick mode from recording_meta encodePath / files (default)" >&2
  echo "  vision  — CameraRecord_<id>.mp4" >&2
  echo "  surface — for4u_CameraRecord_/for4u_frame_map_ (production) or surf_b0_/surf_b1_ (probe)" >&2
  echo "  jpeg    — frames_<id>.jsonl (api-frames)" >&2
}

# Accept either:
#   script <dir> <sid>
#   script <mode> <dir> <sid> [surf]
if [[ $# -ge 2 && -d "${1:-}" ]]; then
  MODE="auto"
  DIR="$1"
  SID="$2"
  SURF="${3:-}"
elif [[ $# -ge 3 ]]; then
  MODE="$1"
  DIR="$2"
  SID="$3"
  SURF="${4:-}"
else
  usage
  exit 2
fi

SID="${SID#<}"
SID="${SID%>}"
DIR="${DIR%/}"

META="$DIR/recording_meta_${SID}.json"
TRACK="$DIR/trackingData_${SID}.txt"

if [[ ! -f "$META" ]]; then
  echo "missing meta: $META" >&2
  exit 1
fi

ENCODE_PATH="$(python3 -c "import json; m=json.load(open('$META')); print(m.get('encodePath') or m.get('saveType') or '')")"

resolve_surface_files() {
  # Prefer production for4u_ names, then probe prefixes, then meta paths.
  local prefer="${1:-}"
  MP4=""
  MAP=""
  local candidates=()
  if [[ -n "$prefer" ]]; then
    candidates+=("surf_${prefer}_")
  fi
  candidates+=("for4u_" "surf_b1_" "surf_b0_")
  local p
  for p in "${candidates[@]}"; do
    if [[ -f "$DIR/${p}frame_map_${SID}.jsonl" || -f "$DIR/${p}CameraRecord_${SID}.mp4" ]]; then
      MAP="$DIR/${p}frame_map_${SID}.jsonl"
      MP4="$DIR/${p}CameraRecord_${SID}.mp4"
      SURFACE_PREFIX="$p"
      break
    fi
  done
  if [[ -z "${SURFACE_PREFIX:-}" ]]; then
    local meta_map meta_mp4
    meta_map="$(python3 -c "import json,os; p=json.load(open('$META')).get('frameMapPath') or ''; print(os.path.basename(p) if p else '')")"
    meta_mp4="$(python3 -c "import json,os; p=json.load(open('$META')).get('videoPath') or ''; print(os.path.basename(p) if p else '')")"
    if [[ -n "$meta_map" && -f "$DIR/$meta_map" ]]; then
      MAP="$DIR/$meta_map"
    fi
    if [[ -n "$meta_mp4" && -f "$DIR/$meta_mp4" ]]; then
      MP4="$DIR/$meta_mp4"
    fi
    SURFACE_PREFIX="(meta)"
  fi
}

if [[ "$MODE" == "auto" ]]; then
  case "$ENCODE_PATH" in
    vision-startrecord|vision-mp4) MODE="vision" ;;
    for4u-surface-mediacodec|for4u-surface-mp4) MODE="surface" ;;
    for4u-jpeg-frames|api-frames|for4u-jpeg) MODE="jpeg" ;;
    *)
      if [[ -f "$DIR/for4u_frame_map_${SID}.jsonl" || -f "$DIR/for4u_CameraRecord_${SID}.mp4" \
         || -f "$DIR/surf_b1_frame_map_${SID}.jsonl" || -f "$DIR/surf_b0_frame_map_${SID}.jsonl" ]]; then
        MODE="surface"
      elif [[ -f "$DIR/CameraRecord_${SID}.mp4" ]]; then
        MODE="vision"
      elif [[ -f "$DIR/frames_${SID}.jsonl" ]]; then
        MODE="jpeg"
      else
        echo "auto: cannot detect mode for session $SID (encodePath='$ENCODE_PATH')" >&2
        usage
        exit 1
      fi
      ;;
  esac
  echo "auto → mode=$MODE (encodePath=$ENCODE_PATH)"
fi

echo "=== meta anchors ==="
python3 - <<PY
import json
meta=json.load(open("$META"))
keys=["encodePath","saveType","timestampStrategy","alignFormula","eyeMode","width","height",
      "encodeSize","previewSize","surfacePath","cropHalf","layout",
      "requestedFps","measuredFps","bitrateBps","requestedBitrateBps",
      "raw","eis","mctf","mfnr","outputCameraRawData",
      "videoStartElapsedRealtimeNs","videoStartPredictTimeUs","videoStartUtc",
      "outputSampleCount","firstPtsUs","lastPtsUs","durationSec","muxerStopOk","eosSeen","status",
      "trackingSamples","trackingFirstElapsedRealtimeNs","trackingLastElapsedRealtimeNs",
      "savedFrames","throttledDrops","diskBudget"]
for k in keys:
    if k in meta:
        print(f"{k}={meta[k]}")
# Framing gate for for4U Surface
eye=(meta.get("eyeMode") or "").upper()
w=int(meta.get("width") or 0)
h=int(meta.get("height") or 0)
if meta.get("encodePath")=="for4u-surface-mediacodec" or meta.get("saveType")=="for4u-surface-mp4":
    expect_w = 4096 if eye=="3D" else 2048
    expect_h = 1536
    ok = (w==expect_w and h==expect_h)
    print(f"framing_gate eye={eye} encode={w}x{h} expect={expect_w}x{expect_h} pass={ok}")
    path=meta.get("surfacePath")
    if eye in ("LEFT","RIGHT") and path and path!="eye-crop":
        print(f"WARN: mono expected surfacePath=eye-crop got {path}")
    if eye=="3D" and path and path!="direct":
        print(f"WARN: 3D expected surfacePath=direct got {path}")
PY

if [[ "$MODE" == "vision" ]]; then
  if [[ "$ENCODE_PATH" != "vision-startrecord" && "$ENCODE_PATH" != "vision-mp4" ]]; then
    echo "WARN: meta encodePath='$ENCODE_PATH' does not look like Vision." >&2
  fi
  MP4="$DIR/CameraRecord_${SID}.mp4"
  if [[ ! -f "$MP4" ]]; then
    echo "missing mp4: $MP4" >&2
    echo "JPEG →: $0 jpeg $DIR $SID   Surface →: $0 surface $DIR $SID" >&2
    exit 1
  fi
  if ! command -v ffprobe >/dev/null 2>&1; then
    echo "ffprobe not found; install ffmpeg" >&2
    exit 1
  fi
  echo "=== ffprobe ==="
  ffprobe -v error -select_streams v:0 -show_entries stream=nb_frames,duration,avg_frame_rate,codec_name \
    -of default=nw=1 "$MP4" || true
  PTS_FILE="$(mktemp)"
  ffprobe -v error -select_streams v:0 -show_entries frame=pts_time \
    -of csv=p=0 "$MP4" > "$PTS_FILE" || true
  if [[ ! -s "$PTS_FILE" ]]; then
    ffprobe -v error -select_streams v:0 -show_entries frame=best_effort_timestamp_time \
      -of csv=p=0 "$MP4" > "$PTS_FILE" || true
  fi
  python3 - <<PY
import json, os
pts=[]
with open("$PTS_FILE") as f:
    for line in f:
        line=line.strip().split(",")[0]
        if not line: continue
        try: pts.append(float(line))
        except: pass
meta=json.load(open("$META"))
anchor_ns=int(meta.get("videoStartElapsedRealtimeNs") or 0)
print(f"frames={len(pts)}")
if pts:
    print(f"first_pts_s={pts[0]}")
    print(f"last_pts_s={pts[-1]}")
    print(f"span_s={pts[-1]-pts[0]}")
    print(f"implied_last_elapsedRealtimeNs={anchor_ns + int(pts[-1]*1e9)}")
track_path="$TRACK"
if os.path.isfile(track_path):
    eras=[]
    with open(track_path) as f:
        for line in f:
            line=line.strip()
            if not line: continue
            try: o=json.loads(line)
            except: continue
            if o.get("type") == "header":
                continue
            if "elapsedRealtimeNs" in o:
                eras.append(int(o["elapsedRealtimeNs"]))
    if eras and pts and anchor_ns:
        implied = anchor_ns + int(pts[-1]*1e9)
        drift_ms=(eras[-1]-implied)/1e6
        print(f"tracking_samples={len(eras)}")
        print(f"tracking_last_elapsedRealtimeNs={eras[-1]}")
        print(f"drift_tracking_minus_implied_video_end_ms={drift_ms:.3f}")
        print("NOTE: Vision has no per-frame sdk_timestamp_ns; this is indirect sync only.")
    elif not eras:
        print("tracking file present but no elapsedRealtimeNs samples parsed")
else:
    print("tracking file absent; skip drift vs tracking")
PY
  rm -f "$PTS_FILE"

elif [[ "$MODE" == "surface" ]]; then
  if [[ "$ENCODE_PATH" != "for4u-surface-mediacodec" && "$ENCODE_PATH" != "for4u-surface-mp4" ]]; then
    echo "WARN: meta encodePath='$ENCODE_PATH' is not for4u-surface-mediacodec (continuing with files)." >&2
  fi
  SURFACE_PREFIX=""
  resolve_surface_files "$SURF"
  if [[ ! -f "$MAP" ]]; then
    echo "missing frame_map for session $SID" >&2
    echo "  looked for: for4u_frame_map_${SID}.jsonl, surf_b1_frame_map_*, surf_b0_frame_map_*" >&2
    exit 1
  fi
  echo "=== surface files ==="
  echo "prefix=$SURFACE_PREFIX"
  echo "frame_map=$MAP"
  echo "mp4=${MP4:-}"
  echo "=== frame_map + align (t_video_ns = video_pts_us * 1000) ==="
  python3 - <<PY
import json, os, subprocess

def pct(xs, p):
    if not xs: return None
    s = sorted(xs)
    i = min(len(s) - 1, max(0, int(round((p / 100.0) * (len(s) - 1)))))
    return s[i]

rows=[]
with open("$MAP") as f:
    for line in f:
        line=line.strip()
        if not line: continue
        rows.append(json.loads(line))
print(f"samples={len(rows)}")
if not rows:
    raise SystemExit(0)

pts=[int(r["video_pts_us"]) for r in rows]
hosts=[r.get("host_elapsedRealtimeNs_at_output") for r in rows]
sdk_n=sum(1 for r in rows if r.get("sdk_timestamp_ns") not in (None, "null"))
span_us=pts[-1]-pts[0]
span_s=span_us/1e6
print(f"first_pts_us={pts[0]} last_pts_us={pts[-1]} span_s={span_s:.6f}")
gaps=[pts[i]-pts[i-1] for i in range(1,len(pts))]
if gaps:
    over40=sum(1 for g in gaps if g>40000)
    print(f"pts_gap_us min={min(gaps)} p50={pct(gaps,50)} max={max(gaps)} >40ms={over40}")
    meas=(len(pts)-1)/span_s if span_s>0 else 0
    print(f"pts_measured_fps={meas:.6f}")
print(f"sdk_timestamp_ns_nonnull={sdk_n}")

# Dequeue lag: host_at_output - video_pts_us*1000 (NOT capture time)
if hosts and hosts[0] is not None:
    lag=[(int(hosts[i]) - pts[i]*1000)/1e6 for i in range(len(pts)) if hosts[i] is not None]
    if lag:
        print(f"dequeue_minus_pts_ms first={lag[0]:.3f} p50={pct(lag,50):.3f} last={lag[-1]:.3f}")
        print("NOTE: host_at_output is dequeue time, NOT capture time.")

meta=json.load(open("$META"))
print(f"alignFormula={meta.get('alignFormula') or 't_video_ns = video_pts_us * 1000'}")

track="$TRACK"
if os.path.isfile(track):
    eras=[]
    with open(track) as f:
        for line in f:
            line=line.strip()
            if not line: continue
            try: o=json.loads(line)
            except: continue
            if o.get("type") == "header":
                continue
            if "elapsedRealtimeNs" in o:
                eras.append(int(o["elapsedRealtimeNs"]))
    if eras and rows:
        implied=[p*1000 for p in pts]
        print(f"tracking_samples={len(eras)}")
        print(f"track_span_s={(eras[-1]-eras[0])/1e9:.6f}")
        print(f"track_first_minus_first_pts_ms={(eras[0]-implied[0])/1e6:.3f}")
        print(f"track_last_minus_last_pts_ms={(eras[-1]-implied[-1])/1e6:.3f}")
        v0,v1=implied[0],implied[-1]
        t0,t1=eras[0],eras[-1]
        overlap=max(0, min(v1,t1)-max(v0,t0))
        print(f"overlap_s={overlap/1e9:.6f} video_span_s={(v1-v0)/1e9:.6f}")
        # nearest-neighbor: each tracking sample vs closest PTS
        deltas=[]; j=0
        for e in eras:
            while j+1 < len(implied) and abs(implied[j+1]-e) <= abs(implied[j]-e):
                j+=1
            deltas.append((e-implied[j])/1e6)
        print(f"nearest_track_minus_pts_ms min={min(deltas):.3f} p50={pct(deltas,50):.3f} max={max(deltas):.3f}")
        # end drift using correct formula (pts*1000), not videoStart+pts
        print(f"drift_tracking_minus_last_pts_ms={(eras[-1]-implied[-1])/1e6:.3f}")
        print("G2: align with video_pts_us*1000 vs elapsedRealtimeNs; sdk_timestamp_ns absent on Surface.")
    elif not eras:
        print("tracking file present but no elapsedRealtimeNs samples")
else:
    print("tracking file absent; skip align vs tracking")

mp4="$MP4"
if mp4 and os.path.isfile(mp4):
    print(f"mp4_present=1 path={mp4} bytes={os.path.getsize(mp4)}")
    try:
        probe=json.loads(subprocess.check_output([
            "ffprobe","-v","error","-select_streams","v:0",
            "-show_entries","stream=nb_frames,duration,width,height,avg_frame_rate,codec_name",
            "-of","json", mp4], text=True))
        s=probe["streams"][0]
        print(f"ffprobe codec={s.get('codec_name')} {s.get('width')}x{s.get('height')} "
              f"fps={s.get('avg_frame_rate')} duration={s.get('duration')} nb_frames={s.get('nb_frames')}")
    except Exception as e:
        print(f"ffprobe_skip={e}")
else:
    print("mp4_present=0")
PY

elif [[ "$MODE" == "jpeg" ]]; then
  JSONL="$DIR/frames_${SID}.jsonl"
  if [[ ! -f "$JSONL" ]]; then
    echo "missing frames jsonl: $JSONL" >&2
    exit 1
  fi
  echo "=== frames.jsonl ==="
  python3 - <<PY
import json, os
rows=[]
with open("$JSONL", encoding="utf-8-sig") as f:
    for line in f:
        line=line.strip()
        if not line: continue
        rows.append(json.loads(line))
print(f"saved_lines={len(rows)}")
if not rows:
    raise SystemExit(0)
def sdk(r):
    for k in ("sdk_timestamp_ns","timestamp","frame_timestamp_ns"):
        if k in r and r[k] is not None:
            return int(r[k])
    return None
def host(r):
    for k in ("elapsedRealtimeNs","mono_ns"):
        if k in r and r[k] is not None:
            return int(r[k])
    return None
sdks=[sdk(r) for r in rows]
sdks=[s for s in sdks if s is not None]
hosts=[host(r) for r in rows]
hosts=[h for h in hosts if h is not None]
print(f"sdk_timestamps={len(sdks)} host_elapsed={len(hosts)}")
if len(sdks)>=2:
    span=(sdks[-1]-sdks[0])/1e9
    print(f"sdk_span_s={span:.6f}")
    gaps=[(sdks[i]-sdks[i-1])/1e6 for i in range(1,len(sdks))]
    print(f"sdk_gap_ms min={min(gaps):.3f} max={max(gaps):.3f} median={sorted(gaps)[len(gaps)//2]:.3f}")
    hz=(len(sdks)-1)/span if span>0 else 0
    print(f"approx_save_hz={hz:.3f}")
track="$TRACK"
if os.path.isfile(track) and sdks:
    eras=[]
    with open(track) as f:
        for line in f:
            line=line.strip()
            if not line: continue
            try: o=json.loads(line)
            except: continue
            if o.get("type") == "header":
                continue
            if "elapsedRealtimeNs" in o:
                eras.append(int(o["elapsedRealtimeNs"]))
    if eras:
        drift_ms=(eras[-1]-sdks[-1])/1e6
        print(f"tracking_samples={len(eras)}")
        print(f"drift_tracking_minus_last_sdk_ms={drift_ms:.3f}")
        print("NOTE: jpeg path has per-frame sdk_timestamp_ns (BOOTTIME).")
else:
    print("tracking absent or no sdk timestamps")
PY

else
  echo "unknown mode: $MODE (use auto, vision, surface, or jpeg)" >&2
  usage
  exit 2
fi

echo "=== done ==="
