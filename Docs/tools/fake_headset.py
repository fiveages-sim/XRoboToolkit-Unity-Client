#!/usr/bin/env python3
"""Pretend to be the headset so a PC receiver can be exercised without a PICO.

The headset is the TCP client (TcpHandler.Connect -> <PC>:63901), so this script
connects out to a listener. Pair it with Docs/tools/tracking_probe.py (which
plays the PC side) or with your own receiver.

Source rates default to what the device actually produces when the Enterprise
sampler runs at 300 Hz: Head 300, Controller 90, Hand 90. Each source keeps its
value frozen between its own samples, exactly like the real caches do, which is
what makes the bundle-vs-split bandwidth difference visible.

--design
  bundle          what the firmware sends today: one packet per Head sample
                  carrying the latest value of every enabled source, whether or
                  not that source changed.
  bundle-changed  same cadence, but only sources that produced a new sample are
                  included in the packet.
  split           one packet per source event, carrying only that source.

Payload shapes mirror TrackingData / TcpHandler so an existing receiver can parse
them unchanged. --v2 additionally adds per-source seq/tsNs, which is the part of
the proposal the current protocol is missing.
"""

import argparse
import json
import math
import socket
import struct
import sys
import time

HEAD_MARK = 0x3F
END_MARK = 0xA5
CMD_TRACKING = 0x6D
CMD_VERSION = 0x6C

DEFAULT_RATES = "Head=300,Controller=90,Hand=90"

# One value per source, matching the real serialised shapes.
DEFAULT_POSE = "0.0000000,0.0000000,0.0000000,0.0000000,0.0000000,0.0000000,1.0000000"
BODY_JOINTS = 24
MOTION_TRACKERS = 3
# UI "Tracker" maps to Motion (Motion Tracker) or Body (full-body). Both accepted.
RATE_ALIASES = {"Tracker": "Motion"}


def pack(cmd, payload, ts_ms=None):
    # Match PackageHandle.Pack: LE length + LE unix-ms timestamp.
    if ts_ms is None:
        ts_ms = int(time.time() * 1000)
    return (bytes([HEAD_MARK, cmd]) + struct.pack("<i", len(payload)) + payload
            + struct.pack("<q", ts_ms) + bytes([END_MARK]))


def pose_str(t, salt):
    return ",".join(f"{math.sin(t * 3.1 + salt + i * 0.7):.7f}" for i in range(7))


def hand_value(t):
    hands = {}
    for side, salt in (("leftHand", 0.0), ("rightHand", 1.5)):
        joints = []
        for joint in range(26):
            joints.append({
                "p": pose_str(t, salt + joint * 0.11),
                "s": 3,
                "r": 0.0085,
            })
        hands[side] = {"isActive": True, "count": 26, "scale": 1.0,
                       "HandJointLocations": joints}
    return hands


def controller_value(t):
    out = {}
    for side, salt in (("left", 0.0), ("right", 1.0)):
        out[side] = {
            "axisX": 0.0, "axisY": 0.0, "axisClick": False, "grip": 0.0,
            "trigger": 0.0, "primaryButton": False, "secondaryButton": False,
            "menuButton": False, "inputDeviceValid": True, "axisValid": True,
            "axisClickValid": True, "gripValid": True, "triggerValid": True,
            "primaryButtonValid": True, "secondaryButtonValid": True,
            "menuButtonValid": True, "hasPose": True,
            "pose": pose_str(t, salt), "status": 3.0,
            "timeStampNs": int(t * 1e9), "type": 1.0, "poseError": 0.0,
        }
    return out


def head_value(t):
    return {"pose": pose_str(t, 0.0), "status": 3}


def body_value(t):
    # Mirrors TrackingData.FillBodyTrackingJson: len + joints[].p/va/wva/t
    joints = []
    for i in range(BODY_JOINTS):
        joints.append({
            "p": pose_str(t, i * 0.17),
            "t": int(t * 1e9),
            "va": "0,0,0,0,0,0",
            "wva": "0,0,0,0,0,0",
        })
    return {"len": BODY_JOINTS, "joints": joints}


def motion_value(t):
    # Mirrors TrackingData.FillMotionTrackingJson: len + joints[].p/va/wva/sn
    joints = []
    for i in range(MOTION_TRACKERS):
        joints.append({
            "p": pose_str(t, i * 0.31),
            "va": "0,0,0,0,0,0",
            "wva": "0,0,0,0,0,0",
            "sn": str(1000 + i),
        })
    return {"len": MOTION_TRACKERS, "joints": joints}


BUILDERS = {
    "Head": head_value,
    "Controller": controller_value,
    "Hand": hand_value,
    "Body": body_value,
    "Motion": motion_value,
}


