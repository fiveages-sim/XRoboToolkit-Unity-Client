#!/usr/bin/env python3
"""Probe Enterprise Tracking main-packet receive rate via xrobotoolkit_sdk.

Uses fa-py-libraries' installed SDK when the current interpreter has no
xrobotoolkit_sdk (temporary bootstrap; does not modify fa-py-libraries).

Scheme B: Hand/Tracker ride the same Tracking packet as Head/Controller, so
any enabled stream advances top-level timeStampNs. unique_hz should track the
headset CollectionRate (enterpriseSampleHz) for Head-only, Controller-only,
Hand-only, Tracker-only, or any combination.

Optional --hand counts left-hand pose changes (requires pybind contains fix
and a rebuilt xrobotoolkit_sdk).

Prerequisites:
  1. Stop vr-xrt / any other xrobotoolkit_sdk client (PC Service allows one).
  2. Start XRoboToolkit PC Service.
  3. On Pico: Enterprise data source, Send on, desired stream toggles on,
     CollectionRate set, USB connected.
  4. Rebuild/reinstall xrobotoolkit_sdk after Controller/Hand contains patch.

Usage:
  python3 scripts/probe_enterprise_recv_hz.py
  python3 scripts/probe_enterprise_recv_hz.py --hand
  ./scripts/probe_enterprise_recv_hz.sh --seconds 5
  FA_PY_LIBRARIES=~/fa-py-libraries python3 scripts/probe_enterprise_recv_hz.py
"""

from __future__ import annotations

import argparse
import os
import sys
import time
from pathlib import Path


def _candidate_fa_roots() -> list[Path]:
    roots: list[Path] = []
    env = os.environ.get("FA_PY_LIBRARIES", "").strip()
    if env:
        roots.append(Path(env).expanduser().resolve())

    here = Path(__file__).resolve()
    for parent in here.parents:
        roots.append(parent / "fa-py-libraries")
        if parent.name == "libraries":
            roots.append(parent.parent / "fa-py-libraries")

    roots.append(Path.home() / "fa-py-libraries")
    roots.append(Path("/home/fiveages/fa-py-libraries"))
    roots.append(Path("/run/host/home/fiveages/fa-py-libraries"))

    out: list[Path] = []
    seen: set[Path] = set()
    for root in roots:
        try:
            resolved = root.resolve()
        except OSError:
            continue
        if resolved in seen or not resolved.is_dir():
            continue
        seen.add(resolved)
        out.append(resolved)
    return out


def _find_site_packages(fa_root: Path) -> Path | None:
    site = fa_root / ".venv" / "lib" / "python3.12" / "site-packages"
    if (site / "xrobotoolkit_sdk.cpython-312-x86_64-linux-gnu.so").is_file() or any(
        site.glob("xrobotoolkit_sdk*.so")
    ):
        return site
    for path in sorted((fa_root / ".venv" / "lib").glob("python3.*/site-packages")):
        if any(path.glob("xrobotoolkit_sdk*.so")):
            return path
    return None


def _find_sdk_lib_dir(fa_root: Path) -> Path | None:
    lib = (
        fa_root
        / "vr_pose_publisher"
        / "dependencies"
        / "XRoboToolkit-PC-Service-Pybind"
        / "lib"
    )
    if (lib / "libPXREARobotSDK.so").is_file():
        return lib
    return None


def _find_python312() -> Path | None:
    env = os.environ.get("PROBE_PYTHON", "").strip()
    candidates: list[Path] = []
    if env:
        candidates.append(Path(env).expanduser())

    for fa_root in _candidate_fa_roots():
        for name in ("python3.12", "python3", "python"):
            p = fa_root / ".venv" / "bin" / name
            if p.is_file() and not p.is_symlink():
                candidates.append(p)
            elif p.is_symlink():
                try:
                    if p.resolve().is_file():
                        candidates.append(p)
                except OSError:
                    pass

    for name in (
        "python3.12",
        "/usr/bin/python3.12",
        "/usr/local/bin/python3.12",
    ):
        candidates.append(Path(name))

    for p in candidates:
        try:
            if p.is_file():
                return p.resolve()
        except OSError:
            continue
    return None


def _bootstrap_from_fa_py_libraries() -> None:
    if os.environ.get("_PROBE_FA_BOOTSTRAPPED") == "1":
        return

    try:
        import xrobotoolkit_sdk  # noqa: F401

        return
    except ImportError:
        pass

    fa_root = None
    site = None
    for root in _candidate_fa_roots():
        candidate = _find_site_packages(root)
        if candidate is not None:
            fa_root = root
            site = candidate
            break
    if fa_root is None or site is None:
        return

    lib_dir = _find_sdk_lib_dir(fa_root)
    ld = os.environ.get("LD_LIBRARY_PATH", "")
    env = os.environ.copy()
    if lib_dir is not None:
        env["LD_LIBRARY_PATH"] = f"{lib_dir}:{ld}" if ld else str(lib_dir)
    env["FA_PY_LIBRARIES"] = str(fa_root)
    env["_PROBE_FA_BOOTSTRAPPED"] = "1"

    py = _find_python312()
    need_reexec = py is not None and Path(sys.executable).resolve() != py.resolve()
    if need_reexec:
        os.execve(str(py), [str(py), *sys.argv], env)

    if str(site) not in sys.path:
        sys.path.insert(0, str(site))
    os.environ.update(
        {
            "LD_LIBRARY_PATH": env.get("LD_LIBRARY_PATH", ld),
            "FA_PY_LIBRARIES": str(fa_root),
            "_PROBE_FA_BOOTSTRAPPED": "1",
        }
    )


