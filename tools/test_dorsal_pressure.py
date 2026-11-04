#!/usr/bin/env python3
"""Checks for tools/dorsal_pressure.py and the P(F, L) model.   python tools/test_dorsal_pressure.py"""
import math
import random
import sys

from dorsal_pressure import DorsalPressure, load_model, PRESSURIZATION, RELEASE, SIGN

fails = []


def check(cond, msg):
    print(("ok    " if cond else "FAIL  ") + msg)
    if not cond:
        fails.append(msg)


dp = DorsalPressure(load_model())
m = dp.model

# 1. The junior's worked example (image.png): L 100, F 1.394 N, pressurizing -> 0.400 bar.
p = dp.pressure_for(1.394, 100, PRESSURIZATION)
check(abs(p - 0.400) < 0.002, "worked example: L 100, F 1.394 N, s +1 -> %.4f bar (sheet 0.400)" % p)
check((dp.l_min, dp.l_max) == (95.0, 110.0), "model range L 95-110 mm")

# 2. The inverse inverts the forward model, above and below the knee.
random.seed(1)
worst = 0.0
for _ in range(2000):
    L = random.uniform(95, 110)
    b = random.choice([RELEASE, PRESSURIZATION])
    P = random.uniform(0.0, 1.0)
    F = m.force(P, L, SIGN[b])
    if F > 0.03 and P < dp.rest_bar:
        worst = max(worst, abs(dp.pressure_for(F, L, b) - P))
check(worst < 1e-9, "pressure_for(force(P)) == P (worst %.2e bar)" % worst)

# 3. Continuous through the knee, and pressure falls as the wanted force rises.
for b in (RELEASE, PRESSURIZATION):
    fk = m.knee_force(100, SIGN[b])
    jump = abs(dp.pressure_for(fk - 1e-9, 100, b) - dp.pressure_for(fk + 1e-9, 100, b))
    check(jump < 1e-6, "%s: no jump at the knee force %.3f N (%.1e bar)" % (b, fk, jump))
prev, mono = 2.0, True
for i in range(1, 300):
    q = dp.pressure_for(i * 0.01, 100, RELEASE)
    mono &= q <= prev + 1e-12
    prev = q
check(mono, "pressure never rises as the wanted force rises (L 100, 0.01-2.99 N)")

# 4. Saturation at both ends.
check(dp.pressure_for(5.0, 100, RELEASE) == 0.0, "more than the actuator can make -> 0 bar (full force)")
check(dp.pressure_for(0.05, 110, PRESSURIZATION) == dp.rest_bar,
      "L 110 needs > 1 bar for ~0 N -> held at rest %.1f bar" % dp.rest_bar)
check(dp.force(dp.rest_bar, 100, PRESSURIZATION) == 0.0, "no force at rest pressure for L 100 (no preload)")

# 5. Longer actuator, same force -> higher pressure (it pulls harder at the same pressure).
p95, p105 = dp.pressure_for(1.0, 95, RELEASE), dp.pressure_for(1.0, 105, RELEASE)
check(p105 > p95, "1.0 N: L 105 needs a higher pressure than L 95 (%.3f > %.3f bar)" % (p105, p95))

# 6. Hysteresis: pressurizing needs a slightly higher pressure than releasing, for the same force.
d = dp.pressure_for(1.0, 100, PRESSURIZATION) - dp.pressure_for(1.0, 100, RELEASE)
check(0.02 < d < 0.08, "hysteresis at 1.0 N, L 100: pressurize - release = %.3f bar" % d)

# 7. No force -> rest, and the branch resets to pressurization.
dp.reset()
check(dp.update(1, 0.0, 100) == (dp.rest_bar, PRESSURIZATION, 100.0), "zero force -> rest pressure")
check(dp.update(1, float("nan"), 100)[0] == dp.rest_bar, "NaN force -> rest pressure")

# 8. Branch follows the direction of the force; small jitter keeps it.
dp.reset()
_, b1, _ = dp.update(1, 0.8, 100)
_, b2, _ = dp.update(1, 0.802, 100)     # +2 mN: inside the 5 mN deadband
_, b3, _ = dp.update(1, 0.6, 100)
_, b4, _ = dp.update(1, 0.9, 100)
check(b1 == RELEASE, "force rising from rest -> release (pressure falling)")
check(b2 == RELEASE, "jitter below the deadband keeps the branch")
check(b3 == PRESSURIZATION, "force falling -> pressurization (pressure rising)")
check(b4 == RELEASE, "force rising again -> release")

# 9. Fingers are independent.
dp.reset()
dp.update(0, 1.0, 100)
dp.update(1, 0.5, 100)
dp.update(1, 0.3, 100)
check(dp._branch == [RELEASE, PRESSURIZATION], "each finger keeps its own branch")

# 10. Length outside the model's 95-110 mm is clamped, not an error.
check(dp.update(0, 0.2, 80)[2] == 95.0 and dp.update(0, 0.2, 130)[2] == 110.0 and dp.update(0, 0.2, math.inf)[2] == 95.0,
      "L clamped to 95-110 mm (and non-finite -> 95)")

print("\n%s" % ("ALL PASSED" if not fails else "%d FAILED" % len(fails)))
sys.exit(1 if fails else 0)
