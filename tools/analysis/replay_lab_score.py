"""Scores GloveReplayLab runs with one yardstick, so changes can be compared without the headset.

  python replay_lab_score.py <lab_out_root> [recordings_root]

For every <set>/<recording>/frames.csv under lab_out_root, prints:
  follow  - shown hand vs the model's points (palm lengths on the image) on frames where the glove is clearly in view
            (mean confidence of the 9 points >= 0.6): median / 90th percentile / share of frames above 0.2
  lost    - share of those glove frames where the hand counts as not seen (the headset shows the Quest hand instead)
  ghost   - share of frames WITHOUT a glove (mean confidence < 0.45) where the shown hand still moves (> 1.5 cm or > 10 degrees
            since the previous image): it is being driven by rubbish
  jumps   - shown wrist rotation steps > 25 degrees between two images, per minute
  3D      - only for recordings with the second camera (kpts2): the model's points seen by both cameras are triangulated
            (= the real glove in 3D); mean 3D distance raw / shown (cm), depth bias of the raw solve (cm, - = too close),
            palm direction error (degrees, median / 90th percentile)
"""
import csv
import json
import os
import sys

import numpy as np

F = 866.0
CR = np.array([635.5, 637.0])  # right (main) camera principal point at 1280 px
CL = np.array([642.1, 640.5])  # left (second) camera
B = 0.0638


def rays(px, c):
    return np.stack([(px[:, 0] - c[0]) / F, -(px[:, 1] - c[1]) / F, np.ones(len(px))], 1)


def rotvec(v):
    th = np.linalg.norm(v)
    if th < 1e-12:
        return np.eye(3)
    k = v / th
    K = np.array([[0, -k[2], k[1]], [k[2], 0, -k[0]], [-k[1], k[0], 0]])
    return np.eye(3) + np.sin(th) * K + (1 - np.cos(th)) * K @ K


_stereo_cache = {}


def stereo_truth(rec_dir):
    """frame -> (9,3) triangulated points (nan where not seen by both cameras)."""
    if rec_dir in _stereo_cache:
        return _stereo_cache[rec_dir]
    from scipy.optimize import least_squares
    kp = {}
    for line in open(os.path.join(rec_dir, "keypoints.jsonl")):
        j = json.loads(line)
        if j.get("kpts2") and j.get("kpts"):
            kp[j["frame"]] = (np.array(j["kpts"][:9]), np.array(j["scores"][:9]),
                              np.array(j["kpts2"][:9]), np.array(j["scores2"][:9]))
    truth = {}
    if kp:
        R_, L_ = [], []
        for a, sa, b, sb in kp.values():
            m = (sa > 0.6) & (sb > 0.6)
            R_.append(rays(a[m], CR))
            L_.append(rays(b[m], CL))
        R_, L_ = np.concatenate(R_), np.concatenate(L_)

        def resid(x):
            t = np.array([-1.0, x[3], x[4]])
            t /= np.linalg.norm(t)
            l = L_ @ rotvec(x[:3]).T
            return (np.cross(l, R_) @ t) / (np.linalg.norm(l, axis=1) * np.linalg.norm(R_, axis=1))

        x = least_squares(resid, np.zeros(5), loss="huber", f_scale=0.005).x
        Rm = rotvec(x[:3])
        t = np.array([-1.0, x[3], x[4]])
        t = t / np.linalg.norm(t) * B
        for f, (a, sa, b, sb) in kp.items():
            T = np.full((9, 3), np.nan)
            ra, lb = rays(a, CR), rays(b, CL) @ Rm.T
            for i in range(9):
                if sa[i] <= 0.5 or sb[i] <= 0.5:
                    continue
                r, l = ra[i], lb[i]
                A = np.array([[r @ r, -r @ l], [r @ l, -l @ l]])
                s, u = np.linalg.solve(A, np.array([r @ t, l @ t]))
                p1, p2 = s * r, t + u * l
                p = (p1 + p2) / 2
                if np.linalg.norm(p1 - p2) < 0.01 and 0.1 < p[2] < 0.8:
                    T[i] = p
            truth[f] = T
    _stereo_cache[rec_dir] = truth
    return truth


def normal(P):
    n = np.cross(P[1] - P[0], P[5] - P[0])
    return n / np.linalg.norm(n)


def qang(a, b):
    return np.degrees(2 * np.arccos(np.clip(np.abs((a * b).sum(-1)), 0, 1)))


