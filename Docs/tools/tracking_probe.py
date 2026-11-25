#!/usr/bin/env python3
"""Listen as the PC-side service and profile the headset's Tracking stream.

REAL DEVICE (panel CollectionRate applies):
  1. Stop whatever already owns 63901 (PC service).
  2. python3 Docs/tools/tracking_probe.py --port 63901
  3. On Pico: Enterprise source, Send on, toggle Head/Controller/Hand/Tracker.
  Live lines look like `ros2 topic hz` — average send rate every --window seconds.

SIMULATION (panel ignored; rates come from fake_headset.py):
  Docs/tools/selftest.sh --suite designs
  # or manually:
  python3 Docs/tools/tracking_probe.py --port 63902 --duration 10 --quiet
  python3 Docs/tools/fake_headset.py --port 63902 --seconds 8 --rates Head=400

Frame layout (PackageHandle.Pack — little-endian length, matches Unity BitConverter):
    head(0x3F) cmd(1B) len(4B little-endian) payload(len) ts(8B little-endian, ms) end(0xA5)

Payload of cmd 0x6D (PACKET_CCMD_TO_CONTROLLER_FUNCTION):
    {"functionName":"Tracking","value":"<escaped json string>"}
"""

import argparse
import collections
import hashlib
import json
import math
import socket
import struct
import sys
import time

HEAD = 0x3F
END = 0xA5
CMD_CONNECT = 0x19
CMD_TRACKING = 0x6D
CMD_VERSION = 0x6C
CMD_HEARTBEAT = 0x23
CMD_CUSTOM = 0x72

CMD_NAMES = {
    CMD_CONNECT: "CONNECT",
    CMD_TRACKING: "Tracking",
    CMD_VERSION: "VERSION",
    CMD_HEARTBEAT: "HEARTBEAT",
    CMD_CUSTOM: "CUSTOM",
}

SOURCES = ("Head", "Controller", "Hand", "Body", "Motion")


def read_frames(sock, buffer):
    """Yield (cmd, payload, device_ts_ms) tuples from a blocking socket."""
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
            # PackageHandle.Pack / long2Bytes + BlockCopy: little-endian int32 length.
            length = struct.unpack("<i", bytes(buffer[2:6]))[0]
            if length < 0 or length > 4 * 1024 * 1024:
                del buffer[:1]
                continue
            total = 15 + length
            if len(buffer) < total:
                break
            payload = bytes(buffer[6:6 + length])
            ts_ms = struct.unpack("<q", bytes(buffer[6 + length:14 + length]))[0]
            end = buffer[total - 1]
            if end != END:
                del buffer[:1]
                continue
            del buffer[:total]
            yield cmd, payload, ts_ms


def decompress_value(value):
    """The Tracking payload carries its value as an escaped JSON string."""
    if isinstance(value, str):
        try:
            return json.loads(value)
        except ValueError:
            return None
    return value


def pct(sorted_vals, p):
    if not sorted_vals:
        return 0.0
    k = (len(sorted_vals) - 1) * p / 100.0
    lo, hi = int(k), min(int(k) + 1, len(sorted_vals) - 1)
    return sorted_vals[lo] + (sorted_vals[hi] - sorted_vals[lo]) * (k - lo)


def stddev(vals):
    n = len(vals)
    if n < 2:
        return 0.0
    mean = sum(vals) / n
    return math.sqrt(sum((v - mean) ** 2 for v in vals) / (n - 1))


