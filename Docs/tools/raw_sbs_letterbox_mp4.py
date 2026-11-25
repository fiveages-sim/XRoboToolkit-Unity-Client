#!/usr/bin/env python3
"""Layout-only helper for for4U Raw Surface sessions (NOT a calib undistort).

IMPORTANT
---------
Saved fx/fy/cx/cy / extrinsics are written into note JSON only.
They do NOT change pixels — PICO RGBCameraParamsNew has no k1/k2/p1/p2.
If your input was already raw-sbs-black (4096), output ≈ copy (crop→non-crop
only happens when rebuilding SBS from a 2048 eye-cropped file).

For true “as PICO filled the buffer” pixels, record with exp_buf (Buf) and use
buffer_frames_to_mp4.py instead.

Behaviors
---------
* surfacePath raw-sbs-black / exp_crop=0 (full 4096 SBS): re-encode/copy as-is
  → uncropped_black_border.mp4 (content half + official black half preserved).
* surfacePath eye-crop (2048 mono): rebuild 4096 SBS with the other half black
  → same output name (restores black border lost by on-device crop).
* direct processed mono: optional letterbox to 4096 with black other half for A/B
  compare (--force-sbs).

Usage
-----
  python3 Docs/tools/raw_sbs_letterbox_mp4.py /path/to/session_YYYYMMDD_HHMMSS
  python3 Docs/tools/raw_sbs_letterbox_mp4.py /path/to/session --force-sbs
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path


EYE_W, EYE_H = 2048, 1536
SBS_W = 4096


def load_json(path: Path) -> dict:
    with path.open("r", encoding="utf-8") as f:
        return json.load(f)


def capture_blob(meta: dict) -> dict:
    cap = meta.get("capture")
    return cap if isinstance(cap, dict) else meta


def which_ffmpeg() -> str | None:
    return shutil.which("ffmpeg")


def run(cmd: list[str]) -> None:
    print("+", " ".join(cmd), flush=True)
    subprocess.check_call(cmd)


def rebuild_sbs_from_mono(
    src: Path,
    dst: Path,
    eye: str,
    fps: float,
    ffmpeg: str,
) -> None:
    """Place 2048×1536 eye into LEFT or RIGHT half of 4096×1536 black canvas."""
    x = "0" if eye.upper() != "RIGHT" else str(EYE_W)
    # scale2ref not needed; pad then overlay.
    filt = (
        f"color=c=black:s={SBS_W}x{EYE_H}:r={fps}[bg];"
        f"[0:v]scale={EYE_W}:{EYE_H}:flags=bicubic[fg];"
        f"[bg][fg]overlay=x={x}:y=0:shortest=1"
    )
    run(
        [
            ffmpeg,
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-i",
            str(src),
            "-filter_complex",
            filt,
            "-an",
            "-c:v",
            "libx264",
            "-pix_fmt",
            "yuv420p",
            "-crf",
            "18",
            str(dst),
        ]
    )


def remux_or_reencode(src: Path, dst: Path, ffmpeg: str, reencode: bool) -> None:
    if not reencode:
        run(
            [
                ffmpeg,
                "-y",
                "-hide_banner",
                "-loglevel",
                "error",
                "-i",
                str(src),
                "-c",
                "copy",
                "-an",
                str(dst),
            ]
        )
        return
    run(
        [
            ffmpeg,
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-i",
            str(src),
            "-an",
            "-c:v",
            "libx264",
            "-pix_fmt",
            "yuv420p",
            "-crf",
            "18",
            str(dst),
        ]
    )


def probe_size(path: Path, ffmpeg: str) -> tuple[int, int] | None:
    ffprobe = shutil.which("ffprobe")
    if not ffprobe:
        return None
    out = subprocess.check_output(
        [
            ffprobe,
            "-v",
            "error",
            "-select_streams",
            "v:0",
            "-show_entries",
            "stream=width,height",
            "-of",
            "csv=p=0:s=x",
            str(path),
        ],
        text=True,
    ).strip()
    if "x" not in out:
        return None
    w_s, h_s = out.split("x", 1)
    return int(w_s), int(h_s)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("session_dir", type=Path, help="CameraCollection/session_* directory")
    ap.add_argument(
        "--out",
        type=Path,
        default=None,
        help="Output mp4 (default: <session>/uncropped_black_border.mp4)",
    )
    ap.add_argument(
        "--force-sbs",
        action="store_true",
        help="Even for processed direct mono, rebuild 4096 SBS with black other half",
    )
    ap.add_argument(
        "--reencode",
        action="store_true",
        help="Reencode when copying full SBS (default: stream copy when possible)",
    )
    args = ap.parse_args()

    session = args.session_dir.resolve()
    if not session.is_dir():
        print(f"Not a directory: {session}", file=sys.stderr)
        return 2

    meta_path = session / "meta.json"
    calib_path = session / "camera_calib.json"
    video = session / "CameraRecord.mp4"
    if not video.is_file():
        # legacy name
        candidates = list(session.glob("*.mp4"))
        if not candidates:
            print(f"No MP4 in {session}", file=sys.stderr)
            return 2
        video = candidates[0]

    meta = load_json(meta_path) if meta_path.is_file() else {}
    cap = capture_blob(meta)
    calib = load_json(calib_path) if calib_path.is_file() else cap.get("calibration") or {}

    surface = str(cap.get("surfacePath") or "")
    exp_crop = int(cap.get("exp_crop") if "exp_crop" in cap else (0 if "raw-sbs" in surface else 1))
    eye = str(cap.get("eyeMode") or cap.get("requestedEyeMode") or "LEFT")
    raw = bool(cap.get("raw") or cap.get("outputCameraRawData"))
    fps = float(cap.get("requestedFps") or cap.get("configuredFps") or 30)

    out = args.out or (session / "uncropped_black_border.mp4")
    ffmpeg = which_ffmpeg()
    if not ffmpeg:
        print("ffmpeg not found in PATH", file=sys.stderr)
        return 2

    size = probe_size(video, ffmpeg)
    print(
        f"input={video.name} size={size} surfacePath={surface!r} "
        f"exp_crop={exp_crop} raw={raw} eye={eye} fps={fps}"
    )

    note = {
        "tool": "raw_sbs_letterbox_mp4.py",
        "input": str(video),
        "output": str(out),
        "surfacePath": surface,
        "exp_crop": exp_crop,
        "raw": raw,
        "eyeMode": eye,
        "inputSize": {"width": size[0], "height": size[1]} if size else None,
        "calibration": {
            "fx": calib.get("fx"),
            "fy": calib.get("fy"),
            "cx": calib.get("cx"),
            "cy": calib.get("cy"),
            "intrinsicsStatus": calib.get("intrinsicsStatus"),
            "extrinsicsStatus": calib.get("extrinsicsStatus"),
            "distortionAvailable": calib.get("distortionAvailable", False),
            "note": calib.get("distortion")
            or "No k1/k2 in PICO RGBCameraParamsNew; output preserves layout only.",
        },
        "action": None,
    }

    # Full SBS already (B: Raw + exp_crop=0).
    if size and size[0] >= SBS_W - 8:
        note["action"] = "preserve_full_sbs"
        remux_or_reencode(video, out, ffmpeg, reencode=args.reencode)
    elif surface == "eye-crop" or (raw and exp_crop == 1) or (
        size and size[0] <= EYE_W + 8 and (raw or args.force_sbs)
    ):
        note["action"] = "rebuild_sbs_from_mono_eye"
        rebuild_sbs_from_mono(video, out, eye, fps, ffmpeg)
    elif args.force_sbs:
        note["action"] = "force_sbs_from_processed_mono"
        rebuild_sbs_from_mono(video, out, eye, fps, ffmpeg)
    else:
        note["action"] = "copy_as_is_not_sbs"
        print(
            "Input is not full SBS and not eye-crop Raw; copying as-is. "
            "Use --force-sbs to letterbox to 4096.",
            file=sys.stderr,
        )
        remux_or_reencode(video, out, ffmpeg, reencode=True)

    note_path = session / "uncropped_black_border_note.json"
    note_path.write_text(json.dumps(note, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Wrote {out}")
    print(f"Wrote {note_path}")
    if calib.get("fx") is None and not (session / "camera_calib.json").is_file():
        print("Warning: no camera_calib.json / fx in meta — calib sidecar incomplete.", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
