"""Frozen empirical actuator force model. Runtime: Python standard library only.

Inputs: pressure_bar, length_setting_mm, branch ('pressurization' or 'release').
Output: fitted force-sensor reading in N, with recorded offsets preserved.
The length input is the nominal tether-setting label, NOT measured live length.
No fitting, filtering, or physical force clipping occurs during inference.
"""
from __future__ import annotations

from bisect import bisect_left, bisect_right
import hashlib
import json
import math
from pathlib import Path
from typing import Iterable


def _finite_scalar(value: float, name: str) -> float:
    if isinstance(value, bool):
        raise TypeError(f"{name} must be a real scalar, not a boolean")
    try:
        result = float(value)
    except (TypeError, ValueError) as exc:
        raise TypeError(f"{name} must be a real scalar") from exc
    if not math.isfinite(result):
        raise ValueError(f"{name} must be finite")
    return result


def _edge_slope(h0: float, h1: float, delta0: float, delta1: float) -> float:
    slope = ((2.0 * h0 + h1) * delta0 - h0 * delta1) / (h0 + h1)
    if slope * delta0 <= 0.0:
        return 0.0
    if delta0 * delta1 <= 0.0 and abs(slope) > 3.0 * abs(delta0):
        return 3.0 * delta0
    return slope


def _pchip_value(x: tuple[float, ...], y: list[float], query: float) -> float:
    """Shape-preserving cubic Hermite interpolation along the length axis."""
    exact = bisect_left(x, query)
    if exact < len(x) and query == x[exact]:
        return y[exact]
    h = [x[i + 1] - x[i] for i in range(len(x) - 1)]
    delta = [(y[i + 1] - y[i]) / h[i] for i in range(len(h))]
    slopes = [0.0] * len(x)
    slopes[0] = _edge_slope(h[0], h[1], delta[0], delta[1])
    slopes[-1] = _edge_slope(h[-1], h[-2], delta[-1], delta[-2])
    for i in range(1, len(x) - 1):
        left, right = delta[i - 1], delta[i]
        if left == 0.0 or right == 0.0 or left * right < 0.0:
            slopes[i] = 0.0
        else:
            w1 = 2.0 * h[i] + h[i - 1]
            w2 = h[i] + 2.0 * h[i - 1]
            slopes[i] = (w1 + w2) / (w1 / left + w2 / right)
    j = min(max(bisect_right(x, query) - 1, 0), len(x) - 2)
    u = (query - x[j]) / h[j]
    h00 = 2.0 * u**3 - 3.0 * u**2 + 1.0
    h10 = u**3 - 2.0 * u**2 + u
    h01 = -2.0 * u**3 + 3.0 * u**2
    h11 = u**3 - u**2
    return h00 * y[j] + h10 * h[j] * slopes[j] + h01 * y[j + 1] + h11 * h[j] * slopes[j + 1]


class ForceModel:
    """Load the frozen coefficients once; call predict_force repeatedly.

    Pressure is clipped separately to each learned pressure curve's support,
    exactly as during fitting. Length extrapolation is rejected. Branch is an
    explicit controller state; it is not inferred from the sign of pressure.
    """

    def __init__(self, coefficients_path: str | Path | None = None):
        path = Path(coefficients_path) if coefficients_path is not None else Path(__file__).with_name("actuator_coefficients.json")
        self._data = json.loads(path.read_text(encoding="utf-8"))
        if self._data.get("schema") != "actuator_force_piecewise_v1":
            raise ValueError("Unsupported coefficient schema")
        signature = json.dumps(self._data["parameters"], sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")
        if hashlib.sha256(signature).hexdigest() != self._data.get("parameters_sha256"):
            raise ValueError("Coefficient checksum mismatch: use the matching complete coefficient file")
        self._lengths = tuple(float(x) for x in self._data["parameters"]["length_settings_mm"])
        if len(self._lengths) < 3 or any(b <= a for a, b in zip(self._lengths, self._lengths[1:])):
            raise ValueError("Length settings must be strictly increasing; at least three are required")
        self._curves = self._data["parameters"]["curves"]
        for branch in ("pressurization", "release"):
            if len(self._curves[branch]) != len(self._lengths):
                raise ValueError("Missing branch/length coefficients")
            for curve in self._curves[branch]:
                x = curve["pressure_breaks_bar"]
                c = curve["coefficients_a3_a2_a1_a0"]
                if len(x) < 2 or len(c) != len(x) - 1 or any(b <= a for a, b in zip(x, x[1:])):
                    raise ValueError("Invalid pressure interval table")
                if any(len(row) != 4 for row in c) or not all(math.isfinite(v) for row in c for v in row):
                    raise ValueError("Invalid cubic coefficients")

    @property
    def model_id(self) -> str:
        return self._data["model_id"]

    @property
    def length_domain_mm(self) -> tuple[float, float]:
        return self._lengths[0], self._lengths[-1]

    def predict_force(self, pressure_bar: float, length_setting_mm: float, branch: str) -> float:
        """Return fitted load-cell reading [N] for scalar inputs.

        length_setting_mm must be within [55, 80] for the supplied model.
        branch must be 'pressurization' or 'release'. No automatic force
        zeroing or max(0,F) is applied; small negative offsets are retained.
        """
        pressure = _finite_scalar(pressure_bar, "pressure_bar")
        length = _finite_scalar(length_setting_mm, "length_setting_mm")
        if not isinstance(branch, str) or branch.strip().lower() not in self._curves:
            raise ValueError("branch must be 'pressurization' or 'release'")
        branch = branch.strip().lower()
        if not self._lengths[0] <= length <= self._lengths[-1]:
            raise ValueError(f"length_setting_mm must be in {self.length_domain_mm}; extrapolation is disabled")
        values = []
        for curve in self._curves[branch]:
            x = curve["pressure_breaks_bar"]
            p = min(max(pressure, x[0]), x[-1])
            j = min(max(bisect_right(x, p) - 1, 0), len(x) - 2)
            dp = p - x[j]
            a3, a2, a1, a0 = curve["coefficients_a3_a2_a1_a0"][j]
            values.append(((a3 * dp + a2) * dp + a1) * dp + a0)
        return float(_pchip_value(self._lengths, values, length))

    def predict_batch(self, pressure_bar: Iterable[float], length_setting_mm: Iterable[float], branch: Iterable[str]) -> list[float]:
        """Predict equally sized input sequences; no implicit broadcasting."""
        pressures, lengths, branches = list(pressure_bar), list(length_setting_mm), list(branch)
        if not len(pressures) == len(lengths) == len(branches):
            raise ValueError("Batch inputs must have the same number of elements")
        return [self.predict_force(p, length, b) for p, length, b in zip(pressures, lengths, branches)]


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description="Predict actuator force [N] with frozen all-six calibration")
    parser.add_argument("--pressure-bar", type=float, required=True)
    parser.add_argument("--length-mm", type=float, required=True, help="Nominal tether-setting label [mm]")
    parser.add_argument("--branch", choices=("pressurization", "release"), required=True)
    parser.add_argument("--coefficients", type=Path, default=None)
    args = parser.parse_args()
    model = ForceModel(args.coefficients)
    print(json.dumps({"model_id": model.model_id, "force_N": model.predict_force(args.pressure_bar, args.length_mm, args.branch)}))
