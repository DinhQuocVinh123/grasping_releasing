#!/usr/bin/env python3
"""
Wanted dorsal force -> actuator pressure, by running the empirical force model backwards.

    force F (N) + actuator length L (mm) + direction  ->  target pressure P (bar)

The forward model (tools/actuator_model, from Scripts/ForceModelActuator) gives the force the
actuator makes at pressure P, length L, on the branch the pressure is moving along. Force FALLS
as pressure rises: full force near 0 bar, about none by ~0.5-0.9 bar depending on L. So:

  * no force wanted  -> REST pressure (rest_bar): the Arduino holds the reference position;
  * more force       -> lower pressure, read on the 'release' branch (pressure falling);
  * less force       -> higher pressure, read on the 'pressurization' branch (pressure rising);
  * more than the actuator can make at this L -> the lowest pressure (p_min_bar): its maximum.

Each finger keeps its own branch state. A change smaller than branch_deadband_n keeps the
previous branch, so tracking jitter does not flip between the two curves.

    python dorsal_pressure.py            # prints a small table, as a self-check
"""
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PRESSURIZATION, RELEASE = "pressurization", "release"


def load_model(model_dir=None):
    """Load ForceModel from tools/actuator_model (or model_dir)."""
    model_dir = model_dir or os.path.join(HERE, "actuator_model")
    if model_dir not in sys.path:
        sys.path.insert(0, model_dir)
    from actuator_force_model import ForceModel   # noqa: E402 (path set just above)
    return ForceModel(os.path.join(model_dir, "actuator_coefficients.json"))


class DorsalPressure:
    def __init__(self, model, rest_bar=1.0, p_min_bar=0.0, min_force_n=0.02,
                 branch_deadband_n=0.005, grid=41):
        self.model = model
        self.l_min, self.l_max = model.length_domain_mm
        self.rest_bar = rest_bar
        self.p_min = p_min_bar
        self.min_force = min_force_n
        self.deadband = branch_deadband_n
        self.grid = grid
        self._branch = [PRESSURIZATION, PRESSURIZATION]
        self._prev_force = [0.0, 0.0]

    def clamp_length(self, length_mm):
        if not (isinstance(length_mm, (int, float)) and math.isfinite(length_mm)):
            return self.l_min
        return min(max(float(length_mm), self.l_min), self.l_max)

    def force(self, pressure_bar, length_mm, branch):
        return self.model.predict_force(pressure_bar, self.clamp_length(length_mm), branch)

    def pressure_for(self, force_n, length_mm, branch):
        """Pressure (bar) at which the actuator makes force_n. Saturates at both ends."""
        L = self.clamp_length(length_mm)
        lo, hi = self.p_min, self.rest_bar
        f = lambda p: self.model.predict_force(p, L, branch)
        if force_n >= f(lo):
            return lo                    # more than this L can make: full force
        if force_n <= f(hi):
            return hi                    # less than the floor at rest pressure
        # Force falls with pressure. Coarse scan for the first grid point at or below the wanted
        # force (robust to tiny wiggles between curves), then bisect inside that cell.
        prev = lo
        for i in range(1, self.grid):
            p = lo + (hi - lo) * i / (self.grid - 1)
            if f(p) <= force_n:
                a, b = prev, p
                break
            prev = p
        else:
            return hi
        for _ in range(30):
            m = 0.5 * (a + b)
            if f(m) > force_n:
                a = m
            else:
                b = m
        return 0.5 * (a + b)

    def update(self, finger, force_n, length_mm):
        """One finger, one update. Returns (pressure_bar, branch, length_used_mm)."""
        L = self.clamp_length(length_mm)
        if not (isinstance(force_n, (int, float)) and math.isfinite(force_n)) or force_n <= self.min_force:
            self._prev_force[finger] = 0.0
            self._branch[finger] = PRESSURIZATION        # heading back up to rest pressure
            return self.rest_bar, PRESSURIZATION, L
        prev = self._prev_force[finger]
        if force_n > prev + self.deadband:
            self._branch[finger] = RELEASE               # more force = pressure falling
        elif force_n < prev - self.deadband:
            self._branch[finger] = PRESSURIZATION        # less force = pressure rising
        self._prev_force[finger] = force_n
        branch = self._branch[finger]
        return self.pressure_for(force_n, L, branch), branch, L

    def reset(self):
        self._branch = [PRESSURIZATION, PRESSURIZATION]
        self._prev_force = [0.0, 0.0]


if __name__ == "__main__":
    dp = DorsalPressure(load_model())
    print("model %s, L %g-%g mm, rest %.2f bar" % (dp.model.model_id, dp.l_min, dp.l_max, dp.rest_bar))
    print("F (N) ->  pressure (bar) on the release branch, per L")
    print("        " + "".join("L=%-7g" % L for L in (55, 60, 65, 70, 75, 80)))
    for F in (0.1, 0.3, 0.6, 1.0, 1.3, 1.6, 2.0):
        print("F=%-5.2f " % F + "".join("%-9.3f" % dp.pressure_for(F, L, RELEASE) for L in (55, 60, 65, 70, 75, 80)))
