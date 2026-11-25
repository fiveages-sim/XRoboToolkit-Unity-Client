#!/usr/bin/env python3
"""Receive Unity Enterprise Tracking TCP and diagnose reception / true rates.

Listens like the PC service (headset is TCP client). Reports Hand L/R isActive,
B0 present/unique, and — most importantly — adjacent-sample classification so you
can read true useful Hz from the wire:

  nearDup   palm |Δ| < near threshold  → same logical frame re-sent / re-read
  microΔ    between thresholds         → float noise or very slow motion
  trueΔ     palm |Δ| ≥ true threshold  → clear recognition / motion change
  quantUnique  pose after truncating ~0.1mm  → useful content rate (resource hint)

REAL DEVICE:
  1. Stop PC service on 63901 (or use another port).
  2. python3 Docs/tools/tracking_recv_diag.py --port 63901
  3. Pico: Enterprise source, Send on, Hand (+ Head) on.

SIMULATION:
  python3 Docs/tools/tracking_recv_diag.py --port 63902 --duration 8 --quiet &
  python3 Docs/tools/fake_headset.py --port 63902 --seconds 6 --rates Head=300,Hand=120

  python3 Docs/tools/tracking_recv_diag.py --explain
"""

from __future__ import annotations

import argparse
import collections
import hashlib
import json
import math
import socket
import struct
import sys
import time
from typing import Any

HEAD = 0x3F
END = 0xA5
CMD_TRACKING = 0x6D

SOURCES = ("Head", "Controller", "Hand", "Body", "Motion")
HAND_SIDES = ("leftHand", "rightHand")
CTRL_SIDES = ("left", "right")
PALM_JOINT = 0

# Match Pico TrackingData HandFpPosScale / Aux near-dup & true-change gates.
POS_QUANT_PER_M = 10000.0          # 0.1 mm bins
DEFAULT_NEAR_DUP_M = 1e-5          # 0.01 mm
DEFAULT_TRUE_CHANGE_M = 5e-4       # 0.5 mm

GLOSSARY = """
=== metric meanings ===

PACKET
  arriv Hz          Tracking frames / s on the receive clock.
  gap p50/p99       Inter-arrival. Large p99 = stall / Wi-Fi / backpressure.

PER SOURCE
  present Hz        Packets that carried this key (B0 may omit unchanged).
  unique Hz         Full JSON hash changed vs last present.
  u/p               unique/present. ≪1 ⇒ many duplicate payloads on the wire.

HAND L/R
  isActive duty     Among side presents, fraction isActive != 0 (Pico 0/1 quality).
  activeMismatch    Both sides in packet, one active one not.
  oneSideMiss       Only leftHand or only rightHand key.
  falseInactive     isActive==0 but palm |xyz| still large (pose may still be usable).

TRUE-RATE (adjacent Hand samples that carried Hand)
  nearDup Hz        |Δ| on active-palm ref < --near-dup-m.
                    ≈ same pose re-sent. Unstable if you average inactive palms
                    (isActive flicker) — script uses active sides only.
  microΔ / trueΔ    Intermediate / large motion bins.
  quantUnique Hz    Active-palm xyz after 0.1mm quant changed.
  L/R quant Hz      Per-side palm quant changes (asymmetry of motion/noise).
  hint              After APK: Hand/Tracker follow panel Hz — arriv should ≈ panel
                    when Hand-only. Compare Hand quantUnique to Controller in the
                    same run. nearDup rises when nearly still; during grasp trueΔ
                    dominates (not "empty poll").

CONTROLLER (when present)
  Same adjacent |Δ| / quantUnique on left/right controller pose xyz.

EE
  Need Hand quantUnique (or content unique) ≥100 while moving, not raw arriv.
""".strip()


