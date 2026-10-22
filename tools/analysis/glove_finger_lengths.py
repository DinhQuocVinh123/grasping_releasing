"""Summarises the thumb and index lengths measured WITH THE GLOVE ON, from one glove_finger_lengths_*.csv file
(Assets/Scripts/Measurement/GloveFingerLengthMeter.cs: the glove model's 9 points + the hand distance from the Quest).

Prints: number of accepted frames (finger straight), p90 (the reported length -- a tilted finger only looks shorter), median +
p25-p75 per segment, first / second half of the session, and the difference from a ruler if given. Column r(kc): correlation of each
frame's length with the hand distance used. The length is PROPORTIONAL to that distance, so a high r means the Quest distance noise is
the main noise source (averaging many frames reduces it). r does NOT reveal a systematic bias -- only a ruler comparison (--ruler) does.
Column names: cai = thumb, tro = index, dau = tip, dot = segment. The printed report is in Vietnamese.

Usage:
  python tools/analysis/glove_finger_lengths.py glove_finger_lengths_20261021_xxxxxx.csv [--ruler tro_MCP-dau=90 cai_MCP-dau=60 ...]
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
                continue  # last line cut off when the app was closed
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
        # p90 = the reported length: a tilted finger only looks SHORTER in the image, the longest frames are closest to the truth
        # (first session: index p90 90.4 mm, ruler 90). Single segments are for reference only: the glove hides the middle joints.
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
