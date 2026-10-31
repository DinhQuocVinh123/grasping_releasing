#!/usr/bin/env python3
"""
Watch GloveForceEsp32Output's own log lines from the Quest, exactly as Unity prints them,
and save every line to a CSV.

    Quest: GloveForceEsp32Output --"GLOVEF ..." lines in logcat--> adb (USB) --> this script

GloveForceEsp32Output prints one GLOVEF line every 1/_logcatHz seconds (Inspector, default
5 per second). This script prints each one unchanged, prefixed with the Quest's timestamp,
and writes its fields to a CSV. Nothing is derived or added.

Fields (as printed by GloveForceEsp32Output.LogLive):
    gate    tracking gate 0..1 that scales all forces
    w, fit  ImageHandSolver weight and fit quality (only when the solver is present)
    TH, IX  thumb, index. Per finger:
      touch   1 = touching an object
      d       skin depth into the object, mm (negative = outside, distance to the surface)
      F       force magnitude, N (after gate and limits)
      P       pad pressing force, N (after _padForceGain)
      T       dorsal extension torque, N*m (after _dorsalGain) -- the value the Arduino uses
      face    how squarely the force hits the pad, -1..1
    sent    packets sent so far

Values are rounded as in the log line (F2, T F4). For every packet at full precision, turn on
_logCsv in the Inspector instead (writes glove_esp32_cmd_*.csv on the Quest).

Needs the Quest on USB (`adb devices` lists it) and the app running. Only reads logcat, so it
runs fine next to glove_serial_bridge.py.

    python tools/analysis/watch_glove_force.py
    python tools/analysis/watch_glove_force.py --csv my_test.csv
"""
import argparse
import csv
import os
import queue
import re
import shutil
import subprocess
import sys
import threading
import time

ADB_CANDIDATES = [
    r"C:\Program Files\Meta Quest Developer Hub\resources\bin\adb.exe",
    r"D:\Unity\Editors\6000.4.2f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe",
]

# 10-13 10:40:55.593 18787 18821 I Unity   : GLOVEF gate 1.00 w 1.00 fit 1.00 | TH touch 1 d 5.9mm ... | IX ... | sent 54866
LINE = re.compile(r"^(?P<date>\d\d-\d\d) (?P<time>[\d:.]+) .*?GLOVEF (?P<body>.*)$")
BODY = re.compile(
    r"gate (?P<gate>[-\d.]+)(?: w (?P<w>[-\d.]+) fit (?P<fit>[-\d.]+))?"
    r" \| TH (?P<th>.*?) \| IX (?P<ix>.*?) \| sent (?P<sent>\d+)")
FINGER = re.compile(
    r"touch (?P<touch>[01]) d (?P<d>--|[-\d.]+)mm F (?P<F>[-\d.]+) P (?P<P>[-\d.]+)"
    r" T (?P<T>[-\d.]+) face (?P<face>[-\d.]+)")
FINGER_COLS = ("touch", "d_mm", "F_N", "P_N", "T_Nm", "face")
CSV_COLS = ["pc_time", "quest_time", "gate", "w", "fit"] + [
    "%s_%s" % (f, c) for f in ("th", "ix") for c in FINGER_COLS] + ["sent"]


def find_adb(given):
    for cand in [given, shutil.which("adb")] + ADB_CANDIDATES:
        if cand and os.path.isfile(cand):
            return cand
    return None


def csv_row(quest_time, body):
    """The GLOVEF body's fields as text, unchanged; None if the line has another format."""
    m = BODY.search(body)
    if not m:
        return None
    th, ix = FINGER.search(m["th"]), FINGER.search(m["ix"])
    if not th or not ix:
        return None
    row = [time.strftime("%H:%M:%S"), quest_time, m["gate"], m["w"] or "", m["fit"] or ""]
    for f in (th, ix):
        row += [f["touch"], "" if f["d"] == "--" else f["d"], f["F"], f["P"], f["T"], f["face"]]
    return row + [m["sent"]]


def reader(proc, q):
    for raw in proc.stdout:
        q.put(raw.decode("utf-8", "replace").rstrip())
    q.put(None)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--adb", help="path to adb.exe (default: PATH, then Meta Quest Developer Hub / Unity SDK)")
    ap.add_argument("--serial", help="device serial, if more than one is connected")
    ap.add_argument("--csv", help="CSV file to write (default: glove_force_<date>_<time>.csv)")
    ap.add_argument("--seconds", type=float, default=0, help=argparse.SUPPRESS)
    args = ap.parse_args()

    adb = find_adb(args.adb)
    if not adb:
        sys.exit("adb.exe not found -- install Meta Quest Developer Hub or pass --adb <path>")
    cmd = [adb] + (["-s", args.serial] if args.serial else []) + [
        "logcat", "-v", "threadtime", "-T", "1", "-s", "Unity:I"]

    path = args.csv or time.strftime("glove_force_%Y%m%d_%H%M%S.csv")
    out = open(path, "w", newline="", encoding="utf-8")
    writer = csv.writer(out)
    writer.writerow(CSV_COLS)

    try:
        proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    except OSError as e:
        sys.exit("could not start adb (%s)" % e)
    q = queue.Queue()
    threading.Thread(target=reader, args=(proc, q), daemon=True).start()

    print("reading GLOVEF lines from the Quest via adb, saving to %s (Ctrl+C to stop)" % path)

    t0 = last_line = time.time()
    warned = False
    rows = 0
    try:
        while True:
            if args.seconds and time.time() - t0 > args.seconds:
                break
            try:
                line = q.get(timeout=0.5)
            except queue.Empty:
                if not warned and time.time() - last_line > 3:
                    print("... no new GLOVEF line -- is the Quest on USB and the app running?")
                    warned = True
                continue
            if line is None:
                print("adb stopped -- Quest unplugged or adb error")
                break
            m = LINE.search(line)
            if not m:
                if line.startswith(("error", "adb:", "- waiting")):
                    print(line)
                continue
            last_line, warned = time.time(), False
            print("%s  %s" % (m["time"], m["body"]))
            row = csv_row(m["time"], m["body"])
            if row:
                writer.writerow(row)
                out.flush()
                rows += 1
    except KeyboardInterrupt:
        pass
    finally:
        proc.terminate()
        out.close()
        print("\n%d lines saved to %s" % (rows, path))


if __name__ == "__main__":
    main()
