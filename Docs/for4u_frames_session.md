# for4U frames session (exp_Image=1)

Headset stores lightly compressed frames at the RecordDialog FPS. PC encodes `CameraRecord_<sessionId>.mp4`.

## Layout under `/sdcard/Download`

| File / dir | Role |
|------------|------|
| `recording_meta_<sessionId>.json` | Session status, fps, flags, paths, drop counts |
| `trackingData_<sessionId>.txt` | Tracking JSONL (same clock family as frames) |
| `frames_<sessionId>/000000.jpg` … | JPEG frames (quality 80, full 2048×1536 3D SBS) |
| `frames_<sessionId>.jsonl` | One JSON object per saved frame |

## `frames_*.jsonl` fields

- `index` — zero-based order (matches `000000.jpg`)
- `file` — relative file name inside `frames_<sessionId>/`
- `sdk_timestamp_ns` — `Frame.timestamp` (BOOTTIME ns)
- `elapsedRealtimeNs` — `SystemClock.elapsedRealtimeNanos` at accept time
- `width` / `height`
- `dropped_before` — callbacks skipped since previous saved frame

## meta highlights

- `saveType`: `api-frames`
- `requested_fps`: dialog FPS (shared with exp_Image=0)
- `framesDir` / `framesJsonlPath`
- `throttledDrops` / `savedFrames`

## PC encode

```bash
./Docs/for4u_frames_to_mp4.sh /sdcard/Download <sessionId>
# or after adb pull:
./Docs/for4u_frames_to_mp4.sh ./pulled_download <sessionId>
```

Output: `<dir>/CameraRecord_<sessionId>.mp4`

## Stop / race rules (headset)

1. Stop latches: no new frames accepted.
2. JPEG writer thread is joined (up to 20s) before camera close.
3. jsonl + tracking flushed, then `CloseCamerafor4U`.
4. Validation checks frames dir + jsonl + first JPEG — not MP4 moov.
