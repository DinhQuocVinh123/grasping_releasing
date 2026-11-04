#!/usr/bin/env python3
"""
P(F, L) model of the braided (extensile McKibben) hydraulic dorsal actuator.

Fitted by Tran Van Nghia on four recordings (95, 100, 105, 110 mm), 28 Oct 2026; the numbers
are in pfl_coefficients.json. L is the MEASURED actuator length in mm, F the force in N,
P the pressure in bar, s = +1 while pressure is rising (pressurize), -1 while falling (release).

Forward model (force the actuator makes):

    F = F_el - a1*P - K*Phi(P) + s*F_f0

  F_el = c_lin*L - c_inv2/L^2   tube elasticity (full force, at 0 bar)
  a1   = c/L                    pressure on the bore pushes the actuator longer
  P*   = c*L^exp + hyst*s       the knee: below it force barely changes, above it falls fast
  D    = a1 + K                 slope above the knee (N/bar)

The fit's exact knee function Phi (a smooth step of width w = 0.02 bar) is not in the files we
received, so it is taken as a hinge here: Phi = 0 below P*, P - P* above it. That is exact away
from the knee and off by at most about w right at it.

Inverse (what the relay needs), from the junior's sheet (image.png):

    above the knee (F <= F_knee):  P = P* + (F_el + F_f0*s - a1*P* - F) / D
    below the knee (F >  F_knee):  P = (F_el + F_f0*s - F) / a1    (the flat zone)

F_knee = F_el + F_f0*s - a1*P* is the force at the knee. Below the knee the force changes by only
about 0.07 N over 0-0.3 bar, so a small force error there moves the pressure a lot; the hinge keeps
the answer continuous and inside 0..P*.

    python pfl_model.py         # self-check against the worked example, and a small table
"""
import json
import math
import os

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_COEFFICIENTS = os.path.join(HERE, "pfl_coefficients.json")


class PFLModel:
    def __init__(self, path=None):
        path = path or DEFAULT_COEFFICIENTS
        with open(path, "r", encoding="utf-8") as fh:
            c = json.load(fh)
        self.model_id = c["model_id"]
        self.length_domain_mm = tuple(float(x) for x in c["length_domain_mm"])
        self._fel = (float(c["F_el"]["c_lin"]), float(c["F_el"]["c_inv2"]))
        self._a1 = float(c["a1"]["c"])
        self._ps = (float(c["P_star"]["c"]), float(c["P_star"]["exp"]), float(c["P_star"]["hyst"]))
        self._d = (float(c["D"]["c"]), float(c["D"]["k_num"]), float(c["D"]["k_den"]))
        self._ff0 = float(c["F_f0"])
        self.check = c.get("check")

    # -- the four length-dependent terms ------------------------------------------------
    def terms(self, length_mm, s):
        """(F_el N, a1 N/bar, P* bar, D N/bar, F_f0*s N) at length L and direction s (+1 / -1)."""
        L = float(length_mm)
        f_el = self._fel[0] * L - self._fel[1] / (L * L)
        a1 = self._a1 / L
        p_star = self._ps[0] * L ** self._ps[1] + self._ps[2] * s
        d = self._d[0] * (1.0 - self._d[1] * L * L) / (1.0 - self._d[2] * L * L)
        return f_el, a1, p_star, d, self._ff0 * s

    def knee_force(self, length_mm, s):
        """Force at the knee P*: wanted forces above this fall in the flat zone."""
        f_el, a1, p_star, _, ff = self.terms(length_mm, s)
        return f_el + ff - a1 * p_star

    def max_force(self, length_mm, s):
        """Force at 0 bar: the most the actuator makes at this length."""
        f_el, _, _, _, ff = self.terms(length_mm, s)
        return f_el + ff

    # -- forward and inverse ------------------------------------------------------------
    def force(self, pressure_bar, length_mm, s):
        """Force (N) at pressure P, length L, direction s. Negative = the tether would go slack."""
        f_el, a1, p_star, d, ff = self.terms(length_mm, s)
        P = float(pressure_bar)
        if P <= p_star:
            return f_el + ff - a1 * P
        return f_el + ff - a1 * p_star - d * (P - p_star)

    def pressure(self, force_n, length_mm, s):
        """Pressure (bar) that makes force F at length L, direction s. Not clamped: below 0 means
        'more than the actuator can make', large means 'less than zero force needs'."""
        f_el, a1, p_star, d, ff = self.terms(length_mm, s)
        f_knee = f_el + ff - a1 * p_star
        if force_n <= f_knee:
            return p_star + (f_knee - force_n) / d
        return (f_el + ff - force_n) / a1


if __name__ == "__main__":
    m = PFLModel()
    ck = m.check
    got = m.pressure(ck["F"], ck["L"], ck["s"])
    print("model %s, L %g-%g mm" % (m.model_id, *m.length_domain_mm))
    print("worked example: L %g, F %g, s %+d -> P %.4f bar (sheet: %.3f) %s" % (
        ck["L"], ck["F"], ck["s"], got, ck["P"], "OK" if abs(got - ck["P"]) < 0.002 else "MISMATCH"))
    print("\n  L   F@0bar  F@knee  P*(+1)  D     P for F=0 (+1)")
    for L in (95, 100, 105, 110):
        print("%5g  %6.3f  %6.3f  %6.3f  %5.3f  %6.3f" % (
            L, m.max_force(L, 1), m.knee_force(L, 1), m.terms(L, 1)[2], m.terms(L, 1)[3], m.pressure(0.0, L, 1)))
