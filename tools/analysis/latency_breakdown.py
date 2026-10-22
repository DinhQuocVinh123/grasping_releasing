"""Splits the glove hand's latency into stages, from one glove_latency_*.csv file (turn on Log Diagnostics on
FingerUDPReceiver; needs a run_glove_quest_stream.py that sends the "tm:" field).

One row = one image with a result. On the headset (Quest clock): exposure -> image requested (cam) -> GPU returns the image (rb)
-> wait for the worker thread (wq) -> JPEG encode (enc) -> write to the socket (send) -> result back on the headset (rtt) -> taken
by the virtual hand (pick). On the PC (PC clock, durations only): image complete -> result sent (py_pc = decode + model + the rest).

rtt - py_pc = Wi-Fi out + Wi-Fi back + the time the image QUEUED waiting for Python. The two parts are NOT separated using the images
Python had to wait for (py_blocked = 1): only ~6% of images, almost all at app start / during Wi-Fi hiccups, which gave a wrong Wi-Fi
time (the stages summed to 304 ms against 208 ms measured directly). Instead: Python takes the NEWEST image, so that image waited on
average about half the interval between two headset sends (when Python is slower than the headset) -> the rest is Wi-Fi (estimate).

Not included: the smoothing filter after the virtual hand takes the result + 1 frame to draw it -- see tools/analysis/latency_diag.py
(total virtual-hand latency) on the glove_diag file of the same session. The printed report is in Vietnamese.

Usage:
  python tools/analysis/latency_breakdown.py glove_latency_20261020_xxxxxx.csv
"""
import csv
import statistics
import sys


def pct(values, p):
    v = sorted(values)
    return v[min(len(v) - 1, max(0, int(round(p / 100 * (len(v) - 1)))))] if v else float("nan")


def main(path):
    with open(path, newline="", encoding="utf-8") as f:
        rows = []
        for r in csv.DictReader(f):
            try:
                rows.append({k: float(v) for k, v in r.items() if v not in ("", None)})
            except ValueError:
                continue  # last line cut off when the app was closed
    rows = [r for r in rows if r.get("py_pc_ms", -1) >= 0 and r.get("rtt_ms", -1) >= 0 and r.get("rb_ms", -1) >= 0]
    if len(rows) < 30:
        sys.exit(f"Chi co {len(rows)} anh du so lieu -- can Python ban 20/10 (gui 'tm:') va tay trong khung camera.")
    report(path, rows)


def report(path, rows):
    dur = rows[-1]["arr_s"] - rows[0]["arr_s"]
    sent = rows[-1]["fid"] - rows[0]["fid"] + 1
    print(f"{path}: {dur:.0f} s, kinh gui {sent / dur:.1f} anh/s, co ket qua {len(rows) / dur:.1f} anh/s, "
          f"JPEG trung vi {statistics.median(r['jpeg_kb'] for r in rows):.0f} KB")

    for r in rows:
        r["net_q"] = r["rtt_ms"] - r["py_pc_ms"]
    queued = [r for r in rows if r["py_blocked"] == 0]
    send_interval = dur / max(sent - 1, 1) * 1000.0

    stages = [
        ("Camera phoi sang -> Unity xin anh", "cam_ms"),
        ("GPU tra anh cho Unity", "rb_ms"),
        ("Cho luong nen ranh", "wq_ms"),
        ("Nen JPEG tren kinh", "enc_ms"),
        ("Ghi anh vao socket", "send_ms"),
        ("WiFi di + ve + XEP HANG cho Python ranh", "net_q"),
        ("Python giai nen JPEG", "py_decode_ms"),
        ("Python chay model", "py_infer_ms"),
        ("Python phan con lai (cat anh, kiem tra, tinh goc)", "py_other_ms"),
        ("Unity lay ket qua (khung hinh ke tiep)", "pick_ms"),
    ]
    print("\n  Khau                                              trung vi    p90   (ms, tung anh)")
    meds = []
    for name, key in stages:
        vals = [r[key] for r in rows if r.get(key, -1) >= 0 and r[key] == r[key]]
        if not vals:
            print(f"    {name:48s}   (khong co so lieu)")
            continue
        m = statistics.median(vals)
        meds.append((m, name))
        print(f"    {name:48s} {m:7.1f} {pct(vals, 90):7.1f}")
    total = [r["total_ms"] for r in rows if r.get("total_ms", -1) >= 0]
    if total:
        print(f"    {'TONG phoi sang -> Unity lay ket qua':48s} {statistics.median(total):7.1f} {pct(total, 90):7.1f}"
              f"   (cong cac trung vi: {sum(m for m, _ in meds):.0f})")
    if len(queued) / len(rows) > 0.5:
        q_est = send_interval / 2
        print(f"    -> trong do xep hang uoc ~{q_est:.0f} ms (nua khoang cach 2 anh kinh gui, {send_interval:.0f} ms), "
              f"WiFi uoc ~{statistics.median(r['net_q'] for r in rows) - q_est:.0f} ms")

    print("\n  Python (moi vong lap):")
    print(f"    vong lap dai trung vi {statistics.median(r['py_prev_cycle_ms'] for r in rows if r['py_prev_cycle_ms'] > 0):.0f} ms"
          f" (ve + hien cua so {statistics.median(r['py_prev_tail_ms'] for r in rows):.1f} ms)")
    print(f"    anh da xep hang san: {len(queued) / len(rows) * 100:.0f}%, vut bo anh cu: "
          f"{sum(r['py_dropped'] for r in rows):.0f} anh, chay model > 1 lan/anh: "
          f"{sum(1 for r in rows if r['py_ninfer'] > 1) / len(rows) * 100:.0f}%")
    xfer = [r["py_xfer_ms"] for r in rows if r["py_xfer_ms"] >= 0]
    if xfer:
        print(f"    truyen 1 anh qua WiFi (tu goi dau toi du anh): trung vi {statistics.median(xfer):.1f} ms ({len(xfer)} anh)")

    print("\n  Lon nhat:")
    for m, name in sorted(meds, reverse=True)[:3]:
        print(f"    {m:6.1f} ms  {name}")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    main(sys.argv[1])