def read_frames(sock: socket.socket, buffer: bytearray):
    while True:
        data = sock.recv(65536)
        if not data:
            return
        buffer.extend(data)
        while True:
            if len(buffer) < 15:
                break
            if buffer[0] != HEAD:
                idx = buffer.find(HEAD)
                if idx < 0:
                    del buffer[:]
                    break
                del buffer[:idx]
                continue
            cmd = buffer[1]
            length = struct.unpack("<i", bytes(buffer[2:6]))[0]
            if length < 0 or length > 4 * 1024 * 1024:
                del buffer[:1]
                continue
            total = 15 + length
            if len(buffer) < total:
                break
            payload = bytes(buffer[6:6 + length])
            end = buffer[total - 1]
            if end != END:
                del buffer[:1]
                continue
            del buffer[:total]
            yield cmd, payload


def decompress_value(value: Any) -> Any:
    if isinstance(value, str):
        try:
            return json.loads(value)
        except ValueError:
            return None
    return value


def pct(sorted_vals: list[float], p: float) -> float:
    if not sorted_vals:
        return 0.0
    k = (len(sorted_vals) - 1) * p / 100.0
    lo, hi = int(k), min(int(k) + 1, len(sorted_vals) - 1)
    return sorted_vals[lo] + (sorted_vals[hi] - sorted_vals[lo]) * (k - lo)


def blake8(obj: Any) -> bytes:
    ser = json.dumps(obj, sort_keys=True, separators=(",", ":"))
    return hashlib.blake2b(ser.encode(), digest_size=8).digest()


def parse_pose_xyz(p: Any) -> tuple[float, float, float] | None:
    if not isinstance(p, str):
        return None
    parts = p.split(",")
    if len(parts) < 3:
        return None
    try:
        return float(parts[0]), float(parts[1]), float(parts[2])
    except ValueError:
        return None


def quant_xyz(xyz: tuple[float, float, float]) -> tuple[int, int, int]:
    return (
        int(round(xyz[0] * POS_QUANT_PER_M)),
        int(round(xyz[1] * POS_QUANT_PER_M)),
        int(round(xyz[2] * POS_QUANT_PER_M)),
    )


def dist_m(a: tuple[float, float, float], b: tuple[float, float, float]) -> float:
    dx, dy, dz = a[0] - b[0], a[1] - b[1], a[2] - b[2]
    return math.sqrt(dx * dx + dy * dy + dz * dz)


def joint_pose_str(hand: dict, index: int) -> str | None:
    locs = hand.get("HandJointLocations")
    if not isinstance(locs, list) or index >= len(locs):
        return None
    j = locs[index]
    if not isinstance(j, dict):
        return None
    p = j.get("p")
    return p if isinstance(p, str) else None


def as_active(flag: Any) -> int:
    try:
        return 1 if float(flag) > 0.5 else 0
    except (TypeError, ValueError):
        return 0


def palm_xyz(hand_side: dict) -> tuple[float, float, float] | None:
    return parse_pose_xyz(joint_pose_str(hand_side, PALM_JOINT))


def hand_side_palms(hand: dict) -> dict[str, tuple[float, float, float]]:
    out: dict[str, tuple[float, float, float]] = {}
    for side in HAND_SIDES:
        obj = hand.get(side)
        if not isinstance(obj, dict):
            continue
        xyz = palm_xyz(obj)
        if xyz is not None:
            out[side] = xyz
    return out


def hand_ref_palm(hand: dict) -> tuple[float, float, float] | None:
    """Palm reference for adjacent Δ: average of *active* sides only.

    Mixing inactive palms into a mid-point makes nearDup unstable when isActive
    flickers (pose can jump while flagged inactive). Fall back to any present.
    """
    pts = []
    fallback = []
    for side in HAND_SIDES:
        obj = hand.get(side)
        if not isinstance(obj, dict):
            continue
        xyz = palm_xyz(obj)
        if xyz is None:
            continue
        fallback.append(xyz)
        if as_active(obj.get("isActive", 0)):
            pts.append(xyz)
    use = pts if pts else fallback
    if not use:
        return None
    n = float(len(use))
    return (sum(p[0] for p in use) / n,
            sum(p[1] for p in use) / n,
            sum(p[2] for p in use) / n)