def parse_rates(text):
    rates = {}
    for chunk in text.split(","):
        chunk = chunk.strip()
        if not chunk:
            continue
        name, _, hz = chunk.partition("=")
        name = RATE_ALIASES.get(name.strip(), name.strip())
        rates[name] = float(hz)
    return rates


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=63901)
    ap.add_argument("--seconds", type=float, default=10.0)
    ap.add_argument("--rates", default=DEFAULT_RATES,
                    help=f'source rates, e.g. "{DEFAULT_RATES}"')
    ap.add_argument("--design", default="bundle",
                    choices=("bundle", "bundle-changed", "split"))
    ap.add_argument("--v2", action="store_true",
                    help="add per-source seq / tsNs (proposed protocol)")
    ap.add_argument("--version", default="FAKEHEAD0001|1.0|1.1.2")
    ap.add_argument("--wait", type=float, default=5.0,
                    help="seconds to keep retrying while nothing is listening")
    args = ap.parse_args()

    rates = {name: hz for name, hz in parse_rates(args.rates).items() if hz > 0}
    unknown = set(rates) - set(BUILDERS)
    if unknown:
        print(f"unknown sources: {sorted(unknown)} (known: {sorted(BUILDERS)})")
        return 1
    if not rates:
        print("no enabled sources")
        return 1

    deadline = time.time() + args.wait
    while True:
        try:
            sock = socket.create_connection((args.host, args.port), timeout=2.0)
            break
        except OSError as err:
            if time.time() >= deadline:
                print(f"no listener at {args.host}:{args.port} ({err.strerror}).\n"
                      f"  start the receiver first, e.g.\n"
                      f"    python3 Docs/tools/tracking_probe.py --port {args.port}\n"
                      f"  then rerun this script (or raise --wait).", file=sys.stderr)
                return 1
            time.sleep(0.3)
    sock.settimeout(None)
    sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    sock.sendall(pack(CMD_VERSION, args.version.encode()))

    start = time.time()
    # Each source runs on its own schedule; a source that is not yet due keeps
    # its previous value, which is exactly what the firmware caches do.
    state = {name: {"t": -1.0, "value": None, "seq": 0, "dirty": False,
                    "next": start + 1.0 / rates[name]}
             for name in rates}
    sent_packets = 0
    sent_bytes = 0
    per_source_bytes = {name: 0 for name in rates}
    next_tick = start

    def refresh(name, now):
        info = state[name]
        if now < info["next"]:
            return False
        period = 1.0 / rates[name]
        while info["next"] <= now:
            info["next"] += period
        info["t"] = now
        info["value"] = BUILDERS[name](now)
        info["seq"] += 1
        info["dirty"] = True
        return True

    def value_for(name, source_obj):
        info = state[name]
        if not args.v2:
            return source_obj
        wrapped = {"seq": info["seq"], "tsNs": int(now_ns(info["t"]))}
        if isinstance(source_obj, dict):
            wrapped.update(source_obj)
            return wrapped
        return wrapped

    def now_ns(t):
        return t * 1e9

    def emit(names):
        nonlocal sent_packets, sent_bytes
        body = {}
        for name in names:
            obj = state[name]["value"]
            ser = value_for(name, obj)
            body[name] = ser
            per_source_bytes[name] += len(json.dumps(ser, separators=(",", ":")))
        body["timeStampNs"] = int(time.time() * 1e9)
        body["appState"] = {"focus": True}
        body["Input"] = 1
        outer = {"functionName": "Tracking",
                 "value": json.dumps(body, separators=(",", ":"))}
        blob = json.dumps(outer, separators=(",", ":")).encode()
        sock.sendall(pack(CMD_TRACKING, blob))
        sent_packets += 1
        sent_bytes += len(blob)

    try:
        while True:
            now = time.time()
            if now - start >= args.seconds:
                break

            for name in rates:
                refresh(name, now)

            if args.design == "split":
                for name in rates:
                    if state[name]["dirty"]:
                        emit([name])
                        state[name]["dirty"] = False
                interval = 1.0 / max(rates.values()) / 4.0
            else:
                names = list(rates)
                if args.design == "bundle-changed":
                    names = [n for n in rates if state[n]["dirty"]]
                if names:
                    emit(names)
                for name in rates:
                    state[name]["dirty"] = False
                interval = 1.0 / max(rates.values())

            next_tick += interval
            delay = next_tick - time.time()
            if delay > 0:
                time.sleep(delay)
            else:
                next_tick = time.time()
    except KeyboardInterrupt:
        pass
    finally:
        sock.close()

    elapsed = max(1e-6, time.time() - start)
    print(f"design={args.design}{' v2' if args.v2 else ''} sources={','.join(rates)}")
    print(f"  sent {sent_packets} packets in {elapsed:.1f}s  {sent_packets / elapsed:.0f}/s"
          f"  {sent_bytes / elapsed / 1024:.1f} KiB/s")
    for name, nbytes in per_source_bytes.items():
        print(f"  {name:<10} {nbytes / elapsed / 1024:7.1f} KiB/s payload")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
