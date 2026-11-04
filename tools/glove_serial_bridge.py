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
uses the P(F, L) model of the braided actuator (tools/dorsal_pressure.py,
tools/actuator_model/pfl_model.py) to get the actuator PRESSURE that makes that
force at that length. The Arduino turns pressure into stepper position.
L is clamped to the range the model was fitted on (95-110 mm). The status line
shows "92>95" when that clamp is active.

Where L comes from (--length-source, or press L in this window to switch live):
  unity     the L in Unity's packet: rest length + offset x tracked finger flexion
            (GloveForceEsp32Output, the naive estimate)
  external  "L,<thumb_mm>,<index_mm>" text packets on UDP --length-port (5013) from
            any other program, e.g. the CV model (tools/send_actuator_length.py shows
            the format and is a test sender). A finger whose external value is missing,
            "nan" or older than --length-timeout falls back to Unity's L.
The status line marks each L with u (Unity) or e (external).

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
    python glove_serial_bridge.py --port COM5 --length-source external   # L from the CV model
Options: --baud 115200  --rate 50  --timeout 0.2  --log run.csv  --quiet
         --dorsal-arm 0.012  --rest-bar 1.0  --model tools/actuator_model/pfl_coefficients.json
         --length-source unity|external  --length-port 5013  --length-timeout 0.5
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
LENGTH_PORT = 5013                                          # external actuator lengths (send_actuator_length.py)


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


def dorsal_targets(dp, fingers, arm_m, lengths=None):
    """Per finger: (wanted force N, length used mm, target bar, branch).
    lengths: the L to use per finger (default: the L in Unity's packet)."""
    out = []
    for i, (_, _, _, torque, length) in enumerate(fingers):
        force = torque / arm_m if arm_m > 0 else 0.0
        bar, branch, used = dp.update(i, force, length if lengths is None else lengths[i])
        out.append((force, used, bar, branch))
    return out


def parse_lengths(data):
    """'L,<thumb_mm>,<index_mm>' -> [thumb, index], None for a missing / nan / non-positive value.
    Returns None if the packet is not a length packet."""
    try:
        parts = data.decode("ascii").strip().split(",")
    except UnicodeDecodeError:
        return None
    if len(parts) != 3 or parts[0].strip().upper() != "L":
        return None
    out = []
    for p in parts[1:]:
        try:
            v = float(p)
        except ValueError:
            v = float("nan")
        out.append(v if math.isfinite(v) and v > 0 else None)
    return out


class ExternalLengths:
    """Actuator lengths from another program (e.g. the CV model) on UDP LENGTH_PORT."""

    def __init__(self, bind, port, timeout):
        self.port, self.timeout = port, timeout
        self.value, self.time = [None, None], [-1e9, -1e9]
        self.packets = 0
        self.error = ""
        self.sock = None
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            s.bind((bind, port))
            s.setblocking(False)
            self.sock = s
        except OSError as e:
            self.error = str(e)

    def poll(self, now):
        while self.sock is not None:
            try:
                data, _ = self.sock.recvfrom(256)
            except (BlockingIOError, OSError):
                break
            vals = parse_lengths(data)
            if vals is None:
                continue
            self.packets += 1
            for i, v in enumerate(vals):
                if v is not None:
                    self.value[i], self.time[i] = v, now

    def get(self, finger, now):
        """Fresh external length (mm) for this finger, or None."""
        if self.value[finger] is not None and now - self.time[finger] < self.timeout:
            return self.value[finger]
        return None

    def age(self, now):
        return now - max(self.time)


class KeyPoller:
    """Single key presses in this console window: Windows directly, elsewhere a key + Enter."""

    def __init__(self):
        self.mode = None
        try:
            import msvcrt   # noqa: F401
            self.mode = "win"
        except ImportError:
            if sys.stdin is not None and sys.stdin.isatty():
                self.mode = "tty"

    def keys(self):
        out = ""
        if self.mode == "win":
            import msvcrt
            while msvcrt.kbhit():
                out += msvcrt.getwch()
        elif self.mode == "tty":
            import select
            while select.select([sys.stdin], [], [], 0)[0]:
                line = sys.stdin.readline()
                if not line:
                    self.mode = None
                    break
                out += line
        return out.lower()


def choose_lengths(source, unity_l, ext, now):
    """Per finger: (length to use, 'u' or 'e'). External only when chosen AND fresh."""
    out = []
    for i in (0, 1):
        v = ext.get(i, now) if (source == "external" and ext is not None) else None
        out.append((v, "e") if v is not None else (unity_l[i], "u"))
    return out


