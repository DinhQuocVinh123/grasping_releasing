"""Feasibility test for latency compensation with the Quest: how late and how jittery is the Quest's own hand tracking (looking
at the GLOVED hand) compared with the real hand, from one glove_diag_*.csv file with the qrx..qrt columns (turn on Log Diagnostics
on FingerUDPReceiver).

Real hand = the RAW wrist solved from the camera image, stamped with the time the camera EXPOSED that image (sexp, sx..sz) -- like
latency_diag.py. Raw Quest = the Quest wrist BEFORE GloveWristStabilizer (qrx..qrz, received at qrt). For reference, the Quest wrist
AFTER the filter (rwx..rwz) is compared too.

Latency: the time shift that makes the Quest VELOCITY match the real-hand velocity best (velocities, so the Quest's constant position
offset -- a few cm with the glove -- does not matter). Match r = velocity correlation at that shift. Split into LATERAL (perpendicular
to the view direction) and DEPTH (along the view direction) -- the Quest's depth for the gloved hand jitters the most.

Decision rule (set BEFORE looking at the data):
  CONTINUE if the raw Quest lags <= 100 ms AND the lateral velocity match r >= 0.7; otherwise CHANGE DIRECTION.
  (Result of the first test: 115 ms, r = 0.27 -> change direction.)

The printed report is in Vietnamese.

Usage:
  python tools/analysis/quest_raw_feasibility.py glove_diag_20261020_xxxxxx.csv
"""
import bisect
import csv
import math
import sys

MAX_LATENCY_MS = 100.0
MIN_LATERAL_R = 0.7
MOVING_MPS = 0.08
MAX_GAP_S = 0.2


