"""Tom tat chieu dai ngon cai + ngon tro do bang hand tracking Quest, tu 1 file finger_lengths_*.csv
(Assets/Scripts/FingerLengthMeter.cs, tay TRAN).

In cho tung tay: trung vi (mm) tung dot + khoang phan vi 25-75 (do on dinh), so sanh nua dau / nua sau buoi do, va
chieu dai / scale cua Quest. Neu chieu dai / scale gan nhu khong doi khi tay xoay, cu dong, va giong nhau giua nhieu
nguoi -> Quest chi phong to/thu nho 1 ban tay mau, KHONG do that tung ngon -> phai doi chieu voi thuoc.

Cach dung:
  python tools/finger_lengths.py finger_lengths_20261020_xxxxxx.csv [--ruler cai_MCP-dau=58 tro_MCP-dau=75 ...]
"""
import argparse
import csv
import statistics

COLUMNS = ["cai_goc_dot(CMC-MCP)", "cai_dot1(MCP-IP)", "cai_dot2(IP-dau)",
           "tro_xuongbantay", "tro_dot1(MCP-PIP)", "tro_dot2(PIP-DIP)", "tro_dot3(DIP-dau)",
           "cai_MCP-dau", "tro_MCP-dau"]


def q(v, p):
    v = sorted(v)
    return v[min(len(v) - 1, int(p * (len(v) - 1)))]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("csv")
    ap.add_argument("--ruler", nargs="*", default=[], help="Gia tri do bang thuoc (mm), vd tro_MCP-dau=75")
    args = ap.parse_args()
    ruler = {k: float(v) for k, v in (r.split("=") for r in args.ruler)}

    rows = []
    with open(args.csv, newline="", encoding="utf-8") as f:
        for r in csv.DictReader(f):
            try:
                rows.append({k: (v if k == "hand" else float(v)) for k, v in r.items()})
            except (TypeError, ValueError):
                continue  # dong cuoi bi cat khi tat app
    for hand, name in (("R", "TAY PHAI"), ("L", "TAY TRAI")):
        h = [r for r in rows if r["hand"] == hand]
        if len(h) < 30:
            print(f"{name}: chi {len(h)} khung -- bo qua")
            continue
        dur = h[-1]["t"] - h[0]["t"]
        scale = [r["scale"] for r in h]
        print(f"\n{name}: {len(h)} khung trong {dur:.0f} s, scale Quest trung vi {statistics.median(scale):.4f} "
              f"(p25-p75 {q(scale, .25):.4f}-{q(scale, .75):.4f})")
        print(f"  {'dot':22s} {'trung vi':>9s} {'p25-p75':>13s} {'nua dau':>8s} {'nua sau':>8s} {'/scale':>8s}" +
              (f" {'thuoc':>7s} {'lech':>6s}" if ruler else ""))
        half = len(h) // 2
        for c in COLUMNS:
            v = [r[c] for r in h]
            m = statistics.median(v)
            line = (f"  {c:22s} {m:9.1f} {q(v, .25):6.1f}-{q(v, .75):<6.1f} {statistics.median(v[:half]):8.1f} "
                    f"{statistics.median(v[half:]):8.1f} {statistics.median(r[c] / r['scale'] for r in h):8.1f}")
            if c in ruler:
                line += f" {ruler[c]:7.1f} {m - ruler[c]:+6.1f}"
            print(line)


if __name__ == "__main__":
    main()
