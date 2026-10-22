#!/usr/bin/env python3
"""
PC relay between Unity (Quest, over Wi-Fi) and the Arduino Mega (over USB).

    Quest --UDP 5011--> this PC --USB serial--> Arduino Mega
    Quest <--UDP 5012-- this PC

This relay answers Unity's GloveForceEsp32Output exactly like the two Wi-Fi boards
would (it announces itself on UDP 5012 as both the thumb and the index board),
so Unity finds the PC by itself.

Dorsal actuators: Unity sends the dorsal torque T and the actuator length L per
finger. The relay turns T into the wanted actuator force (T / --dorsal-arm) and
runs the empirical actuator force model backwards (tools/dorsal_pressure.py,
tools/actuator_model) to get the actuator PRESSURE that makes that force at that
length. The Arduino turns pressure into stepper position.
Unity sends L unclamped; this relay clamps it to the range the model file was
calibrated for (model.length_domain_mm), so a refit model needs no Unity change.
The status line shows "92>80" when that clamp is active.

To the Arduino it sends one text line, RATE times per second (default 50):

    H,<seq>,<thP>,<thU>,<thV>,<thBar>,<ixP>,<ixU>,<ixV>,<ixBar>*<cs>

per finger: P pressing force on the pad (N), U/V tilt (N), Bar target pressure of
the dorsal actuator (bar; --rest-bar = no force, lower = more force);
cs = XOR of every character before '*', two hex digits. Parsed on the Arduino
by arduino/GloveSerialLink/GloveSerialLink.h. (The old torque line started with
'G'; an old sketch ignores 'H' lines and stays at rest instead of misreading them.)

Safety: if Unity has been silent for --timeout seconds (default 0.2), the relay
sends REST (no pad force, dorsal at rest pressure) -- the line keeps flowing.
If the relay itself dies, nothing arrives; the Arduino must then go to rest on
its own (GloveSerialLink.alive() turns false after 200 ms).

Everything the Arduino prints comes back to this console as "arduino> ...".

Usage
    pip install pyserial
    python glove_serial_bridge.py --list               # which COM port is the Mega?
    python glove_serial_bridge.py --port COM5
    python glove_serial_bridge.py --dry-run            # no Arduino: show the lines instead
Options: --baud 115200  --rate 50  --timeout 0.2  --log run.csv  --quiet
         --dorsal-arm 0.012  --rest-bar 1.0  --model-dir tools/actuator_model
"""
import argparse
import math
import socket
import struct
import sys
import time

CMD_PORT, TLM_PORT, PROTOCOL_VERSION = 5011, 5012, 3       # GloveEsp32Protocol.cs
CMD_FLOATS, TLM_FLOATS = 16, 16
FAULT_LINK_LOST = 1 << 4


# ------------------------------------------------------------------ helpers
def parse_unity(data):
    """Unity's 64-byte packet -> (seq, trust, [thumb(P,U,V,T,L), index(P,U,V,T,L)]) or None.
    T dorsal torque (N*m), L dorsal actuator length (mm)."""
    if len(data) != CMD_FLOATS * 4:
        return None
    f = struct.unpack("<%df" % CMD_FLOATS, data)
    if any(math.isnan(x) or math.isinf(x) for x in f) or round(f[15]) != PROTOCOL_VERSION or f[0] < 0:
        return None
    fingers = []
    for base in (3, 9):   # per finger: contact, P, U, V, T, L
        fingers.append((max(f[base + 1], 0.0), f[base + 2], f[base + 3], max(f[base + 4], 0.0), f[base + 5]))
    return int(f[0]), min(max(f[2], 0.0), 1.0), fingers


def make_line(seq, pads, bars):
    """pads: [(P, U, V)] per finger; bars: dorsal target pressure per finger."""
    body = "H,%d" % seq
    for (p, u, v), bar in zip(pads, bars):
        body += ",%.4f,%.4f,%.4f,%.4f" % (p, u, v, bar)
    cs = 0
    for ch in body:
        cs ^= ord(ch)
    return "%s*%02X\n" % (body, cs)


ZERO = [(0.0, 0.0, 0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 0.0, 0.0)]   # Unity silent: no force at all


def dorsal_targets(dp, fingers, arm_m):
    """Per finger: (wanted force N, length used mm, target bar, branch)."""
    out = []
    for i, (_, _, _, torque, length) in enumerate(fingers):
        force = torque / arm_m if arm_m > 0 else 0.0
        bar, branch, used = dp.update(i, force, length)
        out.append((force, used, bar, branch))
    return out


def fmt_length(sent, used):
    """"80", or "92>80" when the model's range clamped what Unity sent."""
    if isinstance(sent, float) and math.isfinite(sent) and abs(sent - used) < 0.5:
        return "%.0f" % used
    return "%s>%.0f" % (("%.0f" % sent) if isinstance(sent, float) and math.isfinite(sent) else "?", used)


