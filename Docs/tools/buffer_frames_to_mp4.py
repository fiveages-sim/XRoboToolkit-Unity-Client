#!/usr/bin/env python3
"""Build MP4 from for4U CPU buffer dump frames/ (exp_buf=1).

Does NOT apply intrinsics/extrinsics to pixels — PICO SDK has no k1/k2.
This only packs the saved JPEGs (PICO-delivered buffer contents) into an MP4.

Usage:
  python3 Docs/tools/buffer_frames_to_mp4.py ./session_<id>
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("session_dir", type=Path)
    ap.add_argument("--out", type=Path, default=None)
    args = ap.parse_args()
    session = args.session_dir.resolve()
    frames = session / "frames"
    if not frames.is_dir():
        print(f"Missing {frames}", file=sys.stderr)
        return 2
    jpgs = sorted(frames.glob("*.jpg"))
    if not jpgs:
        print("No frames/*.jpg", file=sys.stderr)
        return 2

    fps = 10
    meta_path = session / "meta.json"
    if meta_path.is_file():
        meta = json.loads(meta_path.read_text(encoding="utf-8"))
        cap = meta.get("capture") if isinstance(meta.get("capture"), dict) else meta
        fps = int(cap.get("saveFps") or cap.get("requestedFps") or 10)

    out = args.out or (session / "CameraRecord_buffer.mp4")
    ffmpeg = shutil.which("ffmpeg")
    if not ffmpeg:
        print("ffmpeg not found", file=sys.stderr)
        return 2

    print(
        "NOTE: This remuxes buffer JPEGs only. Intrinsics/extrinsics are NOT used "
        "to warp pixels (no distortion model in SDK).",
        flush=True,
    )
    cmd = [
        ffmpeg,
        "-y",
        "-hide_banner",
        "-loglevel",
        "error",
        "-framerate",
        str(fps),
        "-i",
        str(frames / "%06d.jpg"),
        "-c:v",
        "libx264",
        "-pix_fmt",
        "yuv420p",
        "-crf",
        "18",
        str(out),
    ]
    print("+", " ".join(cmd), flush=True)
    subprocess.check_call(cmd)
    print(f"Wrote {out} ({len(jpgs)} frames @ {fps} fps)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
