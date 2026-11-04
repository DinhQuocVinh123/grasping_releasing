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
Part 3 feeds the relay actuator lengths from another program (send_actuator_length.py),
the --length-source external path, and checks the fallback to Unity's L when they stop.
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
# Unity's test pinch is far above what the actuator can make at L = 95 mm (straight fingers = rest length),
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
          "dorsal 14 N / 7.5 N wanted, beyond the actuator (max 1.6 N at L 95) -> both at 0 bar (got %.4f, %.4f)" % (r["ixBar"], r["thBar"]))
    check(abs(r["thP"] - 1.5) < 2e-3, "thumb P = %.4f (Unity: 1.5)" % r["thP"])
tail = rows[-40:]
check(all(r["thP"] == 0 and r["ixP"] == 0 and r["thBar"] == 1.0 and r["ixBar"] == 1.0 for r in tail),
      "after Unity stops, the Arduino keeps receiving rest (no pad force, dorsal 1.0 bar)")
seqs = [r["seq"] for r in rows]
check(all(b >= a for a, b in zip(seqs, seqs[1:])), "sequence numbers never go backwards")
text = open(os.path.join(OUT, "bridge.txt")).read()
check("arduino> alive" in text, "the Arduino's own prints reach the relay console")
check("serial: " in text and "DOWN" not in text.split("relay stopped")[0][-200:], "serial port stayed up")
check("force model pfl-braided" in text, "the relay loaded the P(F, L) actuator model")

# ---- 2. a force inside the actuator's range: fake Unity -> relay -> parser ----------
# 1.03 N wanted (T 0.01236 N*m / 0.012 m) at L 100 mm, force rising -> release branch.
sys.path.insert(0, TOOLS)
from dorsal_pressure import DorsalPressure, load_model, RELEASE   # noqa: E402
want = DorsalPressure(load_model()).pressure_for(0.01236 / 0.012, 100.0, RELEASE)
# Thumb: 1.0 N at L 120 mm, past the model's range. Unity sends L unclamped; the relay must clamp it
# to the model's longest length (110 mm) and use that.
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
fu = subprocess.Popen([sys.executable, os.path.join(TOOLS, "fake_unity.py"), "--index-t", "0.01236", "--index-l", "100",
                       "--thumb-t", "0.012", "--thumb-l", "120"],
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
    check(abs(got - want) < 2e-3 and 0.40 < got < 0.50,
          "1.03 N at L 100 -> %.4f bar (model, release branch: %.4f) -> sketch: %d steps back"
          % (got, want, round((1.0 - got) * 100)))
    got_th = mid[len(mid) // 2]["thBar"]
    check(abs(got_th - want_th) < 2e-3,
          "thumb 1.0 N with L 120 sent -> relay clamps to L %g -> %.4f bar (model at L %g: %.4f)"
          % (dp_check.l_max, got_th, dp_check.l_max, want_th))
check(rows2 and rows2[-1]["ixBar"] == 1.0, "back to rest after fake Unity stops")

# ---- 3. lengths from another program: --length-source external ------------------------
# Unity (fake) says L 100 for both; the "CV model" (send_actuator_length.py) says index 108 and
# nothing for the thumb. The index must use 108 while packets come, then fall back to 100 within
# --length-timeout (0.5 s) of the last one; the thumb stays on Unity's 100 throughout.
want_ext = dp_check.pressure_for(0.01236 / 0.012, 108.0, RELEASE)
want_uni = dp_check.pressure_for(0.01236 / 0.012, 100.0, RELEASE)
want_th3 = dp_check.pressure_for(0.012 / 0.012, 100.0, RELEASE)
master3, slave3 = pty.openpty()
tty.setraw(slave3)
fake_log3 = os.path.join(OUT, "arduino_received_3.csv")
relay_log3 = os.path.join(OUT, "relay_log_3.csv")
fake3 = subprocess.Popen([os.path.join(OUT, "test_link"), "fake", fake_log3], stdin=master3, stdout=master3)
bridge3_out = open(os.path.join(OUT, "bridge3.txt"), "w")
bridge3 = subprocess.Popen([sys.executable, os.path.join(TOOLS, "glove_serial_bridge.py"), "--port", os.ttyname(slave3),
                            "--reset-wait", "0.2", "--bind", "127.0.0.1", "--broadcast", "127.0.0.1", "--seconds", "4.5",
                            "--length-source", "external", "--length-bind", "127.0.0.1", "--log", relay_log3, "--quiet"],
                           stdout=bridge3_out, stderr=subprocess.STDOUT)
time.sleep(0.6)
fu3 = subprocess.Popen([sys.executable, os.path.join(TOOLS, "fake_unity.py"), "--index-t", "0.01236", "--index-l", "100",
                        "--thumb-t", "0.012", "--thumb-l", "100"], stdout=subprocess.DEVNULL)
time.sleep(0.2)
sender = subprocess.Popen([sys.executable, os.path.join(TOOLS, "send_actuator_length.py"), "--index", "108",
                           "--seconds", "1.5"], stdout=subprocess.DEVNULL)
sender.wait()
time.sleep(1.5)
fu3.terminate()
bridge3.wait()
time.sleep(0.3)
fake3.terminate()
bridge3_out.close()
log3 = list(csv.DictReader(open(relay_log3)))
ext_rows = [r for r in log3 if r["ixL_src"] == "e"]
after = [r for r in log3 if r["ixL_src"] == "u" and float(r["ixT"]) > 0 and log3.index(r) > (log3.index(ext_rows[-1]) if ext_rows else 0)]
check(log3 and all(r["len_source"] == "external" for r in log3), "relay ran with the external length source")
check(len(ext_rows) > 40, "%d lines used the external index length" % len(ext_rows))
if ext_rows:
    r = ext_rows[len(ext_rows) // 2]
    check(abs(float(r["ixL_used_mm"]) - 108.0) < 0.01 and abs(float(r["ixBar"]) - want_ext) < 2e-3,
          "index: external L 108 -> %s bar (model at L 108: %.4f; Unity's L 100 would give %.4f)"
          % (r["ixBar"], want_ext, want_uni))
    check(r["thL_src"] == "u" and abs(float(r["thBar"]) - want_th3) < 2e-3,
          "thumb: no external value -> Unity's L 100 -> %s bar (model: %.4f)" % (r["thBar"], want_th3))
check(len(after) > 20 and all(abs(float(r["ixL_used_mm"]) - 100.0) < 0.01 for r in after),
      "external lengths stopped -> index back on Unity's L 100 (%d lines)" % len(after))
rows3 = [{k: float(v) for k, v in r.items()} for r in csv.DictReader(open(fake_log3))]
check(any(abs(r["ixBar"] - want_ext) < 2e-3 for r in rows3) and any(abs(r["ixBar"] - want_uni) < 2e-3 for r in rows3),
      "the Arduino received both pressures (external L, then Unity's L)")

print("\n%s" % ("ALL PASSED" if not fails else "%d FAILED" % len(fails)))
sys.exit(1 if fails else 0)