def score(csv_path, rec_dir):
    rows = list(csv.DictReader(open(csv_path)))
    conf = np.array([np.mean([float(r[f"c{i}"]) for i in range(9)]) for r in rows])
    found = np.array([r["found"] == "1" for r in rows])
    se = np.array([float(r["shown_err"]) for r in rows])
    state = np.array([int(r["state"]) for r in rows])
    t = np.array([float(r["t"]) for r in rows])
    glove_all = found & (conf >= 0.6)
    glove = glove_all & (se >= 0) & (state != 2)
    nog = found & (conf < 0.45)
    out = {}
    v = se[glove]
    out["follow"] = f"{np.median(v):.3f}/{np.percentile(v, 90):.3f}/{np.mean(v > 0.2) * 100:4.1f}%" if len(v) else "-"
    out["lost"] = f"{np.mean(state[glove_all] == 2) * 100:4.1f}%" if glove_all.sum() else "-"
    dq = np.array([[float(r[k]) for k in ("dqx", "dqy", "dqz", "dqw")] for r in rows])
    steps = qang(dq[1:], dq[:-1])
    shown = state[1:] != 2
    minutes = max((t[-1] - t[0]) / 60, 1e-3)
    dp = np.array([[float(r[k]) for k in ("dpx", "dpy", "dpz")] for r in rows])
    moved = np.zeros(len(rows), bool)
    moved[1:] = ((np.linalg.norm(dp[1:] - dp[:-1], axis=1) > 0.015) | (steps > 10)) & shown
    out["ghost"] = f"{np.mean(moved[nog]) * 100:4.0f}%" if nog.sum() else "-"
    out["jumps"] = f"{np.sum((steps > 25) & shown) / minutes:5.1f}/min"
    truth = stereo_truth(rec_dir)
    if truth:
        e_raw, e_sh, dz, nr, ns = [], [], [], [], []
        for r in rows:
            f = int(r["frame"])
            T = truth.get(f)
            if T is None or r["solved"] != "1":
                continue
            ok = ~np.isnan(T[:, 0])
            if ok.sum() < 4:
                continue
            raw = np.array([[float(r[f"rx{i}"]), float(r[f"ry{i}"]), float(r[f"rz{i}"])] for i in range(9)])
            sh = np.array([[float(r[f"sx{i}"]), float(r[f"sy{i}"]), float(r[f"sz{i}"])] for i in range(9)])
            e_raw.append(np.linalg.norm(raw[ok] - T[ok], axis=1).mean())
            e_sh.append(np.linalg.norm(sh[ok] - T[ok], axis=1).mean())
            dz.append(np.mean(raw[ok, 2] - T[ok, 2]))
            if ok[0] and ok[1] and ok[5]:
                nt = normal(T)
                nr.append(np.degrees(np.arccos(np.clip(normal(raw) @ nt, -1, 1))))
                ns.append(np.degrees(np.arccos(np.clip(normal(sh) @ nt, -1, 1))))
        if e_raw and nr:
            e_raw, e_sh = np.array(e_raw) * 100, np.array(e_sh) * 100
            out["3D"] = (f"raw {np.median(e_raw):.1f}/{np.percentile(e_raw, 90):.1f}cm shown {np.median(e_sh):.1f}/"
                         f"{np.percentile(e_sh, 90):.1f}cm depth {np.median(dz) * 100:+.1f}cm palm dir raw "
                         f"{np.median(nr):.0f}/{np.percentile(nr, 90):.0f} shown {np.median(ns):.0f}/{np.percentile(ns, 90):.0f} deg (n={len(e_raw)})")
    return out


def main():
    root = sys.argv[1]
    recs = sys.argv[2] if len(sys.argv) > 2 else "D:/untitled folder 2/VR/recordings"
    for set_name in sorted(os.listdir(root)):
        sd = os.path.join(root, set_name)
        if not os.path.isdir(sd):
            continue
        for rec in sorted(os.listdir(sd)):
            p = os.path.join(sd, rec, "frames.csv")
            if not os.path.exists(p):
                continue
            s = score(p, os.path.join(recs, rec))
            print(f"{set_name:14s} {rec}  follow {s['follow']}  lost {s['lost']}  ghost {s['ghost']}  jumps {s['jumps']}  {s.get('3D', '')}")


if __name__ == "__main__":
    main()
