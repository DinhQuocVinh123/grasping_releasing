"""Tom tat chieu dai ngon cai + ngon tro do KHI DEO GANG, tu 1 file glove_finger_lengths_*.csv
(Assets/Scripts/GloveFingerLengthMeter.cs: 9 diem model gang + khoang cach tay theo Quest).

In: so anh dat (ngon duoi thang), trung vi + khoang p25-p75 tung dot, nua dau / nua sau buoi do, va lech so voi thuoc
neu co. Cot r(kc): tuong quan chieu dai tung anh voi khoang cach tay dung de tinh. Chieu dai TI LE THUAN voi khoang cach
do, nen r cao = nhieu khoang cach Quest la nguon nhieu chinh (lay trung vi nhieu anh de giam). r KHONG cho biet lech
co he thong -- cai do chi biet khi so voi thuoc (--ruler).

Cach dung:
  python tools/glove_finger_lengths.py glove_finger_lengths_20261021_xxxxxx.csv [--ruler tro_MCP-dau=80 cai_MCP-dau=60 ...]
"""
import argparse
import csv
import statistics

THUMB = ["cai_dot1(MCP-IP)", "cai_dot2(IP-dau)", "cai_MCP-dau"]
INDEX = ["tro_dot1(MCP-PIP)", "tro_dot2(PIP-DIP)", "tro_dot3(DIP-dau)", "tro_MCP-dau"]


def q(v, p):
    v = sorted(v)
    return v[min(len(v) - 1, int(p * (len(v) - 1)))]


def corr(a, b):
    ma, mb = statistics.mean(a), statistics.mean(b)
    sab = sum((x - ma) * (y - mb) for x, y in zip(a, b))
    sa = sum((x - ma) ** 2 for x in a) ** 0.5
    sb = sum((y - mb) ** 2 for y in b) ** 0.5
    return sab / (sa * sb) if sa > 0 and sb > 0 else 0.0


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("csv")
    ap.add_argument("--ruler", nargs="*", default=[], help="Gia tri do bang thuoc (mm), vd tro_MCP-dau=80")
    args = ap.parse_args()
    ruler = {k: float(v) for k, v in (r.split("=") for r in args.ruler)}

    rows = []
    with open(args.csv, newline="", encoding="utf-8") as f:
        for r in csv.DictReader(f):
            try:
                rows.append({k: float(v) for k, v in r.items()})
            except (TypeError, ValueError):
                continue  # dong cuoi bi cat khi tat app
    if not rows:
        raise SystemExit("File khong co dong nao.")
    dur = rows[-1]["t"] - rows[0]["t"]
    depth = [r["depth_m"] for r in rows]
    print(f"{args.csv}: {len(rows)} anh du 9 diem ro trong {dur:.0f} s, khoang cach tay trung vi {statistics.median(depth) * 100:.1f} cm "
          f"(p25-p75 {q(depth, .25) * 100:.1f}-{q(depth, .75) * 100:.1f})")
    for cols, flag, name in ((THUMB, "thumb_ok", "NGON CAI"), (INDEX, "index_ok", "NGON TRO")):
        ok = [r for r in rows if r[flag] == 1]
        print(f"\n{name}: {len(ok)} anh ngon duoi thang (tren {len(rows)})")
        if len(ok) < 20:
            print("  qua it anh -- giu ngon duoi thang, mat ngon quay ve camera")
            continue
        half = len(ok) // 2
        # p90 = chieu dai bao cao: ngon nghieng chi lam NGAN tren anh, anh dai nhat gan that nhat (21/10: ngon tro p90
        # 90.4 mm, thuoc 90). Tung dot chi de tham khao: khop giua bi gang che, vi tri theo cach gan nhan.
        print(f"  {'dot':20s} {'p90':>7s} {'trung vi':>9s} {'p25-p75':>13s} {'nua dau':>8s} {'nua sau':>8s} {'r(kc)':>6s}" +
              (f" {'thuoc':>7s} {'lech p90':>9s}" if ruler else ""))
        for c in cols:
            v = [r[c] for r in ok]
            p90 = q(v, .9)
            line = (f"  {c:20s} {p90:7.1f} {statistics.median(v):9.1f} {q(v, .25):6.1f}-{q(v, .75):<6.1f} "
                    f"{statistics.median(v[:half]):8.1f} {statistics.median(v[half:]):8.1f} "
                    f"{corr(v, [r['depth_m'] for r in ok]):6.2f}")
            if c in ruler:
                line += f" {ruler[c]:7.1f} {(p90 - ruler[c]) / ruler[c] * 100:+8.1f}%"
            print(line)


if __name__ == "__main__":
    main()
