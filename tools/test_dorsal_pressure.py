#!/usr/bin/env python3
"""Checks for tools/dorsal_pressure.py.   python tools/test_dorsal_pressure.py"""
import math
import random
import sys

from dorsal_pressure import DorsalPressure, load_model, PRESSURIZATION, RELEASE

fails = []


def check(cond, msg):
    print(("ok    " if cond else "FAIL  ") + msg)
    if not cond:
        fails.append(msg)


dp = DorsalPressure(load_model())
m = dp.model

# 1. Inverse really inverts the forward model, everywhere the force is reachable.
random.seed(1)
worst = 0.0
for _ in range(500):
    L = random.uniform(55, 80)
    b = random.choice([RELEASE, PRESSURIZATION])
    F = random.uniform(0.03, 2.0)
    fmax, fmin = m.predict_force(0.0, L, b), m.predict_force(dp.rest_bar, L, b)
    if fmin + 0.01 < F < fmax - 0.01:
        P = dp.pressure_for(F, L, b)
        worst = max(worst, abs(m.predict_force(P, L, b) - F))
check(worst < 0.005, "force(pressure_for(F)) == F within 5 mN (worst %.4f N)" % worst)

# 2. Saturation at both ends.
check(dp.pressure_for(5.0, 70, RELEASE) == 0.0, "more than the actuator can make -> 0 bar (full force)")
check(dp.pressure_for(0.3, 80, RELEASE) == dp.rest_bar, "below the floor at L 80 (0.5 N) -> rest pressure")

# 3. Longer actuator, same force -> higher pressure (it pulls harder for the same pressure).
p65, p75 = dp.pressure_for(0.6, 65, RELEASE), dp.pressure_for(0.6, 75, RELEASE)
check(p75 > p65, "0.6 N: L 75 needs a higher pressure than L 65 (%.3f > %.3f bar)" % (p75, p65))

# 4. No force -> rest, and the branch resets to pressurization.
dp.reset()
check(dp.update(1, 0.0, 70) == (dp.rest_bar, PRESSURIZATION, 70.0), "zero force -> rest pressure")
check(dp.update(1, float("nan"), 70)[0] == dp.rest_bar, "NaN force -> rest pressure")

# 5. Branch follows the direction of the force; small jitter keeps it.
dp.reset()
_, b1, _ = dp.update(1, 0.8, 70)
_, b2, _ = dp.update(1, 0.802, 70)     # +2 mN: inside the 5 mN deadband
_, b3, _ = dp.update(1, 0.6, 70)
_, b4, _ = dp.update(1, 0.9, 70)
check(b1 == RELEASE, "force rising from rest -> release branch (pressure falling)")
check(b2 == RELEASE, "jitter below the deadband keeps the branch")
check(b3 == PRESSURIZATION, "force falling -> pressurization branch (pressure rising)")
check(b4 == RELEASE, "force rising again -> release branch")

# 6. Fingers are independent.
dp.reset()
dp.update(0, 1.0, 70)
dp.update(1, 0.5, 70)
dp.update(1, 0.3, 70)
check(dp._branch == [RELEASE, PRESSURIZATION], "each finger keeps its own branch")

# 7. Length outside the model's 55-80 mm is clamped, not an error.
check(dp.update(0, 0.2, 40)[2] == 55.0 and dp.update(0, 0.2, 120)[2] == 80.0 and dp.update(0, 0.2, math.inf)[2] == 55.0,
      "L clamped to 55-80 mm (and non-finite -> 55)")

print("\n%s" % ("ALL PASSED" if not fails else "%d FAILED" % len(fails)))
sys.exit(1 if fails else 0)
