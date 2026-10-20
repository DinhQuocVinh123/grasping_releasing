#!/usr/bin/env python3
"""
End to end, on one Linux PC (needs g++, mono-mcs, pyserial):

  Unity script (GloveForceEsp32Output, compiled against Unity stand-ins)
      --UDP-->  glove_serial_bridge.py  --serial (a pty)-->  fake Arduino
                                                              (the real GloveSerialLink.h parser)

Checks that what the Arduino-side parser understood is what Unity computed
(pad force; dorsal force turned into pressure by the actuator model), that rest
follows when Unity stops, and that the Arduino's own prints reach the relay console.
Part 2 drives the relay with fake_unity.py at a force inside the actuator's range.
"""
import csv
import os
import pty
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
TOOLS = os.path.dirname(HERE)
ROOT = os.path.dirname(TOOLS)
OUT = os.path.join(HERE, "out")
os.makedirs(OUT, exist_ok=True)


def sh(cmd):
    subprocess.run(cmd, shell=True, check=True, cwd=HERE)


fails = []


def check(cond, msg):
    print(("ok    " if cond else "FAIL  ") + msg)
    if not cond:
        fails.append(msg)


# ---- build -------------------------------------------------------------------------
sh("g++ -std=c++17 -O1 -I%s/arduino/GloveSerialLink %s/test_link.cpp -o %s/test_link" % (ROOT, TOOLS, OUT))
S = os.path.join(ROOT, "Assets", "Scripts")
if not os.path.isdir(S):  # older layout of the glove-link package
    S = os.path.join(ROOT, "unity", "Assets", "Scripts")
sh("mcs -langversion:latest -target:library -out:%s/glove.dll UnityStubs.cs ProjectStubs.cs %s/GloveEsp32Protocol.cs "
   "%s/GloveForceEsp32Output.cs" % (OUT, S, S))
sh("mcs -langversion:latest -nowarn:219 -out:%s/tests.exe -r:%s/glove.dll -r:System.Core Tests.cs" % (OUT, OUT))

# ---- wire it up --------------------------------------------------------------------
master, slave = pty.openpty()
import tty
tty.setraw(slave)
fake_log = os.path.join(OUT, "arduino_received.csv")
fake = subprocess.Popen([os.path.join(OUT, "test_link"), "fake", fake_log], stdin=master, stdout=master)
bridge_out = open(os.path.join(OUT, "bridge.txt"), "w")
bridge = subprocess.Popen([sys.executable, os.path.join(TOOLS, "glove_serial_bridge.py"), "--port", os.ttyname(slave),
                           "--reset-wait", "0.2", "--bind", "127.0.0.1", "--broadcast", "127.0.0.1", "--seconds", "5"],
                          stdout=bridge_out, stderr=subprocess.STDOUT)
time.sleep(0.8)
unity = subprocess.run(["mono", os.path.join(OUT, "tests.exe")], cwd=OUT, capture_output=True, text=True)
print(unity.stdout.strip())
bridge.wait()
time.sleep(0.3)
fake.terminate()
bridge_out.close()