def fmt_length(sent, used, src=""):
    """"95u", or "92>95u" when the model's range clamped the incoming length."""
    if isinstance(sent, float) and math.isfinite(sent) and abs(sent - used) < 0.5:
        return "%.0f%s" % (used, src)
    return "%s>%.0f%s" % (("%.0f" % sent) if isinstance(sent, float) and math.isfinite(sent) else "?", used, src)


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
    dp = DorsalPressure(load_model(args.model), rest_bar=args.rest_bar)
    print("force model %s: L %g-%g mm, rest %.2f bar, dorsal arm %.4f m" % (
        dp.model.model_id, dp.l_min, dp.l_max, dp.rest_bar, args.dorsal_arm))
    ext = ExternalLengths(args.length_bind, args.length_port, args.length_timeout)
    if ext.sock is None:
        print("external lengths: cannot listen on UDP %d (%s) -- Unity's L only" % (args.length_port, ext.error))
    source = args.length_source
    keys = KeyPoller()
    print("dorsal length L from %s%s" % (
        "Unity (naive flexion estimate)" if source == "unity" else "external program on UDP %d" % args.length_port,
        "  -- press L%s to switch" % ("" if keys.mode == "win" else " + Enter") if keys.mode else ""))
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
        log.write("t,seq,fresh,len_source,"
                  "thP,thU,thV,thT,thL_unity_mm,thL_ext_mm,thL_src,thL_used_mm,thF_N,thBar,thBranch,"
                  "ixP,ixU,ixV,ixT,ixL_unity_mm,ixL_ext_mm,ixL_src,ixL_used_mm,ixF_N,ixBar,ixBranch\n")

    host, last_rx, arrival = None, -1e9, 0.0
    seq, trust, latest = 0, 0.0, ZERO
    packets = lines_sent = 0
    period = 1.0 / args.rate
    next_send = next_tlm = next_status = time.time()
    rx_text = b""
    t0 = time.time()
    dorsal = dorsal_targets(dp, ZERO, args.dorsal_arm)   # rest, until the first send
    lens = choose_lengths(source, [0.0, 0.0], ext, t0)

    try:
        while True:
            now = time.time()

            # ---- keys and external lengths -------------------------------------------
            if "l" in keys.keys():
                source = "external" if source == "unity" else "unity"
                print("\ndorsal length L now from %s" % (
                    "Unity (naive flexion estimate)" if source == "unity" else "external program on UDP %d%s" % (
                        args.length_port, "" if ext.sock is not None else " -- NOT LISTENING, Unity's L is used")))
            ext.poll(now)

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
                lens = choose_lengths(source, [t[4] for t in targets], ext, now)
                dorsal = dorsal_targets(dp, targets, args.dorsal_arm, [l for l, _ in lens])
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
                    for i, (t, (force, used, bar, branch)) in enumerate(zip(targets, dorsal)):
                        e = ext.get(i, now)
                        cols += ["%.5f" % x for x in t[:4]] + [
                            "%.2f" % t[4], "%.2f" % e if e is not None else "", lens[i][1], "%.2f" % used,
                            "%.4f" % force, "%.4f" % bar, branch]
                    log.write("%.4f,%d,%d,%s,%s\n" % (now - t0, seq, int(fresh), source, ",".join(cols)))

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
                if source == "unity":
                    lsrc = "L: Unity"
                elif ext.packets == 0:
                    lsrc = "L: ext, none yet"
                else:
                    lsrc = "L: ext %.0fms" % (1000 * ext.age(now))
                print("\r[%s | %s | %s] seq %d  thumb P %.2f F %.2f L %s %.2fbar | index P %.2f F %.2f L %s %.2fbar%s | %d lines   " % (
                    link, port, lsrc, seq, targets[0][0], d[0][0], fmt_length(lens[0][0], d[0][1], lens[0][1]), d[0][2],
                    targets[1][0], d[1][0], fmt_length(lens[1][0], d[1][1], lens[1][1]), d[1][2],
                    " " + d[1][3][:3] if d[1][2] < dp.rest_bar else "", lines_sent), end="", flush=True)

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
    ap.add_argument("--model", default=None,
                    help="P(F, L) coefficient file (default tools/actuator_model/pfl_coefficients.json)")
    ap.add_argument("--length-source", choices=("unity", "external"), default="unity",
                    help="where the dorsal actuator length L comes from at start (press L to switch while running)")
    ap.add_argument("--length-port", type=int, default=LENGTH_PORT,
                    help="UDP port for 'L,<thumb_mm>,<index_mm>' packets from another program")
    ap.add_argument("--length-bind", default="0.0.0.0", help="address to listen on for external lengths")
    ap.add_argument("--length-timeout", type=float, default=0.5,
                    help="an external length older than this (s) falls back to Unity's L")
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
