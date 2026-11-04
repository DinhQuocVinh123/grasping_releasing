#!/usr/bin/env python3
"""
Wanted dorsal force -> actuator pressure, with the P(F, L) model of the braided actuator.

    force F (N) + actuator length L (mm) + direction  ->  target pressure P (bar)

The model (tools/actuator_model/pfl_model.py, fitted by Tran Van Nghia, 28 Oct 2026) gives the
pressure directly; nothing is searched. Force FALLS as pressure rises: full force at 0 bar, none by
about 0.64-1.05 bar depending on L (95-110 mm). So:

  * no force wanted  -> REST pressure (rest_bar): the Arduino holds the reference position;
  * more force       -> lower pressure, on the 'release' side (s = -1, pressure falling);
  * less force       -> higher pressure, on the 'pressurization' side (s = +1, pressure rising);
  * more than the actuator can make at this L -> the lowest pressure (p_min_bar): its maximum;
  * less than zero force needs (L near 110, where zero force is above 1 bar) -> rest_bar.

L is clamped to the range the model was fitted on (95-110 mm); outside it the formula breaks down.

Each finger keeps its own direction state. A change smaller than branch_deadband_n keeps the
previous direction, so tracking jitter does not flip between the two sides of the hysteresis.

    python dorsal_pressure.py            # prints a small table, as a self-check
"""
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PRESSURIZATION, RELEASE = "pressurization", "release"
SIGN = {PRESSURIZATION: +1, RELEASE: -1}


def load_model(coefficients=None):
    """Load the P(F, L) model from tools/actuator_model (or a given coefficient file)."""
    model_dir = os.path.join(HERE, "actuator_model")
    if model_dir not in sys.path:
        sys.path.insert(0, model_dir)
    from pfl_model import PFLModel   # noqa: E402 (path set just above)
    return PFLModel(coefficients)


class DorsalPressure:
    def __init__(self, model, rest_bar=1.0, p_min_bar=0.0, min_force_n=0.02, branch_deadband_n=0.005):
        self.model = model
        self.l_min, self.l_max = model.length_domain_mm
        self.rest_bar = rest_bar
        self.p_min = p_min_bar
        self.min_force = min_force_n
        self.deadband = branch_deadband_n
        self._branch = [PRESSURIZATION, PRESSURIZATION]
        self._prev_force = [0.0, 0.0]

    def clamp_length(self, length_mm):
        if not (isinstance(length_mm, (int, float)) and math.isfinite(length_mm)):
            return self.l_min
        return min(max(float(length_mm), self.l_min), self.l_max)

    def force(self, pressure_bar, length_mm, branch):
        """Model force (N) at this pressure, length and branch; 0 where the tether goes slack."""
        return max(0.0, self.model.force(pressure_bar, self.clamp_length(length_mm), SIGN[branch]))

    def pressure_for(self, force_n, length_mm, branch):
        """Pressure (bar) at which the actuator makes force_n. Saturates at both ends."""
        p = self.model.pressure(force_n, self.clamp_length(length_mm), SIGN[branch])
        return min(max(p, self.p_min), self.rest_bar)

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
    print("F (N) ->  pressure (bar) on the release side (force rising), per L")
    lengths = (95, 100, 105, 110)
    print("        " + "".join("L=%-7g" % L for L in lengths))
    for F in (0.1, 0.3, 0.6, 1.0, 1.3, 1.6, 2.0, 2.4):
        print("F=%-5.2f " % F + "".join("%-9.3f" % dp.pressure_for(F, L, RELEASE) for L in lengths))