def _hand_pose_fingerprint(xrt) -> tuple | None:
    getter = getattr(xrt, "get_left_hand_tracking_state", None)
    if getter is None:
        return None
    try:
        state = getter()
    except Exception:
        return None
    if state is None:
        return None
    try:
        # First joint pose is enough to detect updates.
        joint0 = state[0]
        return tuple(float(x) for x in joint0)
    except Exception:
        return None


def main() -> int:
    _bootstrap_from_fa_py_libraries()

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--seconds", type=float, default=5.0, help="Sample window length")
    parser.add_argument(
        "--poll-hz",
        type=float,
        default=1500.0,
        help="Local poll rate (must exceed headset send Hz)",
    )
    parser.add_argument(
        "--warmup",
        type=float,
        default=0.5,
        help="Seconds to wait after init before counting",
    )
    parser.add_argument(
        "--hand",
        action="store_true",
        help="Also count unique left-hand joint0 pose changes",
    )
    args = parser.parse_args()

    try:
        import xrobotoolkit_sdk as xrt
    except ImportError as exc:
        print(
            "ERROR: xrobotoolkit_sdk not importable.\n"
            "  Tried fa-py-libraries bootstrap.\n"
            "  Install: cd ~/fa-py-libraries && ./init.sh install-xrobotoolkit\n"
            f"  ({exc})",
            file=sys.stderr,
        )
        return 1

    poll_hz = max(float(args.poll_hz), 1.0)
    period = 1.0 / poll_hz
    window_s = max(float(args.seconds), 0.1)
    warmup_s = max(float(args.warmup), 0.0)

    print("init xrobotoolkit_sdk … (stop vr-xrt first)")
    try:
        xrt.init()
    except Exception as exc:
        print(f"ERROR: xrt.init() failed: {exc}", file=sys.stderr)
        print(
            "  Is PC Service running? ./run.sh vr-xrt-service  (from fa-py-libraries)",
            file=sys.stderr,
        )
        return 1

    try:
        if warmup_s > 0:
            time.sleep(warmup_s)

        get_ts = xrt.get_time_stamp_ns
        last_ts = 0
        unique = 0
        reads = 0
        zero_reads = 0
        hand_unique = 0
        hand_reads = 0
        last_hand = None
        track_hand = bool(args.hand)

        t0 = time.perf_counter()
        deadline = t0 + window_s

        while True:
            now = time.perf_counter()
            if now >= deadline:
                break
            try:
                ts = int(get_ts())
            except Exception:
                ts = 0
            reads += 1
            if ts == 0:
                zero_reads += 1
            elif ts != last_ts:
                unique += 1
                last_ts = ts

            if track_hand:
                fp = _hand_pose_fingerprint(xrt)
                hand_reads += 1
                if fp is not None and fp != last_hand:
                    hand_unique += 1
                    last_hand = fp

            sleep_for = period - (time.perf_counter() - now)
            if sleep_for > 0:
                time.sleep(sleep_for)

        elapsed = time.perf_counter() - t0
        unique_hz = unique / elapsed if elapsed > 0 else 0.0
        mean_poll_hz = reads / elapsed if elapsed > 0 else 0.0
        repeat_frac = 1.0 - (unique / reads) if reads > 0 else 0.0
        hand_hz = hand_unique / elapsed if elapsed > 0 else 0.0

        print("---")
        print(f"elapsed_s     {elapsed:.3f}")
        print(f"reads         {reads}")
        print(f"unique_frames {unique}")
        print(f"zero_reads    {zero_reads}")
        print(f"unique_hz     {unique_hz:.1f}   # compare to enterpriseSampleHz on headset")
        print(f"poll_hz       {mean_poll_hz:.1f}")
        print(f"repeat_frac   {repeat_frac:.3f}")
        if track_hand:
            print(f"hand_unique   {hand_unique}")
            print(f"hand_hz       {hand_hz:.1f}   # left joint0 pose changes")
        print("---")
        print(
            "Acceptance (scheme B, main packet):\n"
            "  Head-only / Controller-only / Head+Controller: unique_hz ≈ panel Hz\n"
            "  Hand-only / Tracker-only: unique_hz ≈ panel Hz (needs pybind contains)\n"
            "  Head+Controller+Hand: unique_hz near panel; note any drop vs H+C baseline\n"
            "  Send off or all streams off: unique_hz → 0\n"
            "Use --hand to confirm left-hand cache updates when Hand is enabled."
        )
        return 0
    finally:
        try:
            xrt.close()
        except Exception:
            pass


if __name__ == "__main__":
    raise SystemExit(main())