def load(path):
    with open(path, newline="", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        need = {"rt", "sexp", "sx", "qrx", "qvalid", "qrt", "rwx", "vqx"}
        missing = need - set(reader.fieldnames or [])
        if missing:
            sys.exit(f"File thieu cot {sorted(missing)} -- can ban build co cot Quest tho (20/10 tro di).")
        return [r for r in reader if None not in r.values()]


def rotate_forward(qx, qy, qz, qw):
    """View direction (the head's +Z axis) from a Unity quaternion."""
    return (2 * (qx * qz + qw * qy), 2 * (qy * qz - qw * qx), 1 - 2 * (qx * qx + qy * qy))


def main(path):
    rows = load(path)
    t0 = float(rows[0]["rt"])
    dur = float(rows[-1]["rt"]) - t0

    # Real hand by exposure time
    truth = {}
    for r in rows:
        e = float(r["sexp"])
        if e > 0:
            truth[e] = (float(r["sx"]), float(r["sy"]), float(r["sz"]))
    tt = sorted(truth)
    tp = [truth[t] for t in tt]
    if len(tt) < 50:
        sys.exit("Qua it anh co luc phoi sang de do.")

    # View direction over time (to split lateral / depth)
    view_t = [float(r["rt"]) for r in rows]
    view_f = [rotate_forward(float(r["vqx"]), float(r["vqy"]), float(r["vqz"]), float(r["vqw"])) for r in rows]

    def forward_at(t):
        i = min(max(bisect.bisect_left(view_t, t), 0), len(view_t) - 1)
        return view_f[i]

    # Raw Quest: one sample per update (qrt changes), only while tracking
    raw, last_t = [], None
    for r in rows:
        qt = float(r["qrt"])
        if r["qvalid"] == "1" and qt > 0 and qt != last_t:
            raw.append((qt, (float(r["qrx"]), float(r["qry"]), float(r["qrz"]))))
            last_t = qt
    # Quest after the filter (older columns): whenever the value changes
    filt, last_p = [], None
    for r in rows:
        p = (float(r["rwx"]), float(r["rwy"]), float(r["rwz"]))
        if p != last_p and any(abs(v) > 1e-6 for v in p):
            filt.append((float(r["rt"]), p))
        last_p = p

    tracked = sum(1 for r in rows if r["qvalid"] == "1") / len(rows) * 100
    gaps = [b[0] - a[0] for a, b in zip(raw, raw[1:])]
    long_gaps = [g for g in gaps if g > 0.3]
    print(f"{path}: {dur:.0f} s, {len(tt)} anh co luc phoi sang")
    print(f"  Quest tho: {len(raw)} lan cap nhat (~{len(raw) / max(dur, 1e-6):.0f}/s), dang theo doi {tracked:.0f}% so khung, "
          f"mat dau > 0.3 s: {len(long_gaps)} lan (tong {sum(long_gaps):.1f} s)")

    def truth_vel(t):
        """Real-hand velocity at time t (central difference +-20 ms on the interpolated path), None if images are missing or the hand is still."""
        def at(x):
            i = bisect.bisect_right(tt, x)
            if i == 0 or i >= len(tt) or tt[i] - tt[i - 1] > MAX_GAP_S:
                return None
            k = (x - tt[i - 1]) / (tt[i] - tt[i - 1])
            return tuple(tp[i - 1][j] + k * (tp[i][j] - tp[i - 1][j]) for j in range(3))
        a, b = at(t - 0.02), at(t + 0.02)
        if a is None or b is None:
            return None
        v = tuple((b[j] - a[j]) / 0.04 for j in range(3))
        return v if math.sqrt(sum(c * c for c in v)) >= MOVING_MPS else None

    def velocities(seq):
        out = []
        for (ta, pa), (tb, pb) in zip(seq, seq[1:]):
            dt = tb - ta
            if 0 < dt < 0.1:
                out.append(((ta + tb) / 2, tuple((pb[j] - pa[j]) / dt for j in range(3))))
        return out

    def split(v, f):
        d = sum(v[j] * f[j] for j in range(3))
        return tuple(v[j] - d * f[j] for j in range(3)), (d * f[0], d * f[1], d * f[2])

    def best_lag(seq, part):
        vel = velocities(seq)
        best = None
        for lag_ms in range(-50, 401, 5):
            lag = lag_ms / 1000.0
            sxy = sxx = syy = 0.0
            n = 0
            for t, v in vel:
                tv = truth_vel(t - lag)
                if tv is None:
                    continue
                f = forward_at(t)
                if part != "3d":
                    v = split(v, f)[0 if part == "lateral" else 1]
                    tv = split(tv, f)[0 if part == "lateral" else 1]
                for j in range(3):
                    sxy += v[j] * tv[j]; sxx += v[j] * v[j]; syy += tv[j] * tv[j]
                n += 1
            if n >= 200 and sxx > 0 and syy > 0:
                r = sxy / math.sqrt(sxx * syy)
                if best is None or r > best[1]:
                    best = (lag_ms, r, n)
        return best

    print("\n  Tre so voi tay that (luc phoi sang) va do khop van toc r:")
    results = {}
    for name, seq in (("Quest THO", raw), ("Quest sau loc", filt)):
        for part, label in (("3d", "3 chieu"), ("lateral", "ngang"), ("depth", "sau")):
            b = best_lag(seq, part)
            results[(name, part)] = b
            txt = f"{b[0]:4d} ms  r={b[1]:.2f}  ({b[2]} mau)" if b else "khong du du lieu (tay it chuyen dong?)"
            print(f"    {name:14s} {label:8s}: {txt}")

    raw_lat = results[("Quest THO", "lateral")]
    print("\n  KET LUAN theo luat dat truoc (tre <= %d ms VA r ngang >= %.1f):" % (MAX_LATENCY_MS, MIN_LATERAL_R))
    if raw_lat is None:
        print("    CHUA KET LUAN DUOC -- lan sau vay tay ngang qua lai (trong khung camera) it nhat 30 s.")
    elif raw_lat[0] <= MAX_LATENCY_MS and raw_lat[1] >= MIN_LATERAL_R:
        print(f"    LAM TIEP: Quest tho tre {raw_lat[0]} ms, khop ngang r={raw_lat[1]:.2f}.")
    else:
        why = []
        if raw_lat[0] > MAX_LATENCY_MS:
            why.append(f"tre {raw_lat[0]} ms > {MAX_LATENCY_MS:.0f}")
        if raw_lat[1] < MIN_LATERAL_R:
            why.append(f"khop ngang r={raw_lat[1]:.2f} < {MIN_LATERAL_R}")
        print("    CHUYEN HUONG: " + ", ".join(why) + ".")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    main(sys.argv[1])
