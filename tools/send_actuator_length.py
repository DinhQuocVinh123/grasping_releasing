#!/usr/bin/env python3
"""
Send dorsal actuator lengths to the relay, in place of Unity's naive estimate.

The relay (glove_serial_bridge.py) uses these when its length source is "external"
(--length-source external, or press L in the relay window). This script is a test sender
and shows the format another program -- e.g. the CV model -- should send:

    one UDP text packet to the relay PC, port 5013:   L,<thumb_mm>,<index_mm>
    e.g.  L,98.5,104.2      lengths in mm, measured actuator length (model range 95-110)
          L,nan,104.2       'nan' (or empty) = no value for that finger -> the relay uses Unity's L

  Send them continuously (10-50 per second): a finger with no new value for --length-timeout
  (default 0.5 s on the relay) falls back to Unity's L.

From Python, in the CV program:

    import socket
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.sendto(b"L,%.1f,%.1f" % (thumb_mm, index_mm), ("127.0.0.1", 5013))   # relay's IP if on another PC

Examples:
  python send_actuator_length.py --index 104                  # index 104 mm, thumb left to Unity
  python send_actuator_length.py --thumb 100 --index 100
  python send_actuator_length.py --index-sweep 95:110 --period 4    # index goes 95 -> 110 -> 95 every 4 s
  python send_actuator_length.py --index 104 --host 192.168.1.23    # relay on another PC
"""
import argparse
import socket
import time

LENGTH_PORT = 5013   # glove_serial_bridge.LENGTH_PORT


def packet(thumb, index):
    f = lambda v: "nan" if v is None else "%.2f" % v
    return ("L,%s,%s" % (f(thumb), f(index))).encode("ascii")


def sweep(spec, t, period):
    lo, hi = (float(x) for x in spec.split(":"))
    ph = (t / period) % 1.0
    return lo + (hi - lo) * (2 * ph if ph < 0.5 else 2 - 2 * ph)   # triangle lo -> hi -> lo


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default="127.0.0.1", help="the relay's address")
    ap.add_argument("--port", type=int, default=LENGTH_PORT)
    ap.add_argument("--thumb", type=float, default=None, help="thumb actuator length, mm (omit = leave to Unity)")
    ap.add_argument("--index", type=float, default=None, help="index actuator length, mm (omit = leave to Unity)")
    ap.add_argument("--thumb-sweep", default=None, metavar="LO:HI", help="sweep the thumb length, e.g. 95:110")
    ap.add_argument("--index-sweep", default=None, metavar="LO:HI", help="sweep the index length, e.g. 95:110")
    ap.add_argument("--period", type=float, default=4.0, help="sweep period, s")
    ap.add_argument("--rate", type=float, default=30.0, help="packets per second")
    ap.add_argument("--seconds", type=float, default=0, help="stop after this long (0 = until Ctrl+C)")
    args = ap.parse_args()

    tx = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    print("sending actuator lengths to %s:%d (Ctrl+C to stop)" % (args.host, args.port))
    t0 = last_print = time.time()
    try:
        while True:
            now = time.time()
            t = now - t0
            thumb = sweep(args.thumb_sweep, t, args.period) if args.thumb_sweep else args.thumb
            index = sweep(args.index_sweep, t, args.period) if args.index_sweep else args.index
            data = packet(thumb, index)
            try:
                tx.sendto(data, (args.host, args.port))
            except OSError as e:
                print("send failed: %s" % e)
            if now - last_print >= 1.0:
                last_print = now
                print(data.decode())
            if args.seconds and t > args.seconds:
                break
            time.sleep(1.0 / args.rate)
    except KeyboardInterrupt:
        pass
    print("stopped")


if __name__ == "__main__":
    main()
