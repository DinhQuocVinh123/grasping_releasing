#!/usr/bin/env python3
"""
Show exactly what Unity (the Quest build) sends to the PC relay, decoded field by field.

    Quest --UDP 5011--> this script (prints it; sends NOTHING to the Arduino)

Each packet is 56 bytes = 14 float32, little-endian (GloveEsp32Protocol.cs). Every
--every seconds this prints the newest packet: the 4 raw bytes of each number, its name
and value, and the text line glove_serial_bridge.py would write to the Arduino for it.

It announces itself on UDP 5012 the same way the relay does, so Unity finds it whether or
not the board IPs are typed into the inspector. Only one program can receive on 5011:
stop glove_serial_bridge.py first.

    python tools/show_unity_packets.py
    python tools/show_unity_packets.py --every 1      # slower
"""
import argparse
import socket
import struct
import time

CMD_PORT, TLM_PORT, VERSION, CMD_FLOATS, TLM_FLOATS = 5011, 5012, 2, 14, 16   # GloveEsp32Protocol.cs

FIELDS = [
    ("seq", "so thu tu goi"),
    ("unity_time (s)", "dong ho Unity"),
    ("trust 0..1", "do tin tracking"),
    ("thumb contact", "ngon cai cham? 0/1"),
    ("thumb P (N)", "ngon cai: luc an dem ngon"),
    ("thumb U (N)", "ngon cai: nghieng ngang"),
    ("thumb V (N)", "ngon cai: nghieng doc"),
    ("index contact", "ngon tro cham? 0/1"),
    ("index P (N)", "ngon tro: luc an dem ngon"),
    ("index U (N)", "ngon tro: nghieng ngang"),
    ("index V (N)", "ngon tro: nghieng doc"),
    ("thumb T (N*m)", "ngon cai: mo-men duoi (mu ngon)"),
    ("index T (N*m)", "ngon tro: mo-men duoi (mu ngon)"),
    ("version", "phai = 2"),
]


def arduino_line(f):
    """The line glove_serial_bridge.py makes from this packet (same rules as its parse_unity + make_line)."""
    body = "G,%d" % int(f[0])
    for base, tq in ((3, 11), (7, 12)):
        body += ",%.4f,%.4f,%.4f,%.5f" % (max(f[base + 1], 0.0), f[base + 2], f[base + 3], max(f[tq], 0.0))
    cs = 0
    for ch in body:
        cs ^= ord(ch)
    return "%s*%02X" % (body, cs)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--every", type=float, default=0.5, help="seconds between printouts")
    ap.add_argument("--seconds", type=float, default=0, help=argparse.SUPPRESS)
    args = ap.parse_args()

    udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    try:
        udp.bind(("0.0.0.0", CMD_PORT))
    except OSError as e:
        print("cannot listen on UDP %d (%s) -- is glove_serial_bridge.py still running? Stop it first." % (CMD_PORT, e))
        return
    udp.settimeout(0.05)
    print("listening for Unity on UDP %d (Ctrl+C to stop). Nothing is sent to the Arduino." % CMD_PORT)

    t0 = time.time()
    latest, sender, size, last_rx = None, None, 0, 0.0
    seqs, bad, rate = set(), 0, None
    next_print = next_tlm = rate_start = t0
    try:
        while not args.seconds or time.time() - t0 < args.seconds:
            try:
                data, addr = udp.recvfrom(256)
                size = len(data)
                if size == CMD_FLOATS * 4:
                    latest, sender, last_rx = data, addr[0], time.time()
                    seqs.add(struct.unpack_from("<f", data, 0)[0])   # Unity sends each packet twice (2 "boards")
                else:
                    bad += 1
            except socket.timeout:
                pass
            except OSError:
                pass
            now = time.time()

            # Look like the relay, so Unity's discovery finds this PC.
            if now >= next_tlm:
                next_tlm = now + (0.02 if sender else 0.25)
                for finger in (0, 1):
                    f = [0.0, now - t0, float(finger), 0.0, 0.0, 1.0] + [0.0] * 8 + [0.0, float(VERSION)]
                    try:
                        udp.sendto(struct.pack("<%df" % TLM_FLOATS, *f), (sender or "255.255.255.255", TLM_PORT))
                    except OSError:
                        pass

            if now - rate_start >= 1.0:
                rate, seqs, rate_start = len(seqs) / (now - rate_start), set(), now

            if now >= next_print:
                next_print = now + args.every
                if latest is None:
                    print("waiting for Unity ... (%d wrong-size packets)" % bad if bad else "waiting for Unity ...")
                    continue
                f = struct.unpack("<%df" % CMD_FLOATS, latest)
                print("\n=== from %s | %d bytes = %d numbers x 4 bytes (float32, little-endian) | %s packets/s ==="
                      % (sender, size, CMD_FLOATS, "(counting)" if rate is None else "%.0f" % rate))
                if now - last_rx > 1.0:
                    print("!! no new packet for %.1f s -- this is the LAST one received (headset asleep? app closed?)"
                          % (now - last_rx))
                print(" #  raw bytes     name             value       meaning")
                for i, (name, meaning) in enumerate(FIELDS):
                    raw = " ".join("%02X" % b for b in latest[4 * i:4 * i + 4])
                    v = f[i]
                    val = "%d" % v if i in (0, 3, 7, 13) else "%.4f" % v
                    print("%2d  %s   %-15s %10s   %s" % (i, raw, name, val, meaning))
                if round(f[13]) != VERSION:
                    print("!! version %g != %d: the relay would DROP this packet" % (f[13], VERSION))
                print("-> relay would send to Arduino: " + arduino_line(f))
    except KeyboardInterrupt:
        pass
    print("\nstopped")


if __name__ == "__main__":
    main()