# ---- what did the Arduino understand? ----------------------------------------------
# Unity's test pinch is far above what the actuator can make even at L = 80 mm (the model's longest),
# so both dorsal actuators must be at full force = 0 bar; rest = 1.0 bar.
rows = list(csv.DictReader(open(fake_log)))
rows = [{k: float(v) for k, v in r.items()} for r in rows]
check(unity.returncode == 0, "Unity-side checks pass")
check(len(rows) > 150, "Arduino parsed %d lines (~50/s for ~5 s)" % len(rows))
forced = [r for r in rows if r["ixP"] > 1.0]
check(len(forced) > 20, "%d lines carried the pinch force" % len(forced))
if forced:
    r = forced[len(forced) // 2]
    check(abs(r["ixP"] - 1.8794) < 2e-3, "index P = %.4f (Unity: 2 cos 20 = 1.8794)" % r["ixP"])
    check(abs(r["ixU"] - 0.6840) < 2e-3, "index tilt U = %.4f (Unity: 2 sin 20 = 0.6840)" % r["ixU"])
    check(r["ixBar"] == 0.0 and r["thBar"] == 0.0,
          "dorsal 14 N / 7.5 N wanted, beyond the actuator (max 1.9 N at L 80) -> both at 0 bar (got %.4f, %.4f)" % (r["ixBar"], r["thBar"]))
    check(abs(r["thP"] - 1.5) < 2e-3, "thumb P = %.4f (Unity: 1.5)" % r["thP"])
tail = rows[-40:]
check(all(r["thP"] == 0 and r["ixP"] == 0 and r["thBar"] == 1.0 and r["ixBar"] == 1.0 for r in tail),
      "after Unity stops, the Arduino keeps receiving rest (no pad force, dorsal 1.0 bar)")
seqs = [r["seq"] for r in rows]
check(all(b >= a for a, b in zip(seqs, seqs[1:])), "sequence numbers never go backwards")
text = open(os.path.join(OUT, "bridge.txt")).read()
check("arduino> alive" in text, "the Arduino's own prints reach the relay console")
check("serial: " in text and "DOWN" not in text.split("relay stopped")[0][-200:], "serial port stayed up")
check("force model actuator-all-six-v1" in text, "the relay loaded the actuator force model")

# ---- 2. a force inside the actuator's range: fake Unity -> relay -> parser ----------
# 1.03 N wanted (T 0.01236 N*m / 0.012 m) at L 70 mm, force rising -> release branch.
sys.path.insert(0, TOOLS)
from dorsal_pressure import DorsalPressure, load_model, RELEASE   # noqa: E402
want = DorsalPressure(load_model()).pressure_for(0.01236 / 0.012, 70.0, RELEASE)
# Thumb: 1.0 N at L 95 mm, past the model's range. Unity sends L unclamped; the relay must clamp it
# to the model's longest length (80 mm) and use that curve.
dp_check = DorsalPressure(load_model())
want_th = dp_check.pressure_for(0.012 / 0.012, dp_check.l_max, RELEASE)
master2, slave2 = pty.openpty()
tty.setraw(slave2)
fake_log2 = os.path.join(OUT, "arduino_received_2.csv")
fake2 = subprocess.Popen([os.path.join(OUT, "test_link"), "fake", fake_log2], stdin=master2, stdout=master2)
bridge2 = subprocess.Popen([sys.executable, os.path.join(TOOLS, "glove_serial_bridge.py"), "--port", os.ttyname(slave2),
                            "--reset-wait", "0.2", "--bind", "127.0.0.1", "--broadcast", "127.0.0.1", "--seconds", "3.5",
                            "--quiet"], stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
time.sleep(0.6)
fu = subprocess.Popen([sys.executable, os.path.join(TOOLS, "fake_unity.py"), "--index-t", "0.01236", "--index-l", "70",
                       "--thumb-t", "0.012", "--thumb-l", "95"],
                      stdout=subprocess.DEVNULL)
time.sleep(1.5)
fu.terminate()
bridge2.wait()
time.sleep(0.3)
fake2.terminate()
rows2 = [{k: float(v) for k, v in r.items()} for r in csv.DictReader(open(fake_log2))]
mid = [r for r in rows2 if 0.0 < r["ixBar"] < 1.0]
check(len(mid) > 30, "%d lines carried a mid-range index pressure" % len(mid))
if mid:
    got = mid[len(mid) // 2]["ixBar"]
    check(abs(got - want) < 2e-3 and 0.30 < got < 0.40,
          "1.03 N at L 70 -> %.4f bar (model, release branch: %.4f) -> sketch: %d steps back"
          % (got, want, round((1.0 - got) * 100)))
    got_th = mid[len(mid) // 2]["thBar"]
    check(abs(got_th - want_th) < 2e-3,
          "thumb 1.0 N with L 95 sent -> relay clamps to L %g -> %.4f bar (model at L %g: %.4f)"
          % (dp_check.l_max, got_th, dp_check.l_max, want_th))
check(rows2 and rows2[-1]["ixBar"] == 1.0, "back to rest after fake Unity stops")

print("\n%s" % ("ALL PASSED" if not fails else "%d FAILED" % len(fails)))
sys.exit(1 if fails else 0)
