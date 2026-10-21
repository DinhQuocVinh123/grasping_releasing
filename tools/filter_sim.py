"""Mo phong bo loc co tay cua ImageHandSolver (One-Euro co nhan/bo theo muc tin + 2 tang truot muot + ngoai suy) tren
ket qua giai THO trong 1 file glove_diag_*.csv, de thu thong so ma khong can deo kinh.

  1. Chay bo loc voi thong so HIEN TAI va so voi co tay dang hien trong log (wx..wz) -> mo phong co dung khong.
  2. Thu nhieu bo thong so: DO TRE (luc tay chay, so voi tay that luc phoi sang -- nhu latency_diag.py) va DO RUNG
     (luc tay dung yen: lech RMS so voi trung binh truot 0.3 s cua chinh tay hien).

Chi mo phong VI TRI co tay. Bo qua: luat nhay huong (xoay), luc doi cach giai (_switchSeconds), tron voi tay Quest.
Ngon tay dung chung _minCutoff (beta rieng) -- doi _minCutoff la doi ca ngon.

Cach dung:
  python tools/filter_sim.py glove_diag_20261020_xxxxxx.csv
"""
import bisect
import csv
import math
import sys

MOVING_MPS = 0.08
STILL_MPS = 0.02

CURRENT = dict(min_cutoff=0.7, beta=200.0, speed_cutoff=1.0, display=0.015, extrapolate=1.0, max_ex=0.06,
               reject_below=0.1, accept_floor=0.6)


def alpha(te, cutoff):
    r = 2 * math.pi * cutoff * te
    return r / (r + 1)


def lerp(a, b, k):
    return tuple(x + (y - x) * k for x, y in zip(a, b))


def dist(a, b):
    return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))


def load(path):
    rows = []
    with open(path, newline="", encoding="utf-8") as f:
        for r in csv.DictReader(f):
            if None in r.values():
                continue
            rows.append(dict(t=float(r["t"]), rt=float(r["rt"]), sexp=float(r["sexp"]), hstate=int(r["hstate"]),
                             trust=float(r["wtrust"]), raw=(float(r["sx"]), float(r["sy"]), float(r["sz"])),
                             shown=(float(r["wx"]), float(r["wy"]), float(r["wz"]))))
    return rows


def simulate(rows, p):
    """Tra ve vi tri hien moi dong (None truoc lan giai dau)."""
    fP = mid = disp = None
    speed, vel, t_w, t_f = 0.0, (0.0, 0.0, 0.0), 0.0, 0.0
    last_exp, prev_t, out = 0.0, None, []
    for r in rows:
        now = r["t"]
        if r["sexp"] > 0 and r["sexp"] != last_exp:  # ket qua giai moi trong khung nay
            last_exp = r["sexp"]
            x = r["raw"]
            if fP is None:
                fP = mid = disp = x
                t_w = t_f = now
            else:
                tr = r["trust"]
                acc = max(tr, p["accept_floor"]) if tr >= p["reject_below"] else 0.0
                if acc > 0:
                    te = max(now - t_w, 1e-3)
                    speed += (dist(x, fP) / te - speed) * alpha(te, p["speed_cutoff"])
                    newP = lerp(fP, x, alpha(te, p["min_cutoff"] + p["beta"] * speed) * acc)
                    vel = lerp(vel, tuple((a - b) / te for a, b in zip(newP, fP)), 0.5)
                    fP, t_w = newP, now
                else:
                    vel = (0.0, 0.0, 0.0)
                t_f = now
        if fP is not None and prev_t is not None:
            dt = max(now - prev_t, 1e-4)
            a = 1 - math.exp(-dt / max(p["display"], 1e-3)) if p["display"] > 0 else 1.0
            ahead = min(max(now - t_f, 0.0), p["max_ex"])
            tgt = tuple(f + p["extrapolate"] * ahead * v for f, v in zip(fP, vel))
            mid = lerp(mid, tgt, a)
            disp = lerp(disp, mid, a)
        prev_t = now
        out.append(disp)
    return out


def truth_series(rows):
    truth = {}
    for r in rows:
        if r["sexp"] > 0:
            truth[r["sexp"]] = r["raw"]
    tt = sorted(truth)
    return tt, [truth[t] for t in tt]


def make_truth_at(tt, tp):
    def at(x):
        i = bisect.bisect_right(tt, x)
        if i == 0 or i >= len(tt) or tt[i] - tt[i - 1] > 0.2:
            return None
        k = (x - tt[i - 1]) / (tt[i] - tt[i - 1])
        return lerp(tp[i - 1], tp[i], k)
    return at


