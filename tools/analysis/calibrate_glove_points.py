"""Measures where the glove model's 9 points lie relative to the virtual hand's joints (HandImageFit.PointOffset).

The model finds the points on the glove's plastic rings, not on the finger bones. A recording with both passthrough cameras
(keypoints.jsonl with kpts2 / scores2, 29/10) gives each point's real 3D position (triangulated); GloveReplayLab's frames.csv
of the same recording gives the virtual joints of each solve (bx/by/bz, wrist rotation qx..qw, size fscale). The offset of each
point in its joint's frame (see HandImageFit.PointFromJoints) is the median over all images.

Run it in rounds: replay with -labSets "c:UseSecondView=true;PointOffset=<previous result>", calibrate, repeat until the
offsets stop changing (the solve moves the joints a little each round). Point 5 stays fixed (IndexMcpPointLocal): it anchors the
hand; moving every point together would only move the virtual hand away from the glove.

Usage:
  python calibrate_glove_points.py <recording_dir> <lab_out_dir> [--wrist-down 0.0376] [--palm-scale 1]
Prints the offsets as a GloveReplayLab -labSets value and as C# for HandImageFit.PointOffset.
"""
import argparse
import csv
import json
import os

import numpy as np
from scipy.optimize import least_squares

F = 866.0
C_MAIN = np.array([635.5, 637.0])   # right camera (kpts)
C_SECOND = np.array([642.1, 640.5])  # left camera (kpts2)
BASELINE = 0.0638
CALIBRATED = [0, 1, 2, 3, 4, 6, 7, 8]


def rays(px, c):  # camera coordinates, y up (as GloveReplayLab)
    return np.stack([(px[:, 0] - c[0]) / F, -(px[:, 1] - c[1]) / F, np.ones(len(px))], 1)


def rotvec(v):
    th = np.linalg.norm(v)
    if th < 1e-12:
        return np.eye(3)
    k = v / th
    K = np.array([[0, -k[2], k[1]], [k[2], 0, -k[0]], [-k[1], k[0], 0]])
    return np.eye(3) + np.sin(th) * K + (1 - np.cos(th)) * K @ K


def load_pairs(rec):
    kp = {}
    for line in open(os.path.join(rec, "keypoints.jsonl")):
        j = json.loads(line)
        if j.get("kpts2") and j.get("kpts"):
            kp[j["frame"]] = (np.array(j["kpts"][:9]), np.array(j["scores"][:9]), np.array(j["kpts2"][:9]), np.array(j["scores2"][:9]))
    return kp


def calibrate_stereo(kp):
    """Relative pose of the second camera from the point pairs themselves (epipolar constraint, baseline fixed)."""
    R_, L_ = [], []
    for a, sa, b, sb in kp.values():
        m = (sa > 0.6) & (sb > 0.6)
        R_.append(rays(a[m], C_MAIN))
        L_.append(rays(b[m], C_SECOND))
    R_, L_ = np.concatenate(R_), np.concatenate(L_)

    def resid(x):
        t = np.array([-1.0, x[3], x[4]])
        t /= np.linalg.norm(t)
        n = np.cross(L_ @ rotvec(x[:3]).T, R_)
        return (n @ t) / (np.linalg.norm(L_, axis=1) * np.linalg.norm(R_, axis=1))

    x = least_squares(resid, np.zeros(5), loss="huber", f_scale=0.005).x
    t = np.array([-1.0, x[3], x[4]])
    return rotvec(x[:3]), t / np.linalg.norm(t) * BASELINE


def triangulate(r, l, Rm, t):
    l = Rm @ l
    A = np.array([[r @ r, -r @ l], [r @ l, -l @ l]])
    s, u = np.linalg.solve(A, np.array([r @ t, l @ t]))
    p1, p2 = s * r, t + u * l
    return (p1 + p2) / 2, np.linalg.norm(p1 - p2)


