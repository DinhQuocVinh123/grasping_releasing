"""Draws GloveReplayLab's output over the recorded camera images.

For each frame with a mesh (mesh_XXXXX.bin): the camera image cropped around the hand with
  - the shown virtual hand (skinned mesh, semi-transparent) as the headset would draw it,
  - the model's 9 points (white wrist, orange thumb, magenta index) and the raw solve's 9 joints (cyan),
  - a second view of the same 3D hand from the side (camera turned 90 degrees around the hand), where wrong depth or
    tilt is obvious even when the camera view looks right.

Usage:
  python replay_lab_render.py <lab_out_dir> <recording_dir> [--every N] [--sheet sheet.jpg] [--frames 10,20,...]
"""
import argparse
import csv
import math
import os
import struct
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

FX = FY = 866.0
CX, CY = 635.5, 637.0
THUMB = (255, 150, 30)
INDEX = (230, 60, 230)
RAW = (40, 220, 255)
HAND = np.array([120, 190, 255], dtype=float)


def load_tris(out_dir):
    data = open(os.path.join(out_dir, "mesh_tris.bin"), "rb").read()
    return np.frombuffer(data, dtype="<i4").reshape(-1, 3)


def load_mesh(path, n_verts):
    a = np.frombuffer(open(path, "rb").read(), dtype="<f4").reshape(-1, 3)
    return a[:n_verts], a[n_verts:]


def project(p, fx=FX, fy=FY, cx=CX, cy=CY):
    z = np.maximum(p[..., 2], 1e-3)
    return np.stack([cx + p[..., 0] / z * fx, cy - p[..., 1] / z * fy], axis=-1)


def draw_mesh(img, verts, tris, view_rot=None, view_pos=None, fx=FX, fy=FY, cx=CX, cy=CY, alpha=0.55):
    """Painter's-algorithm render of the mesh (verts in camera coords) onto img (PIL RGB), flat shaded."""
    v = verts if view_rot is None else (verts - view_pos) @ view_rot.T
    pts = project(v, fx, fy, cx, cy)
    tv = v[tris]
    n = np.cross(tv[:, 1] - tv[:, 0], tv[:, 2] - tv[:, 0])
    n /= np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-12)
    centre = tv.mean(axis=1)
    view = centre / np.maximum(np.linalg.norm(centre, axis=1, keepdims=True), 1e-12)
    shade = 0.35 + 0.65 * np.abs((n * view).sum(axis=1))
    order = np.argsort(-centre[:, 2])
    layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    for t in order:
        if centre[t, 2] <= 0.02:
            continue
        c = tuple(int(x) for x in HAND * shade[t]) + (255,)
        p = pts[tris[t]]
        d.polygon([tuple(p[0]), tuple(p[1]), tuple(p[2])], fill=c)
    a = layer.split()[3].point(lambda x: int(x * alpha))
    layer.putalpha(a)
    out = img.convert("RGBA")
    out.alpha_composite(layer)
    return out.convert("RGB")


def draw_points(d, pts, conf, scale=1.0, raw=None):
    def ok(i):
        return conf is None or conf[i] >= 0.2

    if raw is not None:
        for a, b in [(0, 1), (1, 2), (2, 3), (3, 4), (0, 5), (5, 6), (6, 7), (7, 8)]:
            if raw[a][0] >= 0 and raw[b][0] >= 0:
                d.line([tuple(raw[a]), tuple(raw[b])], fill=RAW, width=max(1, int(2 * scale)))
    for a, b, col in [(0, 1, THUMB), (1, 2, THUMB), (2, 3, THUMB), (3, 4, THUMB),
                      (0, 5, INDEX), (5, 6, INDEX), (6, 7, INDEX), (7, 8, INDEX)]:
        if ok(a) and ok(b):
            d.line([tuple(pts[a]), tuple(pts[b])], fill=col, width=max(1, int(3 * scale)))
    for i in range(9):
        col = (255, 255, 255) if i == 0 else THUMB if i <= 4 else INDEX
        r = 5 * scale
        x, y = pts[i]
        if ok(i):
            d.ellipse([x - r, y - r, x + r, y + r], fill=col, outline=(0, 0, 0))
        else:
            d.ellipse([x - r, y - r, x + r, y + r], outline=col, width=2)


def side_view(verts, bones, tris, size):
    """The same hand seen from the right side of the hand (camera rotated 90 degrees about the vertical axis through the
    hand centre), on a grey grid: shows depth along the camera axis horizontally."""
    centre = bones.mean(axis=0)
    dist = 0.45
    # View camera: looks at the centre from the +X side, so camera-depth (Z) runs left -> right
    pos = np.array([centre[0] + dist, centre[1], centre[2]])
    fwd = centre - pos
    fwd /= np.linalg.norm(fwd)
    up = np.array([0.0, 1.0, 0.0])
    right = np.cross(up, fwd)
    right /= np.linalg.norm(right)
    up = np.cross(fwd, right)
    rot = np.stack([right, up, fwd])
    img = Image.new("RGB", (size, size), (52, 56, 62))
    d = ImageDraw.Draw(img)
    f = size * 1.6
    c = size / 2
    # grid: 5 cm lines in the camera-depth / height plane through the hand centre
    for k in range(-6, 7):
        for (a0, a1) in [((k * 0.05, -0.3), (k * 0.05, 0.3)), ((-0.3, k * 0.05), (0.3, k * 0.05))]:
            p0 = centre + np.array([0, a0[1], a0[0]])
            p1 = centre + np.array([0, a1[1], a1[0]])
            q = project((np.stack([p0, p1]) - pos) @ rot.T, f, f, c, c)
            d.line([tuple(q[0]), tuple(q[1])], fill=(70, 75, 82), width=1)
    # camera direction arrow (the real camera is to the left, looking right)
    d.text((8, 8), "side view  (camera ->)", fill=(220, 220, 220))
    return draw_mesh(img, verts, tris, rot, pos, f, f, c, c, alpha=0.9)