def controller_mid_pose(ctrl: dict) -> tuple[float, float, float] | None:
    pts = []
    for side in CTRL_SIDES:
        obj = ctrl.get(side)
        if isinstance(obj, dict):
            xyz = parse_pose_xyz(obj.get("pose"))
            if xyz is not None:
                pts.append(xyz)
    if not pts:
        return None
    n = float(len(pts))
    return (sum(p[0] for p in pts) / n,
            sum(p[1] for p in pts) / n,
            sum(p[2] for p in pts) / n)


class DeltaStats:
    """Adjacent-sample classifier for one stream (Hand / Controller)."""

    __slots__ = (
        "pairs", "near", "micro", "true", "quant_changed",
        "near_sum", "true_sum", "last_xyz", "last_quant",
    )

    def __init__(self) -> None:
        self.pairs = 0
        self.near = 0
        self.micro = 0
        self.true = 0
        self.quant_changed = 0
        self.near_sum = 0.0
        self.true_sum = 0.0
        self.last_xyz: tuple[float, float, float] | None = None
        self.last_quant: tuple[int, int, int] | None = None

    def observe(self, xyz: tuple[float, float, float] | None,
                near_m: float, true_m: float) -> str | None:
        """Observe one sample; return class label for this adjacent pair."""
        if xyz is None:
            return None
        q = quant_xyz(xyz)
        label = None
        if self.last_xyz is not None:
            self.pairs += 1
            d = dist_m(xyz, self.last_xyz)
            if d < near_m:
                self.near += 1
                self.near_sum += d
                label = "near"
            elif d >= true_m:
                self.true += 1
                self.true_sum += d
                label = "true"
            else:
                self.micro += 1
                label = "micro"
        if self.last_quant is None or q != self.last_quant:
            self.quant_changed += 1
        self.last_xyz = xyz
        self.last_quant = q
        return label

    def snapshot_hz(self, elapsed: float) -> dict[str, float]:
        e = max(1e-6, elapsed)
        return {
            "near_hz": self.near / e,
            "micro_hz": self.micro / e,
            "true_hz": self.true / e,
            "quant_hz": self.quant_changed / e,
            "near_mean_mm": (1000.0 * self.near_sum / self.near) if self.near else 0.0,
            "true_mean_mm": (1000.0 * self.true_sum / self.true) if self.true else 0.0,
            "pairs": float(self.pairs),
        }


class Window:
    __slots__ = (
        "start", "packets", "bytes", "gaps",
        "present", "unique",
        "hand_side_present", "hand_side_active", "hand_side_unique_palm",
        "hand_both", "hand_mismatch", "hand_one_side", "hand_false_inactive",
        "input_hist",
        "hand_near", "hand_micro", "hand_true", "hand_quant",
        "hand_l_quant", "hand_r_quant",
        "ctrl_near", "ctrl_micro", "ctrl_true", "ctrl_quant",
    )

    def __init__(self, start: float):
        self.start = start
        self.packets = 0
        self.bytes = 0
        self.gaps: list[float] = []
        self.present = {s: 0 for s in SOURCES}
        self.unique = {s: 0 for s in SOURCES}
        self.hand_side_present = {s: 0 for s in HAND_SIDES}
        self.hand_side_active = {s: 0 for s in HAND_SIDES}
        self.hand_side_unique_palm = {s: 0 for s in HAND_SIDES}
        self.hand_both = 0
        self.hand_mismatch = 0
        self.hand_one_side = 0
        self.hand_false_inactive = {s: 0 for s in HAND_SIDES}
        self.input_hist: collections.Counter[int] = collections.Counter()
        self.hand_near = 0
        self.hand_micro = 0
        self.hand_true = 0
        self.hand_quant = 0
        self.hand_l_quant = 0
        self.hand_r_quant = 0
        self.ctrl_near = 0
        self.ctrl_micro = 0
        self.ctrl_true = 0
        self.ctrl_quant = 0