def quat_rotate(q, v):  # Unity Quaternion * Vector3, q = (x, y, z, w)
    u, w = np.array(q[:3]), q[3]
    return v + 2 * np.cross(u, np.cross(u, v) + w * v)


def quat_inverse(q):
    return np.array([-q[0], -q[1], -q[2], q[3]])


def finger_frame(joints, i, q):
    """Same frame as HandImageFit.PointFromJoints: x along the bone ending at joint i, y the bend axis, z = x cross y."""
    along = joints[i] - joints[i - 1]
    along /= np.linalg.norm(along)
    hinge = quat_rotate(q, np.array([0.0, 1.0, 0.0]) if i < 5 else np.array([1.0, 0.0, 0.0]))
    side = hinge - along * (along @ hinge)
    side /= np.linalg.norm(side)
    return np.stack([along, side, np.cross(along, side)])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("rec")
    ap.add_argument("lab")
    ap.add_argument("--wrist-down", type=float, default=0.0376, help="ImageHandSolver _wristPointDown (m)")
    ap.add_argument("--palm-scale", type=float, default=1.0, help="HandImageFit.PalmScale")
    ap.add_argument("--max-serr", type=float, default=0.12, help="skip solves that fit the image worse than this")
    a = ap.parse_args()

    kp = load_pairs(a.rec)
    Rm, t = calibrate_stereo(kp)
    wrist_point = np.array([0.0, 0.0, -a.wrist_down])
    samples = {i: [] for i in CALIBRATED}
    used = 0
    for row in csv.DictReader(open(os.path.join(a.lab, "frames.csv"))):
        f = int(row["frame"])
        if f not in kp or row["solved"] != "1" or row.get("bx0", "") == "" or float(row["serr"]) > a.max_serr:
            continue
        pa, sa, pb, sb = kp[f]
        q = np.array([float(row[k]) for k in ("qx", "qy", "qz", "qw")])
        qi = quat_inverse(q)
        scale = float(row["fscale"])
        joints = np.array([[float(row[f"bx{i}"]), float(row[f"by{i}"]), float(row[f"bz{i}"])] for i in range(9)])
        used += 1
        for i in CALIBRATED:
            if sa[i] <= 0.6 or sb[i] <= 0.6:
                continue
            p, gap = triangulate(rays(pa[i:i + 1], C_MAIN)[0], rays(pb[i:i + 1], C_SECOND)[0], Rm, t)
            if gap > 0.006 or not 0.1 < p[2] < 0.8:
                continue
            d = p - joints[i]
            if i in (0, 1):
                o = quat_rotate(qi, d) / (scale * a.palm_scale)
                if i == 0:
                    o -= wrist_point
            else:
                o = finger_frame(joints, i, q) @ d / scale
            samples[i].append(o)

    names = ["wrist (cuff)", "thumb base", "thumb 2", "thumb 3", "thumb tip", "index base", "index middle", "index distal", "index tip"]
    print(f"{used} solved images with both cameras")
    parts, cs = [], []
    for i in CALIBRATED:
        s = np.array(samples[i])
        if len(s) < 20:
            print(f"  {i} {names[i]:13s}: only {len(s)} samples, left at 0")
            continue
        m = np.median(s, 0)
        spread = np.median(np.abs(s - m), 0)
        print(f"  {i} {names[i]:13s}: offset {m[0] * 100:+5.2f} {m[1] * 100:+5.2f} {m[2] * 100:+5.2f} cm"
              f"  (spread {spread[0] * 100:.2f} {spread[1] * 100:.2f} {spread[2] * 100:.2f}, n={len(s)})")
        parts.append(f"{i}:{m[0]:.4f},{m[1]:.4f},{m[2]:.4f}")
        cs.append(f"[{i}] = new Vector3({m[0]:.4f}f, {m[1]:.4f}f, {m[2]:.4f}f)")
    print("labSets: PointOffset=" + "/".join(parts))
    print("C#:      " + ", ".join(cs))


if __name__ == "__main__":
    main()
