"""Jitter vs lag of the shown virtual hand in replay lab output (GloveReplayLab frames.csv).

Usage: python replay_lab_jitter.py <lab out dir> [<lab out dir> ...]
  (each dir = out/<set>; every recording sub-folder with a frames.csv is scored)

"Truth" is the raw solve smoothed with a 0.4 s centered median (it has no delay, unlike any real-time filter).
  still jitter : shown point minus its own 0.4 s median, while the hand is still (smoothed speed < 8 cm/s)
  still drift  : how far the shown point travels per second while the hand is still (what the eye sees as jitter)
  move error   : shown point minus the truth, while the hand moves (> 15 cm/s) -> mostly lag
Points: wrist (0), thumb tip (4), index tip (8), in mm.
"""
import os
import sys

import numpy as np
import pandas as pd

POINTS = {"wrist": 0, "thumb": 4, "index": 8}


def centered_median(x, t, w=0.4):
    out = np.empty_like(x)
    j0 = j1 = 0
    for i in range(len(t)):
        while t[j0] < t[i] - w / 2: j0 += 1
        while j1 < len(t) and t[j1] <= t[i] + w / 2: j1 += 1
        out[i] = np.median(x[j0:j1], axis=0)
    return out


def score(path):
    d = pd.read_csv(path)
    d = d[(d.solved == 1) & (d.sx0.abs() + d.sz0.abs() > 0)]
    t = d.t.values
    res = {}
    raw0 = d[["rx0", "ry0", "rz0"]].values
    truth0 = centered_median(raw0, t)
    speed = np.r_[0, np.linalg.norm(np.diff(truth0, axis=0), axis=1) / np.maximum(np.diff(t), 1e-3)]
    still, moving = speed < float(os.environ.get("STILL", "0.08")), speed > 0.15
    for name, i in POINTS.items():
        raw = d[[f"rx{i}", f"ry{i}", f"rz{i}"]].values
        shown = d[[f"sx{i}", f"sy{i}", f"sz{i}"]].values
        truth = centered_median(raw, t)
        jit = np.linalg.norm(shown - centered_median(shown, t), axis=1)[still] * 1000
        rjit = np.linalg.norm(raw - truth, axis=1)[still] * 1000
        err = np.linalg.norm(shown - truth, axis=1)[moving] * 1000
        both = still[1:] & still[:-1]
        dt = np.diff(t)[both]
        drift = np.linalg.norm(np.diff(shown, axis=0), axis=1)[both].sum() / dt.sum() * 1000
        rdrift = np.linalg.norm(np.diff(raw, axis=0), axis=1)[both].sum() / dt.sum() * 1000
        res[name] = (np.median(jit), np.percentile(jit, 90), drift, rdrift,
                     np.median(err), np.percentile(err, 90))
    return res, still.mean(), moving.mean()


def main():
    for out in sys.argv[1:]:
        for rec in sorted(os.listdir(out)):
            f = os.path.join(out, rec, "frames.csv")
            if not os.path.exists(f): continue
            res, st, mv = score(f)
            line = f"{os.path.basename(out.rstrip('/')):14s} {rec}  still {st*100:3.0f}% move {mv*100:3.0f}% |"
            for name, (jm, j9, dr, rdr, em, e9) in res.items():
                line += f" {name} jit {jm:3.1f}/{j9:3.1f} drift {dr:4.0f} (raw {rdr:3.0f}) move {em:4.1f}/{e9:4.1f} |"
            print(line)
    print("jit, move: median/p90 mm; drift: mm/s")


if __name__ == "__main__":
    main()