def fmt_duty(active: int, present: int) -> str:
    if present <= 0:
        return "n/a"
    return f"{active / present:.2f}"


def print_window(w: Window, quiet: bool) -> None:
    if quiet:
        return
    elapsed = max(1e-6, time.time() - w.start)
    arriv = w.packets / elapsed
    print(f"--- window {elapsed:.1f}s  Tracking arriv={arriv:.1f}Hz  "
          f"{w.bytes / elapsed / 1024:.1f} KiB/s ---", flush=True)

    bits = []
    for s in SOURCES:
        if not w.present[s]:
            continue
        u = w.unique[s] / elapsed
        p = w.present[s] / elapsed
        ratio = w.unique[s] / max(1, w.present[s])
        bits.append(f"{s}:{u:.1f}uniq/{p:.1f}pres(u/p={ratio:.2f})")
    if bits:
        print("  sources  " + "  ".join(bits), flush=True)

    if w.present["Hand"]:
        hp = w.present["Hand"]
        print(
            f"  Hand L/R isActive duty="
            f"{fmt_duty(w.hand_side_active['leftHand'], w.hand_side_present['leftHand'])}/"
            f"{fmt_duty(w.hand_side_active['rightHand'], w.hand_side_present['rightHand'])}  "
            f"mismatch={w.hand_mismatch} oneSideMiss={w.hand_one_side} "
            f"({100.0 * w.hand_one_side / max(1, hp):.0f}% Hand)",
            flush=True,
        )
        print(
            f"  Hand true-rate  nearDup={w.hand_near / elapsed:.1f}  "
            f"microΔ={w.hand_micro / elapsed:.1f}  trueΔ={w.hand_true / elapsed:.1f}  "
            f"quantUnique={w.hand_quant / elapsed:.1f} Hz  "
            f"L/R quant={w.hand_l_quant / elapsed:.1f}/{w.hand_r_quant / elapsed:.1f}  "
            f"(ref=active palms; ≈ panel after APK rate align)",
            flush=True,
        )
    elif arriv > 0:
        print("  Hand: not present this window (B0 omit or Hand off)", flush=True)

    if w.present["Controller"]:
        print(
            f"  Ctrl true-rate nearDup={w.ctrl_near / elapsed:.1f}  "
            f"microΔ={w.ctrl_micro / elapsed:.1f}  trueΔ={w.ctrl_true / elapsed:.1f}  "
            f"quantUnique={w.ctrl_quant / elapsed:.1f} Hz",
            flush=True,
        )


