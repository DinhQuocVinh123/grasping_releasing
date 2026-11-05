"""How smoothly the shown virtual hand makes big turns (e.g. turning the palm over), from GloveReplayLab's display.csv.

Usage: python replay_lab_turns.py <lab out dir> [<lab out dir> ...]
  (each dir = out/<set>; every recording sub-folder with a display.csv is scored)

display.csv has one row per rendered frame (72 Hz): the shown wrist rotation and the latest raw solve. The replay camera stands
still, so rotations are compared directly. A "turn" = the RAW solve rotating more than 90 degrees within 0.6 s (the solver often
jumps from one interpretation to the other in a single image). For each turn, measured on the shown hand:
  delay   : time from the raw solve passing half of the turn to the shown hand passing half of it (ms)
  stall   : longest stretch, after the raw solve has started the turn and before the shown hand reaches 90% of it, where the shown
            hand moves slower than 15% of the turn's average speed (ms) -- the visible "hitch"
  peak    : highest shown angular speed during the turn (deg/s) -- very high = a whip / snap
  sweep   : time the shown hand takes from 10% to 90% of the turn (ms) -- very short = a snap
"""
import os
import sys

import numpy as np
import pandas as pd
from scipy.spatial.transform import Rotation as R


def turns(d):
    t = d.t.values
    raw = R.from_quat(d[["rqx", "rqy", "rqz", "rqw"]].values)
    shown = R.from_quat(d[["qx", "qy", "qz", "qw"]].values)
    out = []
    i, n = 1, len(t)
    while i < n:
        j = np.searchsorted(t, t[i] + 0.6)
        if j >= n:
            break
        ang = np.degrees((raw[i].inv() * raw[i:j]).magnitude())
        k = int(np.argmax(ang))
        if ang[k] > 90:
            a, b = i, i + k
            # extend b until the shown hand has (nearly) finished the turn, at most 1.5 s
            e = min(np.searchsorted(t, t[b] + 1.5), n - 1)
            total = ang[k]
            target = raw[b]
            sh = np.degrees((shown[a].inv() * shown[a:e + 1]).magnitude())
            prog = sh / total
            rprog = np.degrees((raw[a].inv() * raw[a:e + 1]).magnitude()) / total
            tt = t[a:e + 1]
            def first(p, x):
                w = np.where(p >= x)[0]
                return tt[w[0]] if len(w) else np.nan
            r50, s10, s50, s90 = first(rprog, 0.5), first(prog, 0.1), first(prog, 0.5), first(prog, 0.9)
            sp = np.r_[0, np.degrees((shown[a:e].inv() * shown[a + 1:e + 1]).magnitude()) / np.maximum(np.diff(tt), 1e-4)]
            avg = 0.8 * total / max(s90 - s10, 1e-3) if np.isfinite(s90 - s10) else 0
            r_start = first(rprog, 0.1)
            win = (tt >= r_start) & (tt <= (s90 if np.isfinite(s90) else tt[-1]))
            slow = (sp < 0.15 * avg) & win
            stall, k0 = 0.0, None
            for q in range(len(slow)):
                if slow[q] and k0 is None:
                    k0 = q
                if (not slow[q] or q == len(slow) - 1) and k0 is not None:
                    stall = max(stall, tt[q] - tt[k0]); k0 = None
            out.append(dict(t=t[a], total=total, delay=(s50 - r50) * 1000, stall=stall * 1000,
                            peak=sp[win].max() if win.any() else 0, sweep=(s90 - s10) * 1000))
            i = b + 1
        else:
            i += 3
    return out


def main():
    for out in sys.argv[1:]:
        for rec in sorted(os.listdir(out)):
            f = os.path.join(out, rec, "display.csv")
            if not os.path.exists(f):
                continue
            d = pd.read_csv(f)
            d = d[d.shown == 1]
            res = turns(d)
            if not res:
                print(f"{os.path.basename(out.rstrip('/')):12s} {rec}: no turns"); continue
            r = pd.DataFrame(res)
            print(f"{os.path.basename(out.rstrip('/')):12s} {rec}: {len(r)} turns | delay med {r.delay.median():4.0f} ms | "
                  f"stall med {r.stall.median():4.0f} max {r.stall.max():4.0f} ms | peak med {r.peak.median():5.0f} deg/s | "
                  f"sweep med {r.sweep.median():4.0f} ms")


if __name__ == "__main__":
    main()
