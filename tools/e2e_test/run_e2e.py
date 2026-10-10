#!/usr/bin/env python3
"""
End to end, on one Linux PC (needs g++, mono-mcs, pyserial):

  Unity script (GloveForceEsp32Output, compiled against Unity stand-ins)
      --UDP-->  glove_serial_bridge.py  --serial (a pty)-->  fake Arduino
                                                              (the real GloveSerialLink.h parser)

Checks that what the Arduino-side parser understood is what Unity computed,
that zeros follow when Unity stops, and that the Arduino's own prints reach
the relay console.
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
    check(abs(r["ixT"] - 0.1691) < 2e-4, "index torque = %.5f (Unity: 0.09 x 1.8794 = 0.16914)" % r["ixT"])
    check(abs(r["thP"] - 1.5) < 2e-3 and abs(r["thT"] - 0.09) < 2e-4,
          "thumb P = %.4f, torque = %.5f (Unity: 1.5, 0.09)" % (r["thP"], r["thT"]))
tail = rows[-40:]
check(all(r["thP"] == 0 and r["ixP"] == 0 and r["thT"] == 0 and r["ixT"] == 0 for r in tail),
      "after Unity stops, the Arduino keeps receiving zeros")
seqs = [r["seq"] for r in rows]
check(all(b >= a for a, b in zip(seqs, seqs[1:])), "sequence numbers never go backwards")
text = open(os.path.join(OUT, "bridge.txt")).read()
check("arduino> alive" in text, "the Arduino's own prints reach the relay console")
check("serial: " in text and "DOWN" not in text.split("relay stopped")[0][-200:], "serial port stayed up")

print("\n%s" % ("ALL PASSED" if not fails else "%d FAILED" % len(fails)))
sys.exit(1 if fails else 0)