def open_serial(port, baud, reset_wait):
    import serial   # pyserial
    s = serial.Serial()
    s.port, s.baudrate, s.timeout, s.write_timeout = port, baud, 0, 0.05
    # Opening a USB port normally pulses DTR, which RESETS the Mega. Keep it low.
    # (Some drivers pulse it anyway; the wait below covers the bootloader.)
    s.dtr = False
    s.rts = False
    s.open()
    time.sleep(reset_wait)
    s.reset_input_buffer()
    return s


# --------------------------------------------------------------------- main
def run(args):
    from dorsal_pressure import DorsalPressure, load_model
    dp = DorsalPressure(load_model(args.model_dir), rest_bar=args.rest_bar)
    print("force model %s: L %g-%g mm, rest %.2f bar, dorsal arm %.4f m" % (
        dp.model.model_id, dp.l_min, dp.l_max, dp.rest_bar, args.dorsal_arm))
    udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    udp.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    udp.bind((args.bind, CMD_PORT))
    udp.settimeout(0.002)

    ser, ser_err, next_open = None, "", 0.0
    if not args.dry_run:
        try:
            ser = open_serial(args.port, args.baud, args.reset_wait)
            print("serial: %s @ %d baud" % (args.port, args.baud))
        except Exception as e:   # keep running: Unity still sees the relay, and we retry
            ser_err = str(e)
            print("serial: cannot open %s (%s) -- retrying every second" % (args.port, e))
    else:
        print("dry run: no Arduino, lines are printed instead")
    print("listening for Unity on UDP %d, announcing on %d (Ctrl+C to stop)" % (CMD_PORT, TLM_PORT))

    log = open(args.log, "w") if args.log else None
    if log:
        log.write("t,seq,fresh,thP,thU,thV,thT,thL_sent_mm,thL_used_mm,thF_N,thBar,thBranch,"
                  "ixP,ixU,ixV,ixT,ixL_sent_mm,ixL_used_mm,ixF_N,ixBar,ixBranch\n")

    host, last_rx, arrival = None, -1e9, 0.0
    seq, trust, latest = 0, 0.0, ZERO
    packets = lines_sent = 0
    period = 1.0 / args.rate
    next_send = next_tlm = next_status = time.time()
    rx_text = b""
    t0 = time.time()
    dorsal = dorsal_targets(dp, ZERO, args.dorsal_arm)   # rest, until the first send

    try:
        while True:
            now = time.time()

            # ---- Unity -> relay: keep the newest valid packet --------------------------
            # (Unity sends each packet twice, once per "board" -- both are this PC.)
            while True:
                try:
                    data, addr = udp.recvfrom(256)
                except socket.timeout:
                    break
                except OSError:
                    break
                cmd = parse_unity(data)
                if cmd is None:
                    continue
                if cmd[0] != seq or now - last_rx > 0.5:
                    packets += 1
                seq, trust, latest = cmd
                host, last_rx, arrival = addr[0], now, now

            fresh = now - last_rx < args.timeout
            targets = latest if fresh else ZERO   # Unity silent -> say "no force", explicitly

            # ---- relay -> Arduino, fixed rate -----------------------------------------
            if now >= next_send:
                next_send += period
                if next_send < now:
                    next_send = now + period
                dorsal = dorsal_targets(dp, targets, args.dorsal_arm)
                line = make_line(seq, [t[:3] for t in targets], [d[2] for d in dorsal])
                if ser is not None:
                    try:
                        ser.write(line.encode("ascii"))
                        lines_sent += 1
                    except Exception as e:
                        print("\nserial: write failed (%s) -- reconnecting" % e)
                        try:
                            ser.close()
                        except Exception:
                            pass
                        ser, ser_err, next_open = None, str(e), now + 1.0
                elif args.dry_run:
                    lines_sent += 1
                    if not args.quiet and lines_sent % max(1, int(args.rate / 5)) == 0:
                        print("-> " + line.strip())
                if log:
                    cols = []
                    for t, (force, used, bar, branch) in zip(targets, dorsal):
                        cols += ["%.5f" % x for x in t[:4]] + ["%.2f" % t[4], "%.2f" % used, "%.4f" % force, "%.4f" % bar, branch]
                    log.write("%.4f,%d,%d,%s\n" % (now - t0, seq, int(fresh), ",".join(cols)))

            # ---- reconnect a lost port ---------------------------------------------------
            if ser is None and not args.dry_run and now >= next_open:
                next_open = now + 1.0
                try:
                    ser = open_serial(args.port, args.baud, args.reset_wait)
                    ser_err = ""
                    print("\nserial: reconnected to %s" % args.port)
                except Exception as e:
                    ser_err = str(e)

            # ---- Arduino -> console ----------------------------------------------------
            if ser is not None:
                try:
                    n = ser.in_waiting
                    if n:
                        rx_text += ser.read(n)
                        while b"\n" in rx_text:
                            ln, rx_text = rx_text.split(b"\n", 1)
                            txt = ln.decode("ascii", "replace").strip()
                            if txt and not args.quiet:
                                print("\narduino> " + txt)
                        if len(rx_text) > 4096:
                            rx_text = b""
                except Exception as e:
                    print("\nserial: read failed (%s) -- reconnecting" % e)
                    try:
                        ser.close()
                    except Exception:
                        pass
                    ser, ser_err, next_open = None, str(e), now + 1.0

            # ---- relay -> Unity: look like the two boards ------------------------------
            if host and now - last_rx > 3.0:
                host = None                      # Unity gone: go back to announcing
            if now >= next_tlm:
                next_tlm = now + (0.02 if host else 0.25)
                faults = 0 if (fresh and (ser is not None or args.dry_run)) else FAULT_LINK_LOST
                for finger in (0, 1):
                    p = targets[finger][0]
                    f = [float(seq), now - t0, float(finger), float(faults),
                         (now - arrival) if host else 0.0, trust] + [0.0] * 8 + [p, float(PROTOCOL_VERSION)]
                    try:
                        udp.sendto(struct.pack("<%df" % TLM_FLOATS, *f), (host or args.broadcast, TLM_PORT))
                    except OSError:
                        pass

            # ---- status line -----------------------------------------------------------
            if not args.quiet and not args.dry_run and now >= next_status:
                next_status = now + 0.5
                link = ("Unity %s" % host) if fresh else ("Unity silent -> zeros" if host or packets else "waiting for Unity")
                port = args.port if ser is not None else ("%s DOWN" % args.port)
                d = dorsal
                print("\r[%s | %s] seq %d  thumb P %.2f F %.2f L %s %.2fbar | index P %.2f F %.2f L %s %.2fbar%s | %d lines   " % (
                    link, port, seq, targets[0][0], d[0][0], fmt_length(targets[0][4], d[0][1]), d[0][2],
                    targets[1][0], d[1][0], fmt_length(targets[1][4], d[1][1]), d[1][2], " " + d[1][3][:3] if d[1][2] < dp.rest_bar else "",
                    lines_sent), end="", flush=True)

            if args.seconds and now - t0 > args.seconds:
                break
            time.sleep(0.001)
    except KeyboardInterrupt:
        pass
    finally:
        # Last word: no force, a few times, so the glove goes to rest right away.
        if ser is not None:
            try:
                rest = make_line(seq, [z[:3] for z in ZERO], [dp.rest_bar, dp.rest_bar])
                for _ in range(10):
                    ser.write(rest.encode("ascii"))
                    time.sleep(0.02)
                ser.close()
            except Exception:
                pass
        if log:
            log.close()
        print("\nrelay stopped: %d Unity packets, %d lines sent" % (packets, lines_sent))


