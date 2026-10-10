#!/usr/bin/env python3
"""
Pretend to be Unity: send GloveForceEsp32Output's packet to the relay, 50 per second.

Two uses:
  1. Find out which side is broken. Run the relay in one window and this in another.
     If the relay's status line changes from "waiting for Unity" to "Unity 127.0.0.1",
     the relay is fine and the problem is on the Unity side.
  2. Drive the motors without Unity, with a fixed torque you choose.

  python fake_unity.py                         # index T 0.01236 N*m (1.03 N -> M2 52 steps back), thumb 0
  python fake_unity.py --index-t 0 --thumb-t 0.024     # thumb 2.0 N -> M3 100 steps back
  python fake_unity.py --pulse 2               # alternate force / zero every 2 s
  python fake_unity.py --host 192.168.1.23     # relay on another PC

Ctrl+C stops it; the relay then sends zeros by itself after 0.2 s.
"""
import argparse
import socket
import struct
import time

CMD_PORT, TLM_PORT, VERSION, CMD_FLOATS = 5011, 5012, 2, 14   # GloveEsp32Protocol.cs


def packet(seq, t, thumb_p, thumb_t, index_p, index_t):
    f = [0.0] * CMD_FLOATS
    f[0], f[1], f[2] = float(seq), t, 1.0                          # seq, time, trust
    f[3], f[4] = (1.0 if thumb_p > 0 else 0.0), thumb_p           # thumb contact, P (U, V = 0)
    f[7], f[8] = (1.0 if index_p > 0 else 0.0), index_p           # index contact, P
    f[11], f[12] = thumb_t, index_t                               # dorsal torques
    f[13] = float(VERSION)
    return struct.pack("<%df" % CMD_FLOATS, *f)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default="127.0.0.1", help="the relay's address")
    ap.add_argument("--thumb-t", type=float, default=0.0, help="thumb dorsal torque, N*m")
    ap.add_argument("--index-t", type=float, default=0.01236, help="index dorsal torque, N*m")
    ap.add_argument("--thumb-p", type=float, default=0.0, help="thumb pad force, N")
    ap.add_argument("--index-p", type=float, default=0.0, help="index pad force, N")
    ap.add_argument("--rate", type=float, default=50.0)
    ap.add_argument("--pulse", type=float, default=0.0, help="alternate force / zero every N seconds")
    args = ap.parse_args()

    tx = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    print("sending to %s:%d -- thumb T %.5f, index T %.5f%s (Ctrl+C to stop)" % (
        args.host, CMD_PORT, args.thumb_t, args.index_t,
        ", pulsing every %g s" % args.pulse if args.pulse > 0 else ""))
    t0 = time.time()
    seq = 1
    last_print = 0.0
    try:
        while True:
            t = time.time() - t0
            on = args.pulse <= 0 or int(t / args.pulse) % 2 == 0
            k = 1.0 if on else 0.0
            data = packet(seq, t, args.thumb_p * k, args.thumb_t * k, args.index_p * k, args.index_t * k)
            try:
                tx.sendto(data, (args.host, CMD_PORT))
            except OSError as e:
                print("send failed: %s" % e)
            if t - last_print >= 1.0:
                last_print = t
                print("seq %d  %s" % (seq, "force" if on else "zero"))
            seq += 1
            time.sleep(1.0 / args.rate)
    except KeyboardInterrupt:
        print("\nstopped")


if __name__ == "__main__":
    main()