def print_summary(totals: dict[str, Any], dur: float, near_m: float, true_m: float) -> None:
    print("\n=== tracking recv diag summary ===")
    print(f"duration {dur:.1f}s  packets {totals['packets']}  "
          f"arriv {totals['packets'] / dur:.1f}Hz  "
          f"{totals['bytes'] / dur / 1024:.1f} KiB/s")
    gaps = sorted(totals["gaps"]) or [0.0]
    print(f"packet gap p50 {pct(gaps, 50):.2f}ms  p95 {pct(gaps, 95):.2f}ms  "
          f"p99 {pct(gaps, 99):.2f}ms  max {gaps[-1]:.2f}ms")

    print("\n-- sources --")
    print(f"{'src':<12} {'present':>8} {'unique':>8} {'u/p':>6}  "
          f"{'chg p50':>8} {'chg p99':>8}")
    for s in SOURCES:
        pr = totals["present"][s]
        un = totals["unique"][s]
        if not pr:
            continue
        iv = sorted(totals["change_iv"][s]) or [0.0]
        print(f"{s:<12} {pr / dur:8.1f}/s {un / dur:8.1f}/s "
              f"{un / max(1, pr):6.2f}  "
              f"{pct(iv, 50):7.1f}ms {pct(iv, 99):7.1f}ms")

    hp = totals["hand_present"]
    if hp:
        print("\n-- Hand L/R --")
        for side, tag in (("leftHand", "L"), ("rightHand", "R")):
            sp = totals["side_present"][side]
            sa = totals["side_active"][side]
            fi = totals["false_inactive"][side]
            duty = sa / sp if sp else 0.0
            print(f"  {tag} present {sp / dur:6.1f}/s  isActive duty={duty:.3f}  "
                  f"falseInactive {fi} ({100.0 * fi / max(1, sp):.1f}%)")
        both = totals["hand_both"]
        print(f"  bothSides {both}  activeMismatch {totals['mismatch']} "
              f"({100.0 * totals['mismatch'] / max(1, both):.1f}% of both)  "
              f"oneSideMiss {totals['one_side']} "
              f"({100.0 * totals['one_side'] / max(1, hp):.1f}% of Hand)")

        hs: DeltaStats = totals["hand_delta"]
        snap = hs.snapshot_hz(dur)
        print("\n-- Hand true-rate (adjacent Hand presents) --")
        print(f"  thresholds  nearDup < {near_m * 1000:.3f}mm  "
              f"trueΔ ≥ {true_m * 1000:.2f}mm  quant=0.1mm")
        print(f"  nearDup {snap['near_hz']:6.1f}/s  microΔ {snap['micro_hz']:6.1f}/s  "
              f"trueΔ {snap['true_hz']:6.1f}/s  quantUnique {snap['quant_hz']:6.1f}/s")
        print(f"  nearDupMean {snap['near_mean_mm']:.4f}mm  "
              f"trueMean {snap['true_mean_mm']:.2f}mm  "
              f"pairs={int(snap['pairs'])}")
        ls = totals["hand_l_delta"].snapshot_hz(dur)
        rs = totals["hand_r_delta"].snapshot_hz(dur)
        print(f"  L/R palm quantUnique {ls['quant_hz']:.1f}/{rs['quant_hz']:.1f}  "
              f"trueΔ {ls['true_hz']:.1f}/{rs['true_hz']:.1f}  "
              f"nearDup {ls['near_hz']:.1f}/{rs['near_hz']:.1f}")
        useful = snap["quant_hz"]
        status = "OK for EE≥100" if useful >= 100.0 else "BELOW EE≥100 if this was a moving scene"
        print(f"  → useful Hand Hz ≈ quantUnique={useful:.1f}  [{status}]")
        print("  → expect arriv ≈ panel CollectionRate after Aux follows panel "
              "(Hand-only). During grasp, low nearDup is normal (real motion).")
        if snap["near_hz"] > useful * 0.5 and useful > 0:
            print("  → high nearDup vs quantUnique: wire still carries near-duplicate Hand "
                  "(B0 off, keepalive, or float-only churn before device quantize)")

    if totals["present"]["Controller"]:
        snap = totals["ctrl_delta"].snapshot_hz(dur)
        print("\n-- Controller true-rate (adjacent Controller presents) --")
        print(f"  nearDup {snap['near_hz']:6.1f}/s  microΔ {snap['micro_hz']:6.1f}/s  "
              f"trueΔ {snap['true_hz']:6.1f}/s  quantUnique {snap['quant_hz']:6.1f}/s")
        print(f"  nearDupMean {snap['near_mean_mm']:.4f}mm  "
              f"trueMean {snap['true_mean_mm']:.2f}mm")
        print(f"  → useful Controller Hz ≈ quantUnique={snap['quant_hz']:.1f}")

    print("\n" + GLOSSARY)