def main():
    ap = argparse.ArgumentParser(
        description="PC-side Tracking probe. Default live output ≈ ros2 topic hz.")
    ap.add_argument("--bind", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=63901)
    ap.add_argument("--duration", type=float, default=0.0,
                    help="seconds, 0 = until Ctrl-C")
    ap.add_argument("--window", type=float, default=1.0,
                    help="seconds between live hz reports (default 1.0)")
    ap.add_argument("--csv", default="", help="write per-window rows to this csv")
    ap.add_argument("--quiet", action="store_true",
                    help="suppress live hz lines; still print final summary")
    args = ap.parse_args()
    if args.window <= 0:
        print("--window must be > 0", file=sys.stderr)
        return 2

    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        srv.bind((args.bind, args.port))
    except OSError as err:
        print(f"cannot bind {args.bind}:{args.port}: {err.strerror}", file=sys.stderr)
        if err.errno == 98:
            print("  port 63901 is usually the running PC service; stop it, or use\n"
                  "  --port 63902 together with Docs/tools/fake_headset.py to test\n"
                  "  locally. Check the owner with: ss -lntp | rg 63901", file=sys.stderr)
        return 1
    srv.listen(2)
    if not args.quiet:
        print(f"listening on {args.bind}:{args.port} (waiting for headset)...", flush=True)
        print("  REAL DEVICE: panel CollectionRate ≈ 'average rate' when Head(+Send) on.",
              flush=True)
        print("  SIMULATION:  pair with fake_headset.py; panel Hz is ignored.",
              flush=True)
    srv.settimeout(1.0)
    conn = None
    started = time.time()
    while conn is None:
        try:
            conn, addr = srv.accept()
            if not args.quiet:
                print(f"headset connected from {addr[0]}:{addr[1]}", flush=True)
                print("--- send Hz (ros2 topic hz style; Ctrl-C to stop) ---", flush=True)
        except socket.timeout:
            if args.duration and time.time() - started > args.duration + 30:
                print("no client connected, giving up")
                return 1
    conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    conn.settimeout(min(0.2, args.window))

    buffer = bytearray()
    rows = []

    last_sig = {s: None for s in SOURCES}
    last_change_ts = {s: None for s in SOURCES}
    change_intervals = {s: collections.deque(maxlen=20000) for s in SOURCES}
    stale_at_packet = {s: collections.deque(maxlen=20000) for s in SOURCES}
    inter_packet = collections.deque(maxlen=20000)
    prev_packet_t = None
    offset = None
    latencies = collections.deque(maxlen=20000)
    total_packets = 0
    total_bytes = 0
    version = None
    prev_inner_ts = None
    dup_inner_ts = 0
    inner_ts_values = 0
    first_pkt_t = None
    last_pkt_t = None

    # Sliding arrival times for ros2-like rolling window across the whole run.
    arrival_times = collections.deque()
    # Per-report window counters.
    win_start = time.time()
    win_packets = 0
    win_bytes = 0
    win_gaps = []
    win_present = {s: 0 for s in SOURCES}
    win_changes = {s: 0 for s in SOURCES}
    win_bytes_by = {s: 0 for s in SOURCES}
    win_resent = {s: 0 for s in SOURCES}
    win_dup_ts = 0
    win_other = collections.Counter()
    seen_cmds = set()
    idle_hint_printed = False

    def reset_window(now):
        nonlocal win_start, win_packets, win_bytes, win_gaps, win_dup_ts
        win_start = now
        win_packets = 0
        win_bytes = 0
        win_gaps = []
        win_dup_ts = 0
        win_other.clear()
        for s in SOURCES:
            win_present[s] = 0
            win_changes[s] = 0
            win_bytes_by[s] = 0
            win_resent[s] = 0

    def emit_hz(now):
        """Print one ros2-topic-hz-like block for the just-finished window."""
        nonlocal idle_hint_printed
        elapsed = max(1e-6, now - win_start)
        row = {
            "t": int(now),
            "packets": win_packets,
            "bytes": win_bytes,
            "bytes_by": dict(win_bytes_by),
            "redundant_bytes": dict(win_resent),
            "dup_ts": win_dup_ts,
            "present": dict(win_present),
            "changes": dict(win_changes),
        }
        # Only keep summary rows that actually carried Tracking.
        if win_packets:
            rows.append(row)

        if args.quiet:
            return

        # Rolling window: keep ~2s of arrivals for the "average rate" line so it
        # stays stable like ros2 topic hz, while still refreshing every --window.
        while arrival_times and now - arrival_times[0] > max(2.0, args.window * 2):
            arrival_times.popleft()
        if len(arrival_times) >= 2:
            span = arrival_times[-1] - arrival_times[0]
            avg_rate = (len(arrival_times) - 1) / max(1e-6, span)
            gaps_s = [arrival_times[i] - arrival_times[i - 1]
                      for i in range(1, len(arrival_times))]
            print(f"average rate: {avg_rate:.3f}", flush=True)
            print(f"\tmin: {min(gaps_s):.5f}s max: {max(gaps_s):.5f}s "
                  f"std dev: {stddev(gaps_s):.5f}s window: {len(arrival_times)}",
                  flush=True)
        elif win_packets:
            print(f"average rate: {win_packets / elapsed:.3f}", flush=True)
            print(f"\twindow: {win_packets} (warming up)", flush=True)
        else:
            other = " ".join(f"{CMD_NAMES.get(c, hex(c))}={n}"
                             for c, n in sorted(win_other.items())) or "none"
            print("average rate: 0.000", flush=True)
            print(f"\t(no Tracking this window; other cmds: {other})", flush=True)
            if not idle_hint_printed:
                idle_hint_printed = True
                print("\thint: on Pico enable Send + at least one of "
                      "Head/Controller/Hand/Tracker (Enterprise source).",
                      flush=True)

        src_bits = []
        for s in SOURCES:
            if win_present[s]:
                # present/s = how often the key rode a packet; changes/s = new samples
                src_bits.append(
                    f"{s}:{win_changes[s] / elapsed:.1f}chg"
                    f"/{win_present[s] / elapsed:.1f}pkt")
        bw = win_bytes / elapsed / 1024.0
        carried = sum(win_bytes_by.values())
        resent = sum(win_resent.values())
        red = f"  redundant {100.0 * resent / carried:.1f}%" if carried else ""
        extra = ("  " + "  ".join(src_bits)) if src_bits else ""
        print(f"\tsend {win_packets / elapsed:.1f}Hz  {bw:.1f} KiB/s{red}{extra}",
              flush=True)

    start = time.time()
    reset_window(start)
    try:
        while True:
            now = time.time()
            if now - win_start >= args.window:
                emit_hz(now)
                reset_window(now)

            try:
                for cmd, payload, dev_ms in read_frames(conn, buffer):
                    now = time.time()
                    if now - win_start >= args.window:
                        emit_hz(now)
                        reset_window(now)

                    if cmd not in seen_cmds:
                        seen_cmds.add(cmd)
                        if not args.quiet and cmd != CMD_TRACKING:
                            preview = payload[:64].decode("utf-8", "replace")
                            print(f"device cmd {CMD_NAMES.get(cmd, hex(cmd))} "
                                  f"({len(payload)}B): {preview}", flush=True)

                    if cmd == CMD_VERSION:
                        if version is None:
                            version = payload.decode("utf-8", "replace")
                        win_other[cmd] += 1
                        continue
                    if cmd == CMD_HEARTBEAT or cmd == CMD_CUSTOM or cmd == CMD_CONNECT:
                        win_other[cmd] += 1
                        continue
                    if cmd != CMD_TRACKING:
                        win_other[cmd] += 1
                        continue

                    total_packets += 1
                    total_bytes += len(payload)
                    if first_pkt_t is None:
                        first_pkt_t = now
                    last_pkt_t = now
                    win_packets += 1
                    win_bytes += len(payload)
                    arrival_times.append(now)

                    if prev_packet_t is not None:
                        gap_ms = (now - prev_packet_t) * 1000.0
                        inter_packet.append(gap_ms)
                        win_gaps.append(gap_ms)
                    prev_packet_t = now

                    if dev_ms > 0:
                        d = now * 1000.0 - dev_ms
                        if offset is None or d < offset:
                            offset = d
                        latencies.append(d - offset)

                    try:
                        outer = json.loads(payload.decode("utf-8"))
                    except ValueError:
                        continue
                    if outer.get("functionName") != "Tracking":
                        continue
                    value = decompress_value(outer.get("value"))
                    if not isinstance(value, dict):
                        continue

                    inner_ts = value.get("timeStampNs")
                    if inner_ts is not None:
                        inner_ts_values += 1
                        if inner_ts == prev_inner_ts:
                            dup_inner_ts += 1
                            win_dup_ts += 1
                        prev_inner_ts = inner_ts

                    for s in SOURCES:
                        if s not in value:
                            continue
                        win_present[s] += 1
                        ser = json.dumps(value[s], sort_keys=True)
                        win_bytes_by[s] += len(ser)
                        sig = hashlib.blake2b(ser.encode(), digest_size=8).digest()
                        if sig != last_sig[s]:
                            last_sig[s] = sig
                            win_changes[s] += 1
                            if last_change_ts[s] is not None:
                                change_intervals[s].append((now - last_change_ts[s]) * 1000.0)
                            last_change_ts[s] = now
                        elif last_change_ts[s] is not None:
                            stale_at_packet[s].append((now - last_change_ts[s]) * 1000.0)
                            win_resent[s] += len(ser)
            except socket.timeout:
                pass

            if args.duration and time.time() - start >= args.duration:
                break
    except KeyboardInterrupt:
        pass
    finally:
        now = time.time()
        if win_packets:
            emit_hz(now)
        conn.close()
        srv.close()

    print("\n=== summary ===")
    if not rows or total_packets == 0:
        print("no tracking packets received")
        return 1
    dur = max(1e-6, (last_pkt_t - first_pkt_t)) if first_pkt_t is not None else 1.0
    print(f"duration {dur:.1f}s  packets {total_packets}  "
          f"avg {total_packets / dur:.1f}/s  {total_bytes / dur / 1024:.1f} KiB/s")
    for s in SOURCES:
        present = sum(r["present"][s] for r in rows)
        changes = sum(r["changes"][s] for r in rows)
        if not present:
            continue
        iv = sorted(change_intervals[s]) or [0]
        st = sorted(stale_at_packet[s]) or [0]
        sb = sum(r["bytes_by"][s] for r in rows) / dur / 1024.0
        rb = sum(r["redundant_bytes"][s] for r in rows) / dur / 1024.0
        print(f"  {s:<10} present {present / dur:6.1f}/s  changes {changes / dur:6.1f}/s"
              f"  interval p50 {pct(iv, 50):5.1f}ms p99 {pct(iv, 99):6.1f}ms max {iv[-1]:6.1f}ms")
        print(f"  {'':<10} bytes {sb:7.1f}KiB/s  resent {rb:7.1f}KiB/s"
              f"  stale-at-send p50 {pct(st, 50):5.1f}ms p99 {pct(st, 99):6.1f}ms")
    iv = sorted(inter_packet) or [0]
    print(f"  packet gap p50 {pct(iv, 50):5.2f}ms p99 {pct(iv, 99):6.2f}ms max {iv[-1]:6.2f}ms"
          f" (= {1000.0 / max(0.001, pct(iv, 50)):.0f}Hz median)")
    if latencies:
        l = sorted(latencies)
        print(f"  one-way delay p50 {pct(l, 50):5.1f}ms p99 {pct(l, 99):6.1f}ms max {l[-1]:6.1f}ms"
              f" (clock-offset normalised; rising values = queueing)")
    resent = sum(sum(r["redundant_bytes"].values()) for r in rows)
    carried = sum(sum(r["bytes_by"].values()) for r in rows)
    if carried:
        print(f"  payload {carried / dur / 1024:.1f}KiB/s of which resent {resent / dur / 1024:.1f}KiB/s"
              f" ({100.0 * resent / carried:.1f}% redundant)")
    if inner_ts_values:
        print(f"  timeStampNs repeats {dup_inner_ts / dur:.1f}/s of {inner_ts_values / dur:.1f}/s packets"
              f" ({100.0 * dup_inner_ts / inner_ts_values:.1f}% non-advancing)")

    if args.csv:
        with open(args.csv, "w", encoding="utf-8") as fh:
            fh.write("sec,packets,bytes," + ",".join(f"{s}_present" for s in SOURCES) + ","
                     + ",".join(f"{s}_changes" for s in SOURCES) + ","
                     + ",".join(f"{s}_bytes" for s in SOURCES) + ","
                     + ",".join(f"{s}_resent" for s in SOURCES) + ",dup_ts\n")
            for r in rows:
                fh.write(f"{r['t']},{r['packets']},{r['bytes']},"
                         + ",".join(str(r["present"][s]) for s in SOURCES) + ","
                         + ",".join(str(r["changes"][s]) for s in SOURCES) + ","
                         + ",".join(str(r["bytes_by"][s]) for s in SOURCES) + ","
                         + ",".join(str(r["redundant_bytes"][s]) for s in SOURCES) + ","
                         + f"{r['dup_ts']}\n")
        print(f"per-window rows -> {args.csv}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