def jerk(rows, shown, truth_at):
    """GIAT khi tay chay: RMS sai phan bac 2 cua vi tri hien giua 3 khung lien tiep (mm) -- chuyen dong deu = 0, nhay theo
    nhip anh (18/s) = lon. Truot muot qua nhanh tung lam tay giat khi nhac vat (ghi chu _displaySeconds, 03/10)."""
    acc = []
    for i in range(1, len(rows) - 1):
        a, b, c = shown[i - 1], shown[i], shown[i + 1]
        if a is None or c is None or rows[i]["hstate"] != 0:
            continue
        p, q = truth_at(rows[i]["rt"] - 0.05), truth_at(rows[i]["rt"] + 0.05)
        if not (p and q) or dist(p, q) / 0.1 < MOVING_MPS:
            continue
        acc.append(sum((x - 2 * y + z) ** 2 for x, y, z in zip(a, b, c)))
    return math.sqrt(sum(acc) / len(acc)) * 1000 if acc else float("nan")


def evaluate(rows, shown, truth_at):
    """(do tre ms, sai lech mm o do tre do, rung luc dung yen mm RMS)."""
    samples = [(r["rt"], s) for r, s in zip(rows, shown) if s is not None and r["hstate"] == 0]
    moving = []
    for rt, s in samples:
        a, b = truth_at(rt - 0.05), truth_at(rt + 0.05)
        if a and b and dist(a, b) / 0.1 >= MOVING_MPS:
            moving.append((rt, s))
    best = None
    for lag_ms in range(0, 401, 2):
        errs = [dist(s, tr) for rt, s in moving if (tr := truth_at(rt - lag_ms / 1000)) is not None]
        if len(errs) > 100:
            e = sorted(errs)[len(errs) // 2]
            if best is None or e < best[1]:
                best = (lag_ms, e)
    ts = [rt for rt, _ in samples]
    jit = []
    for i, (rt, s) in enumerate(samples):
        a, b = truth_at(rt - 0.15), truth_at(rt + 0.15)
        if not (a and b) or dist(a, b) / 0.3 > STILL_MPS:
            continue
        lo, hi = bisect.bisect_left(ts, rt - 0.15), bisect.bisect_right(ts, rt + 0.15)
        if hi - lo < 10:
            continue
        m = tuple(sum(samples[k][1][j] for k in range(lo, hi)) / (hi - lo) for j in range(3))
        jit.append(dist(s, m) ** 2)
    rms = math.sqrt(sum(jit) / len(jit)) * 1000 if jit else float("nan")
    return (best[0] if best else float("nan"), best[1] * 1000 if best else float("nan"), rms, len(jit))


def main(path):
    rows = load(path)
    tt, tp = truth_series(rows)
    truth_at = make_truth_at(tt, tp)

    sim = simulate(rows, CURRENT)
    errs = [dist(s, r["shown"]) for r, s in zip(rows, sim) if s is not None and r["hstate"] == 0]
    errs.sort()
    print(f"{path}: {len(rows)} khung, {len(tt)} ket qua giai")
    print(f"  Mo phong thong so hien tai so voi tay dang hien trong log: lech trung vi {errs[len(errs) // 2] * 1000:.1f} mm, "
          f"p90 {errs[int(len(errs) * 0.9)] * 1000:.1f} mm")
    shown_real = [r["shown"] for r in rows]
    real = evaluate(rows, shown_real, truth_at)
    print(f"  Tay hien THAT trong log: tre {real[0]:.0f} ms (lech {real[1]:.1f} mm), rung dung yen {real[2]:.2f} mm RMS "
          f"({real[3]} khung), giat khi chay {jerk(rows, shown_real, truth_at):.2f} mm")

    # minCutoff va san nhan (accept_floor) gan nhu khong doi gi (20/10 12:17) -> chi thu beta + truot muot hien thi
    # Hien tai = sau buoc C (20/10 12:40); truoc do beta 80, hien 0.02
    variants = [("hien tai (beta 200, hien 0.015)", {})]
    for beta in (80, 150, 200, 300):
        for disp in (0.02, 0.015, 0.01, 0.005):
            if (beta, disp) != (200, 0.015):
                variants.append((f"beta {beta}, hien {disp}", dict(beta=beta, display=disp)))
    variants.append(("khong loc One-Euro, hien 0.01", dict(min_cutoff=1000, beta=0, display=0.01, accept_floor=1.0)))
    print("\n  Bo thong so (mo phong)                 tre ms   lech mm   rung dung yen mm RMS   giat khi chay mm")
    for name, change in variants:
        p = dict(CURRENT, **change)
        shown = simulate(rows, p)
        lag, err, jit, _ = evaluate(rows, shown, truth_at)
        print(f"    {name:36s} {lag:6.0f}   {err:6.1f}    {jit:10.2f}          {jerk(rows, shown, truth_at):8.2f}")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    main(sys.argv[1])
