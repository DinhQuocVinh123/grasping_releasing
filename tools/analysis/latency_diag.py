"""Latency of the virtual glove hand against the real hand, from one glove_diag_*.csv file (turn on Log Diagnostics on FingerUDPReceiver).

Method: each camera image gives one RAW solved wrist position (sx, sy, sz) with the time the camera EXPOSED that image (sexp) -- that is
"the real hand at time sexp". Every frame logs the SHOWN wrist position (wx, wy, wz) with its time (rt). If the virtual hand lags by tau
seconds, the position shown at rt must match the real position at rt - tau. Try tau = 0..800 ms and pick the tau with the smallest offset
(only while the hand moves -- with a still hand every tau matches).

  virtual-hand latency = tau (exposure -> Unity sets the hand pose) + 1 frame to draw it (~14 ms, not measurable in the app)

The printed report is in Vietnamese.

Usage:
  python tools/analysis/latency_diag.py glove_diag_20261019_103938.csv
"""
import bisect
import csv
import math
import sys

FRAME_MS = 1000.0 / 72.0      # 1 frame drawn on the Quest display (72 Hz) -- added to the result
MOVING_MPS = 0.08             # only consider the real hand moving faster than this (m/s)
MAX_GAP_S = 0.2               # no interpolation between two images further apart than this
WINDOW_S = 10.0


def load(path):
    rows = []
    with open(path, newline="", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        missing = {"rt", "sexp", "sx", "wx", "hstate"} - set(reader.fieldnames or [])
        if missing:
            sys.exit(f"File thieu cot {sorted(missing)} -- can ban build co cot do tre (19/10 tro di).")
        for row in reader:
            if None in row.values():
                continue  # last line cut off when the app was closed
            rows.append(row)
    return rows


def pct(values, p):
    v = sorted(values)
    return v[min(len(v) - 1, max(0, int(round(p / 100 * (len(v) - 1)))))] if v else float("nan")


def main(path):
    rows = load(path)
    # Real hand: one point per image (deduplicated -- every row repeats the latest result)
    truth = {}
    for r in rows:
        e = float(r["sexp"])
        if e > 0:
            truth[e] = (float(r["sx"]), float(r["sy"]), float(r["sz"]))
    tt = sorted(truth)
    tp = [truth[t] for t in tt]
    if len(tt) < 50:
        sys.exit(f"Chi co {len(tt)} anh co luc phoi sang -- khong du de do (kiem tra cot cam_ms co -1 khong).")

    # Virtual hand: only frames fully following the image (hstate 0)
    shown = [(float(r["rt"]), (float(r["wx"]), float(r["wy"]), float(r["wz"])))
             for r in rows if r["hstate"] == "0"]

    def truth_at(t):
        """Real position at time t (interpolated between the two neighbouring images) and speed; None if no image is close."""
        i = bisect.bisect_right(tt, t)
        if i == 0 or i >= len(tt) or tt[i] - tt[i - 1] > MAX_GAP_S:
            return None
        a, b = tt[i - 1], tt[i]
        k = (t - a) / (b - a)
        p = tuple(tp[i - 1][j] + k * (tp[i][j] - tp[i - 1][j]) for j in range(3))
        speed = math.dist(tp[i], tp[i - 1]) / (b - a)
        return p, speed

    def best_tau(samples):
        best = None
        for tau_ms in range(0, 801, 2):
            tau = tau_ms / 1000.0
            err, n = 0.0, 0
            for t, p in samples:
                q = truth_at(t - tau)
                if q is None or q[1] < MOVING_MPS:
                    continue
                err += math.dist(p, q[0]) ** 2
                n += 1
            if n >= 100:
                rms = math.sqrt(err / n) * 1000
                if best is None or rms < best[1]:
                    best = (tau_ms, rms, n)
        return best

    exp_age = [float(r["exp_age_ms"]) for r in rows if float(r["exp_age_ms"]) > 0]
    cam = [float(r["cam_ms"]) for r in rows if float(r["cam_ms"]) >= 0]
    print(f"{path}: {len(rows)} khung, {len(tt)} anh co luc phoi sang, {len(shown)} khung tay theo anh")
    print(f"  camera phoi sang -> Unity lay anh  : trung vi {pct(cam, 50):5.0f} ms  (p90 {pct(cam, 90):.0f})")
    print(f"  phoi sang -> ket qua Python ve     : trung vi {pct(exp_age, 50):5.0f} ms  (p90 {pct(exp_age, 90):.0f})")

    overall = best_tau(shown)
    if overall is None:
        sys.exit("Khong du doan tay chuyen dong de do -- lan sau vay tay qua lai vai lan.")
    tau, rms, n = overall
    print(f"\n  DO TRE TAY AO so voi tay that: {tau + FRAME_MS:.0f} ms")
    print(f"    = {tau} ms (phoi sang -> Unity dat tu the) + {FRAME_MS:.0f} ms (1 khung ve len man hinh)")
    print(f"    sai lech con lai {rms:.1f} mm o do tre do, tren {n} khung tay dang chay")

    print(f"\n  Theo tung doan {WINDOW_S:.0f} s (tre ms | sai lech mm | so khung):")
    start = shown[0][0]
    i = 0
    while i < len(shown):
        j = i
        while j < len(shown) and shown[j][0] - shown[i][0] < WINDOW_S:
            j += 1
        b = best_tau(shown[i:j])
        label = f"{shown[i][0] - start:6.0f}s"
        print(f"    {label}: " + (f"{b[0] + FRAME_MS:5.0f} | {b[1]:5.1f} | {b[2]}" if b else "  (tay it chuyen dong)"))
        i = j


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    main(sys.argv[1])