def list_ports():
    from serial.tools import list_ports as lp
    ports = list(lp.comports())
    if not ports:
        print("no serial ports found -- is the Mega plugged in?")
    for p in ports:
        print("%-12s %s" % (p.device, p.description))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--port", help="Arduino serial port, e.g. COM5 or /dev/ttyACM0")
    ap.add_argument("--baud", type=int, default=115200, help="must match Serial.begin() in the sketch")
    ap.add_argument("--rate", type=float, default=50.0, help="lines per second to the Arduino")
    ap.add_argument("--timeout", type=float, default=0.2, help="Unity silent this long -> send zeros (s)")
    ap.add_argument("--reset-wait", type=float, default=2.0, help="wait after opening the port, in case the board reset (s)")
    ap.add_argument("--bind", default="0.0.0.0")
    ap.add_argument("--broadcast", default="255.255.255.255", help="where to announce before Unity talks")
    ap.add_argument("--log", help="CSV of every line sent")
    ap.add_argument("--dorsal-arm", type=float, default=0.012,
                    help="moment arm (m) turning Unity's dorsal torque into actuator force; must match _dorsalGain")
    ap.add_argument("--rest-bar", type=float, default=1.0,
                    help="dorsal pressure meaning 'no force' (bar); must match P_REF_BAR in the Arduino sketch")
    ap.add_argument("--model-dir", default=None, help="folder with actuator_force_model.py + coefficients (default tools/actuator_model)")
    ap.add_argument("--dry-run", action="store_true", help="no Arduino: print the lines")
    ap.add_argument("--list", action="store_true", help="list serial ports and exit")
    ap.add_argument("--quiet", action="store_true")
    ap.add_argument("--seconds", type=float, default=0, help=argparse.SUPPRESS)
    args = ap.parse_args()
    if args.list:
        return list_ports()
    if not args.port and not args.dry_run:
        ap.error("give --port (see --list), or --dry-run")
    run(args)


if __name__ == "__main__":
    main()
