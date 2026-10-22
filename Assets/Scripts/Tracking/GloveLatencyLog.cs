using System;
using UnityEngine;

/// <summary>
/// Per-stage latency log of the glove hand: one row of glove_latency_*.csv per image result
/// (Application.persistentDataPath), read by tools/analysis/latency_breakdown.py.
///
/// Used by FingerUDPReceiver when its _logDiagnostics is on:
///   - OnPacket (receive thread): a result packet arrived, with Python's per-stage times ("tm:" field);
///   - OnPicked (main thread): the virtual hand took that result for the first time;
///   - Write (main thread, every frame): joins both with the headset timestamps (GloveLiveStreamer.TryGetFrameTimings)
///     and writes the rows that are complete.
/// </summary>
public sealed class GloveLatencyLog
{
    private struct PacketTiming { public int Fid; public double Arrive; public string Py; }

    private const int PickHistory = 64;
    private const int PyTimingFields = 11; // number of fields in "tm:" (run_glove_quest_stream.py)

    private readonly System.Collections.Concurrent.ConcurrentQueue<PacketTiming> _timingQueue =
        new System.Collections.Concurrent.ConcurrentQueue<PacketTiming>();
    private readonly System.Collections.Generic.List<PacketTiming> _pendingTimings = new System.Collections.Generic.List<PacketTiming>();
    private readonly int[] _pickFid = new int[PickHistory];
    private readonly double[] _pickTime = new double[PickHistory];
    private int _lastPickFid;
    private System.IO.StreamWriter _latencyLog;

    /// <summary>A result packet for image fid arrived at `arrive` (GloveLiveStreamer.ClockNow) with Python's "tm:" field.
    /// Safe to call from the receive thread.</summary>
    public void OnPacket(int fid, double arrive, string pyTimings) =>
        _timingQueue.Enqueue(new PacketTiming { Fid = fid, Arrive = arrive, Py = pyTimings });

    /// <summary>The virtual hand is taking the result of image fid; only the first time per image is recorded (pick_ms column).</summary>
    public void OnPicked(int fid)
    {
        if (fid == _lastPickFid) return;
        _lastPickFid = fid;
        _pickFid[fid % PickHistory] = fid;
        _pickTime[fid % PickHistory] = GloveLiveStreamer.ClockNow;
    }

    /// <summary>Writes one row per image result, once the virtual hand has taken it (or 0.5 s after it arrived without being taken --
    /// a newer image came first, pick_ms = -1). Each column is the ms between two consecutive stamps:
    /// exposure -> image requested (cam) -> GPU returns the image (rb) -> worker thread starts encoding (wq) -> encoded (enc) -> sent (send)
    /// -> result back on the headset (rtt; includes Wi-Fi out + queueing + Python + Wi-Fi back) -> taken by the virtual hand (pick).</summary>
    public void Write(GloveLiveStreamer streamer, UnityEngine.Object context)
    {
        while (_timingQueue.TryDequeue(out PacketTiming p)) _pendingTimings.Add(p);
        if (_pendingTimings.Count == 0) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (_latencyLog == null)
        {
            string path = System.IO.Path.Combine(Application.persistentDataPath,
                "glove_latency_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
            _latencyLog = new System.IO.StreamWriter(path, false, new System.Text.UTF8Encoding(false));
            _latencyLog.WriteLine("fid,arr_s,jpeg_kb,cam_ms,rb_ms,wq_ms,enc_ms,send_ms,rtt_ms," +
                                  "py_blocked,py_wait_ms,py_xfer_ms,py_decode_ms,py_infer_ms,py_ninfer,py_other_ms,py_pc_ms," +
                                  "py_prev_cycle_ms,py_prev_tail_ms,py_dropped,pick_ms,total_ms");
            Debug.Log($"[FingerUDPReceiver] Writing per-stage latency to {path}", context);
        }
        double now = GloveLiveStreamer.ClockNow;
        var sb = new System.Text.StringBuilder(256);
        int keep = 0;
        for (int i = 0; i < _pendingTimings.Count; i++)
        {
            PacketTiming p = _pendingTimings[i];
            int ps = p.Fid % PickHistory;
            bool picked = _pickFid[ps] == p.Fid;
            if (!picked && now - p.Arrive < 0.5)
            {
                _pendingTimings[keep++] = p; // wait for the virtual hand to take this result
                continue;
            }
            GloveLiveStreamer.FrameTimings t = default;
            bool ok = streamer != null && streamer.TryGetFrameTimings(p.Fid, out t);
            string Ms(double from, double to) => ok && from > 0 && to > 0 ? ((to - from) * 1000.0).ToString("F1", inv) : "-1";
            sb.Clear();
            sb.Append(p.Fid).Append(',').Append(p.Arrive.ToString("F4", inv))
              .Append(',').Append(ok ? (t.JpegBytes / 1024f).ToString("F1", inv) : "-1")
              .Append(',').Append(ok ? t.CameraAgeMs.ToString("F1", inv) : "-1")
              .Append(',').Append(Ms(t.Capture, t.Readback))
              .Append(',').Append(Ms(t.Readback, t.EncodeStart))
              .Append(',').Append(Ms(t.EncodeStart, t.EncodeEnd))
              .Append(',').Append(Ms(t.EncodeEnd, t.Sent))
              .Append(',').Append(Ms(t.Sent, p.Arrive));
            string[] py = p.Py.Split('|');
            for (int k = 0; k < PyTimingFields; k++) sb.Append(',').Append(k < py.Length ? py[k] : "");
            sb.Append(',').Append(picked ? Ms(p.Arrive, _pickTime[ps]) : "-1");
            sb.Append(',').Append(picked && ok && t.CameraAgeMs >= 0f
                ? (t.CameraAgeMs + (_pickTime[ps] - t.Capture) * 1000.0).ToString("F1", inv) : "-1");
            _latencyLog.WriteLine(sb);
        }
        _pendingTimings.RemoveRange(keep, _pendingTimings.Count - keep);
        if (Time.frameCount % 72 == 0) _latencyLog.Flush();
    }

    /// <summary>Closes the file; the next Write opens a new one.</summary>
    public void Close()
    {
        _latencyLog?.Dispose();
        _latencyLog = null;
    }
}