def main() -> int:
    ap = argparse.ArgumentParser(
        description="Diagnose Tracking TCP: isActive L/R + true-rate from adjacent deltas.")
    ap.add_argument("--bind", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=63901)
    ap.add_argument("--duration", type=float, default=0.0)
    ap.add_argument("--window", type=float, default=1.0)
    ap.add_argument("--csv", default="")
    ap.add_argument("--quiet", action="store_true")
    ap.add_argument("--explain", action="store_true")
    ap.add_argument("--false-inactive-m", type=float, default=0.10)
    ap.add_argument("--near-dup-m", type=float, default=DEFAULT_NEAR_DUP_M,
                    help="palm |Δ| below this = nearDup (default 1e-5 m)")
    ap.add_argument("--true-change-m", type=float, default=DEFAULT_TRUE_CHANGE_M,
                    help="palm |Δ| at/above this = trueΔ (default 5e-4 m)")
    args = ap.parse_args()

    if args.explain:
        print(GLOSSARY)
        return 0
    if args.window <= 0:
        print("--window must be > 0", file=sys.stderr)
        return 2
    if args.near_dup_m <= 0 or args.true_change_m <= args.near_dup_m:
        print("--true-change-m must be > --near-dup-m > 0", file=sys.stderr)
        return 2

    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        srv.bind((args.bind, args.port))
    except OSError as err:
        print(f"cannot bind {args.bind}:{args.port}: {err.strerror}", file=sys.stderr)
        if err.errno == 98:
            print("  stop PC service on 63901, or use --port 63902 + fake_headset.py",
                  file=sys.stderr)
        return 1
    srv.listen(2)
    if not args.quiet:
        print(f"listening on {args.bind}:{args.port} (waiting for headset)...", flush=True)
        print(f"true-rate gates: nearDup<{args.near_dup_m * 1000:.3f}mm  "
              f"trueΔ≥{args.true_change_m * 1000:.2f}mm  quant=0.1mm", flush=True)
        print("hint: --explain for metric glossary", flush=True)

    srv.settimeout(1.0)
    conn = None
    wait_start = time.time()
    while conn is None:
        try:
            conn, addr = srv.accept()
            if not args.quiet:
                print(f"connected {addr[0]}:{addr[1]}", flush=True)
        except socket.timeout:
            if args.duration and time.time() - wait_start > args.duration + 30:
                print("no client connected, giving up")
                return 1
    conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    conn.settimeout(min(0.2, args.window))

    buffer = bytearray()
    last_sig = {s: None for s in SOURCES}
    last_change_t = {s: None for s in SOURCES}
    last_palm_str = {s: None for s in HAND_SIDES}
    change_iv = {s: collections.deque(maxlen=30000) for s in SOURCES}
    hand_delta = DeltaStats()
    hand_l_delta = DeltaStats()
    hand_r_delta = DeltaStats()
    ctrl_delta = DeltaStats()
    # Per-window quant/near counters need edge detection vs previous window state:
    # DeltaStats is cumulative; window increments are diffs of cumulative counters.
    prev_hand_near = prev_hand_micro = prev_hand_true = prev_hand_quant = 0
    prev_hand_l_quant = prev_hand_r_quant = 0
    prev_ctrl_near = prev_ctrl_micro = prev_ctrl_true = prev_ctrl_quant = 0
    prev_pkt_t = None
    first_t = None
    last_t = None

    totals: dict[str, Any] = {
        "packets": 0,
        "bytes": 0,
        "gaps": [],
        "present": {s: 0 for s in SOURCES},
        "unique": {s: 0 for s in SOURCES},
        "change_iv": change_iv,
        "hand_present": 0,
        "side_present": {s: 0 for s in HAND_SIDES},
        "side_active": {s: 0 for s in HAND_SIDES},
        "false_inactive": {s: 0 for s in HAND_SIDES},
        "hand_both": 0,
        "mismatch": 0,
        "one_side": 0,
        "hand_delta": hand_delta,
        "hand_l_delta": hand_l_delta,
        "hand_r_delta": hand_r_delta,
        "ctrl_delta": ctrl_delta,
    }
    csv_rows: list[dict[str, Any]] = []
    w = Window(time.time())
    start = time.time()

    def sync_window_delta_counters() -> None:
        nonlocal prev_hand_near, prev_hand_micro, prev_hand_true, prev_hand_quant
        nonlocal prev_hand_l_quant, prev_hand_r_quant
        nonlocal prev_ctrl_near, prev_ctrl_micro, prev_ctrl_true, prev_ctrl_quant
        w.hand_near = hand_delta.near - prev_hand_near
        w.hand_micro = hand_delta.micro - prev_hand_micro
        w.hand_true = hand_delta.true - prev_hand_true
        w.hand_quant = hand_delta.quant_changed - prev_hand_quant
        w.hand_l_quant = hand_l_delta.quant_changed - prev_hand_l_quant
        w.hand_r_quant = hand_r_delta.quant_changed - prev_hand_r_quant
        w.ctrl_near = ctrl_delta.near - prev_ctrl_near
        w.ctrl_micro = ctrl_delta.micro - prev_ctrl_micro
        w.ctrl_true = ctrl_delta.true - prev_ctrl_true
        w.ctrl_quant = ctrl_delta.quant_changed - prev_ctrl_quant

    def roll_window(now: float) -> None:
        nonlocal w
        nonlocal prev_hand_near, prev_hand_micro, prev_hand_true, prev_hand_quant
        nonlocal prev_hand_l_quant, prev_hand_r_quant
        nonlocal prev_ctrl_near, prev_ctrl_micro, prev_ctrl_true, prev_ctrl_quant
        sync_window_delta_counters()
        if w.packets:
            print_window(w, args.quiet)
            if args.csv:
                elapsed = max(1e-6, now - w.start)
                csv_rows.append({
                    "t": int(now),
                    "arriv_hz": w.packets / elapsed,
                    "hand_unique_hz": w.unique["Hand"] / elapsed,
                    "hand_present_hz": w.present["Hand"] / elapsed,
                    "hand_near_hz": w.hand_near / elapsed,
                    "hand_micro_hz": w.hand_micro / elapsed,
                    "hand_true_hz": w.hand_true / elapsed,
                    "hand_quant_hz": w.hand_quant / elapsed,
                    "hand_l_quant_hz": w.hand_l_quant / elapsed,
                    "hand_r_quant_hz": w.hand_r_quant / elapsed,
                    "ctrl_quant_hz": w.ctrl_quant / elapsed,
                    "duty_l": (w.hand_side_active["leftHand"] /
                               max(1, w.hand_side_present["leftHand"])),
                    "duty_r": (w.hand_side_active["rightHand"] /
                               max(1, w.hand_side_present["rightHand"])),
                    "mismatch": w.hand_mismatch,
                    "one_side": w.hand_one_side,
                })
        prev_hand_near = hand_delta.near
        prev_hand_micro = hand_delta.micro
        prev_hand_true = hand_delta.true
        prev_hand_quant = hand_delta.quant_changed
        prev_hand_l_quant = hand_l_delta.quant_changed
        prev_hand_r_quant = hand_r_delta.quant_changed
        prev_ctrl_near = ctrl_delta.near
        prev_ctrl_micro = ctrl_delta.micro
        prev_ctrl_true = ctrl_delta.true
        prev_ctrl_quant = ctrl_delta.quant_changed
        w = Window(now)

    try:
        while True:
            now = time.time()
            if now - w.start >= args.window:
                roll_window(now)

            try:
                for cmd, payload in read_frames(conn, buffer):
                    now = time.time()
                    if now - w.start >= args.window:
                        roll_window(now)

                    if cmd != CMD_TRACKING:
                        continue

                    try:
                        outer = json.loads(payload.decode("utf-8"))
                    except ValueError:
                        continue
                    if outer.get("functionName") != "Tracking":
                        continue
                    value = decompress_value(outer.get("value"))
                    if not isinstance(value, dict):
                        continue

                    if first_t is None:
                        first_t = now
                    last_t = now
                    totals["packets"] += 1
                    totals["bytes"] += len(payload)
                    w.packets += 1
                    w.bytes += len(payload)

                    if prev_pkt_t is not None:
                        gap = (now - prev_pkt_t) * 1000.0
                        totals["gaps"].append(gap)
                        w.gaps.append(gap)
                    prev_pkt_t = now

                    inp = value.get("Input")
                    if inp is not None:
                        try:
                            w.input_hist[int(inp)] += 1
                        except (TypeError, ValueError):
                            pass

                    for s in SOURCES:
                        if s not in value:
                            continue
                        totals["present"][s] += 1
                        w.present[s] += 1
                        sig = blake8(value[s])
                        if sig != last_sig[s]:
                            last_sig[s] = sig
                            totals["unique"][s] += 1
                            w.unique[s] += 1
                            if last_change_t[s] is not None:
                                change_iv[s].append((now - last_change_t[s]) * 1000.0)
                            last_change_t[s] = now

                    ctrl = value.get("Controller")
                    if isinstance(ctrl, dict):
                        ctrl_delta.observe(
                            controller_mid_pose(ctrl),
                            args.near_dup_m,
                            args.true_change_m,
                        )

                    hand = value.get("Hand")
                    if not isinstance(hand, dict):
                        continue

                    totals["hand_present"] += 1
                    hand_delta.observe(
                        hand_ref_palm(hand),
                        args.near_dup_m,
                        args.true_change_m,
                    )
                    palms = hand_side_palms(hand)
                    if "leftHand" in palms:
                        hand_l_delta.observe(
                            palms["leftHand"], args.near_dup_m, args.true_change_m)
                    if "rightHand" in palms:
                        hand_r_delta.observe(
                            palms["rightHand"], args.near_dup_m, args.true_change_m)

                    sides_ok = []
                    actives = {}
                    for side in HAND_SIDES:
                        side_obj = hand.get(side)
                        if not isinstance(side_obj, dict):
                            continue
                        sides_ok.append(side)
                        totals["side_present"][side] += 1
                        w.hand_side_present[side] += 1
                        active = as_active(side_obj.get("isActive", 0))
                        actives[side] = active
                        if active:
                            totals["side_active"][side] += 1
                            w.hand_side_active[side] += 1
                        else:
                            xyz = palm_xyz(side_obj)
                            if xyz is not None:
                                r = math.sqrt(xyz[0] ** 2 + xyz[1] ** 2 + xyz[2] ** 2)
                                if r >= args.false_inactive_m:
                                    totals["false_inactive"][side] += 1
                                    w.hand_false_inactive[side] += 1

                        palm = joint_pose_str(side_obj, PALM_JOINT)
                        if palm is not None and palm != last_palm_str[side]:
                            last_palm_str[side] = palm
                            w.hand_side_unique_palm[side] += 1

                    if len(sides_ok) == 2:
                        totals["hand_both"] += 1
                        w.hand_both += 1
                        if actives.get("leftHand", 0) != actives.get("rightHand", 0):
                            totals["mismatch"] += 1
                            w.hand_mismatch += 1
                    elif len(sides_ok) == 1:
                        totals["one_side"] += 1
                        w.hand_one_side += 1

            except socket.timeout:
                pass

            if args.duration and time.time() - start >= args.duration:
                break
    except KeyboardInterrupt:
        pass
    finally:
        now = time.time()
        if w.packets or (first_t is not None):
            roll_window(now)
        try:
            conn.close()
        except OSError:
            pass
        srv.close()

    if first_t is None or last_t is None or totals["packets"] == 0:
        print("\nno Tracking packets received")
        print(GLOSSARY)
        return 1

    dur = max(1e-6, last_t - first_t)
    print_summary(totals, dur, args.near_dup_m, args.true_change_m)

    if args.csv and csv_rows:
        with open(args.csv, "w", encoding="utf-8") as fh:
            keys = list(csv_rows[0].keys())
            fh.write(",".join(keys) + "\n")
            for row in csv_rows:
                fh.write(",".join(str(row[k]) for k in keys) + "\n")
        print(f"\nper-window csv -> {args.csv}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
