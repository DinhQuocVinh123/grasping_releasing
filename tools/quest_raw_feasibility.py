"""Thu kha thi bu tre bang Quest: bo theo doi tay san co cua Quest (nhin tay DEO GANG) tre va rung bao nhieu so voi tay
that, tu 1 file glove_diag_*.csv co cot qrx..qrt (ban build tu 20/10, bat Log Diagnostics tren FingerUDPReceiver).

Tay that = co tay giai THO tu anh camera, gan voi luc camera PHOI SANG anh do (sexp, sx..sz) -- nhu latency_diag.py.
Quest tho = co tay Quest TRUOC GloveWristStabilizer (qrx..qrz, luc nhan qrt). De so, so sanh them co tay Quest SAU bo loc
(rwx..rwz) -- cung la cot ma lan thu 19/10 da dung.

Do tre: tim do lech thoi gian lam VAN TOC Quest khop nhat voi van toc tay that (dung van toc nen lech vi tri co dinh cua
Quest -- vai cm khi deo gang -- khong anh huong). Do khop r = he so tuong quan van toc tai do tre do. Tach 2 phan: NGANG
(vuong goc huong nhin) va SAU (doc huong nhin) -- Quest nhin tay deo gang rung chieu sau nhieu nhat.

Luat quyet dinh (dat TRUOC khi xem so lieu, Niche_Latency_Compensation_Literature.md muc 6):
  LAM TIEP niche neu Quest tho tre <= 100 ms VA van toc ngang khop r >= 0.7; khong thi CHUYEN HUONG.

Cach dung:
  python tools/quest_raw_feasibility.py glove_diag_20261020_xxxxxx.csv
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
    """Huong nhin (truc +Z cua dau) tu quaternion Unity."""
    return (2 * (qx * qz + qw * qy), 2 * (qy * qz - qw * qx), 1 - 2 * (qx * qx + qy * qy))


def main(path):
    rows = load(path)
    t0 = float(rows[0]["rt"])
    dur = float(rows[-1]["rt"]) - t0

    # Tay that theo luc phoi sang
    truth = {}
    for r in rows:
        e = float(r["sexp"])
        if e > 0:
            truth[e] = (float(r["sx"]), float(r["sy"]), float(r["sz"]))
    tt = sorted(truth)
    tp = [truth[t] for t in tt]
    if len(tt) < 50:
        sys.exit("Qua it anh co luc phoi sang de do.")

    # Huong nhin theo thoi gian dong (de tach ngang / sau)
    view_t = [float(r["rt"]) for r in rows]
    view_f = [rotate_forward(float(r["vqx"]), float(r["vqy"]), float(r["vqz"]), float(r["vqw"])) for r in rows]

    def forward_at(t):
        i = min(max(bisect.bisect_left(view_t, t), 0), len(view_t) - 1)
        return view_f[i]

    # Quest tho: moi lan cap nhat 1 mau (qrt doi), chi khi dang theo doi
    raw, last_t = [], None
    for r in rows:
        qt = float(r["qrt"])
        if r["qvalid"] == "1" and qt > 0 and qt != last_t:
            raw.append((qt, (float(r["qrx"]), float(r["qry"]), float(r["qrz"]))))
            last_t = qt
    # Quest sau bo loc (cot cu): moi khi gia tri doi
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
        """Van toc tay that luc t (sai phan trung tam +-20 ms tren duong noi suy), None neu thieu anh hoac tay dung yen."""
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