def crop_box(points, verts2d, pad=60, min_size=360):
    xs = np.concatenate([points[:, 0], verts2d[:, 0]])
    ys = np.concatenate([points[:, 1], verts2d[:, 1]])
    x0, x1 = np.percentile(xs, 1) - pad, np.percentile(xs, 99) + pad
    y0, y1 = np.percentile(ys, 1) - pad, np.percentile(ys, 99) + pad
    s = max(x1 - x0, y1 - y0, min_size)
    cx_, cy_ = (x0 + x1) / 2, (y0 + y1) / 2
    return (int(cx_ - s / 2), int(cy_ - s / 2), int(cx_ + s / 2), int(cy_ + s / 2))


def render_frame(out_dir, rec_dir, row, tris, n_verts, panel=480):
    fid = int(row["frame"])
    mesh_path = os.path.join(out_dir, f"mesh_{fid:05d}.bin")
    img_path = os.path.join(rec_dir, f"{fid:05d}.jpg")
    if not os.path.exists(mesh_path) or not os.path.exists(img_path):
        return None
    verts, bones = load_mesh(mesh_path, n_verts)
    img = Image.open(img_path).convert("RGB")
    pts = np.array([[float(row[f"u{i}"]), float(row[f"v{i}"])] for i in range(9)])
    conf = [float(row[f"c{i}"]) for i in range(9)]
    raw = [[float(row[f"ru{i}"]), float(row[f"rv{i}"])] for i in range(9)]
    ok = np.array(conf) >= 0.2
    box = crop_box(pts[ok] if ok.sum() >= 3 else pts, np.zeros((0, 2)), pad=140)  # from the model's points: same crop for any solver
    plain = img.crop(box).resize((panel, panel))
    over = draw_mesh(img, verts, tris)
    d = ImageDraw.Draw(over)
    s = (box[2] - box[0]) / panel
    draw_points(d, pts, conf, scale=s, raw=raw)
    over = over.crop(box).resize((panel, panel))
    side = side_view(verts, bones, tris, panel)
    sheet = Image.new("RGB", (panel * 3, panel + 26), (20, 20, 20))
    sheet.paste(plain, (0, 26))
    sheet.paste(over, (panel, 26))
    sheet.paste(side, (2 * panel, 26))
    d = ImageDraw.Draw(sheet)
    state = {"0": "ok", "1": "partly out", "2": "LOST"}.get(row["state"], row["state"])
    d.text((6, 6), f"frame {fid}  t={float(row['t']):.2f}s  image err {float(row['serr']):.3f}  trust {float(row['trust']):.2f}  "
                   f"depth {float(row['depth'])*100:.0f}cm (Quest {float(row['zprior'])*100:.0f})  {state}  "
                   f"switches {row['switches']}", fill=(235, 235, 235))
    return sheet


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out_dir")
    ap.add_argument("rec_dir")
    ap.add_argument("--frames", default="")
    ap.add_argument("--dest", default=None)
    ap.add_argument("--panel", type=int, default=480)
    ap.add_argument("--grid", default=None, help="also tile all rendered frames (camera overlay + side view) into this image")
    ap.add_argument("--cols", type=int, default=4)
    ap.add_argument("--skip-lost", action="store_true", help="skip frames where the hand counts as not seen")
    a = ap.parse_args()
    tris = load_tris(a.out_dir)
    n_verts = int(tris.max()) + 1
    rows = list(csv.DictReader(open(os.path.join(a.out_dir, "frames.csv"))))
    want = set(int(x) for x in a.frames.split(",") if x) if a.frames else None
    dest = a.dest or os.path.join(a.out_dir, "render")
    os.makedirs(dest, exist_ok=True)
    n = 0
    tiles = []
    for row in rows:
        fid = int(row["frame"])
        if want is not None and fid not in want:
            continue
        if a.skip_lost and row["state"] == "2":
            continue
        sheet = render_frame(a.out_dir, a.rec_dir, row, tris, n_verts, a.panel)
        if sheet is None:
            continue
        sheet.save(os.path.join(dest, f"f{fid:05d}.jpg"), quality=88)
        tiles.append(sheet.crop((a.panel, 0, 3 * a.panel, a.panel + 26)))
        n += 1
    if a.grid and tiles:
        w, h = tiles[0].size
        rows_n = (len(tiles) + a.cols - 1) // a.cols
        g = Image.new("RGB", (w * a.cols, h * rows_n), (0, 0, 0))
        for k, t in enumerate(tiles):
            g.paste(t, ((k % a.cols) * w, (k // a.cols) * h))
        g.save(a.grid, quality=85)
    print(f"{n} frames -> {dest}")


if __name__ == "__main__":
    main()
