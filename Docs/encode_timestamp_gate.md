# Encode + timestamp validation gate

Answers: does PICO provide an efficient, sustained, tracking-alignable camera encode path for ~20 min local capture?

Do **not** treat this doc as a production path choice until the G1/G2/D table at the bottom is filled with device numbers.

## Paths under test

| encodePath | How to run | Outputs |
|------------|------------|---------|
| `vision-startrecord` | RecordDialog, exp_Image=0 | `CameraRecord_*.mp4`, `recording_meta_*.json`, optional tracking |
| `for4u-jpeg-frames` | RecordDialog, exp_Image=1 | `frames_*/`, `frames_*.jsonl`, meta, tracking |
| `for4u-surface-mediacodec` production | RecordDialog `exp_Image=1` (eye/fps/flags) | `for4u_CameraRecord_*.mp4`, `for4u_frame_map_*.jsonl`, meta, tracking |
| `for4u-surface-mediacodec` B0 probe | `SurfaceEncodeProbeMode=1` or Download `surface_probe_b0` | `surf_b0_` MP4 + frame_map + meta |

Do **not** use Download `surface_probe_b*` files anymore — they hijacked Vision/`exp_Image` and caused “wrong” status text.

B0/B1 short runs on device: encoder + `StartPreviewfor4U`→Surface G1 OK at 1024². Production for4U Surface encode still needs a dedicated session path (not Prefs hijack).

## Meta contract (every session)

- `encodePath`, `timestampStrategy`, `eyeMode`, `width`/`height`, `requestedFps`, `bitrateBps`, `codec`
- `eis` / `mctf` / `mfnr` / `raw`, `capturePathId`
- `clockSemantics` (sdk / elapsedRealtime / video_pts / host_at_output)
- `dropped_frames.{source,encoder,throttle}`
- `diskBudget` (estimated / free / required / ok)
- Anchors when video starts: `videoStartElapsedRealtimeNs`, `videoStartPredictTimeUs`, `videoStartUtc`

### Timestamp hard rules

| Field | Meaning |
|-------|---------|
| `sdk_timestamp_ns` | Camera/SDK capture mark (BOOTTIME). Absent on Vision and typically on Surface preview. |
| `elapsedRealtimeNs` | Device BOOTTIME; align tracking here. |
| `video_pts_us` | MediaCodec / MP4 presentation time. |
| `host_elapsedRealtimeNs_at_output` | Dequeue time on the app. **Not capture time.** |

## Disk budget (before 10 min stress)

Rule: `free > 1.5 * estimatedBytes + 2GB`. Sessions refuse to start or stop mid-run if free &lt; 2GB.

| Path | ~10 min | ~20 min |
|------|---------|---------|
| Vision ~5 Mbps | ~375 MB | ~750 MB |
| Surface @ 8 Mbps | ~600 MB | ~1.2 GB |
| JPEG @ 10 fps | ~2.4 GB | ~4.8 GB |

JPEG probe: keep `saveFps ≤ 10` for first 2 min runs.

## Phase A — Vision baseline

1. Record 2 min then 10 min with exp_Image=0, tracking on, EIS off as already done by CameraHandle.
2. Pull `recording_meta_*.json`, `CameraRecord_*.mp4`, `trackingData_*.txt`.
3. Run:

```bash
./Docs/analyze_encode_timestamps.sh vision /path/to/Download <sessionId>
```

4. Pass (sustain): moov present, duration error &lt; 3%, no stop hang.
5. Pass (sync): only if 10 min PTS–tracking drift &lt; ~50 ms → mark `acceptable_indirect_sync`. No per-frame `sdk_timestamp_ns` on this path.

## Phase B — Surface probe (split)

### B0 — encoder only (no camera)

1. Set `SurfaceEncodeProbeMode=1`, start Record (Vision toggle on is fine; probe overrides).
2. Record ≥30 s (then 5 min). Stop.
3. Expect non-empty `surf_b0_CameraRecord_*.mp4`, `muxerStopOk`, continuous PTS in frame_map, Stop state timeline without hang.
4. Failure here = encoder bug, not PICO camera.

### B1 — camera → encoder Surface (G1)

1. Mode=2. Config locked: LEFT, 1024×1024 (plugin hardcode), AVC ~8 Mbps, EIS off.
2. Record 2 min then 5–10 min.
3. G1 pass: non-black content, output frames ≈ duration×fps (±10%), moov OK, Stop completes.
4. B0 pass + B1 fail = Surface compatibility / data path issue.

### B2 — timestamps (G2)

Only after B1:

```bash
./Docs/analyze_encode_timestamps.sh /path/to/Download <sessionId>
./Docs/analyze_encode_timestamps.sh surface /path/to/Download <sessionId>
```

Production files: `for4u_CameraRecord_*.mp4`, `for4u_frame_map_*.jsonl`. Probe: `surf_b0_` / `surf_b1_`.

- If only PTS + start anchor / host_at_output: label **indirect sync** only.
- G2 pass: reproducible map and drift &lt; ~50 ms over 10 min.
- Never promote `host_elapsedRealtimeNs_at_output` to capture time.

## Phase C — same-device matrix

| Run | Path | Duration |
|-----|------|----------|
| C1 | Vision | 2 min |
| C2 | Surface B1 (if G1 not dead) | 2 min |
| C3 | JPEG @10 fps | 2 min |
| C4–C6 | same | 10 min each if 2 min OK |

Fill the comparison table (frame count, duration, drops, disk, drift ms).

## Phase D — decision (fill after device runs)

| Gate | Result (fill) | Implication |
|------|---------------|-------------|
| Surface G1 | _pending_ | If fail: abandon Surface direct encode |
| Surface G2 | _pending_ | If fail: Surface archive-only; VLA sync stays on frames |
| Vision sustain | _pending_ | |
| Vision indirect sync | _pending_ | |
| **Production lock** | _pending_ | Exactly one of: Surface+map / Vision+indirect / calibrated JPEG |

Eye-mode UI (LEFT/RIGHT/3D sizes) ships only after production lock.

## Implementation map

- Java: `Assets/Plugins/Android/.../SurfaceVideoEncoder.java`
- C#: `SurfaceEncodeProbe.cs`, `DiskSafety.cs`, `CaptureReport` contract helpers
- Vision / ApiFrameSession write the shared meta contract + diskBudget
