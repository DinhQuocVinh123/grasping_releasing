"""Chia do tre tay gang ra tung khau, tu 1 file glove_latency_*.csv (ban build tu 20/10, bat Log Diagnostics tren
FingerUDPReceiver; can run_glove_quest_stream.py ban 20/10 gui kem "tm:").

Moi dong = 1 anh co ket qua. Tren kinh (dong ho Quest): phoi sang -> xin anh (cam) -> GPU tra anh (rb) -> cho luong phu
(wq) -> nen JPEG (enc) -> ghi vao socket (send) -> ket qua ve toi kinh (rtt) -> tay ao lay ket qua (pick).
Ben may tinh (dong ho PC, chi dung khoang thoi gian): du anh -> gui ket qua (py_pc = giai nen + model + phan con lai).

rtt - py_pc = WiFi di + WiFi ve + thoi gian anh XEP HANG cho Python ranh. KHONG tach 2 phan bang anh Python phai cho
(py_blocked = 1): buoi 20/10 11:11 chi 6% anh nhu vay, va gan het roi vao luc mo app / luc WiFi khung -> WiFi ra 149 ms
(sai, tong cac khau 304 ms > tong do thang 208 ms). Thay vao do: Python lay anh MOI NHAT, nen anh do da nam cho trung
binh ~ nua khoang cach 2 anh kinh gui (khi Python cham hon kinh) -> phan con lai la WiFi (uoc luong).

Chua gom: bo loc lam muot sau khi tay ao lay ket qua + 1 khung ve len man hinh -- xem tools/latency_diag.py
(do tre tay ao tong) tren file glove_diag cung buoi.

Cach dung:
  python tools/latency_breakdown.py glove_latency_20261020_xxxxxx.csv
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
                continue  # dong cuoi bi cat khi tat app
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
