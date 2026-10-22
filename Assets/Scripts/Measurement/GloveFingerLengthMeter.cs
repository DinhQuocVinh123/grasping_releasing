using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Measures the thumb and index finger lengths WHILE WEARING THE GLOVE, using the glove system itself.
/// The lengths are inputs to the actuator force model F(P, L, L0, s).
///
/// For every image result from Python:
///   1. The glove model's 9 image points: wrist, thumb 1-4, index 5-8 (FingerUDPReceiver).
///   2. A ray from the camera AT CAPTURE TIME through each point (GloveLiveStreamer.TryGetFrameRay, which uses
///      the camera's focal length and pose).
///   3. Distance to the hand = the Quest's hand centre at capture time, along the camera's view axis
///      (independent of the model). ImageHandSolver's depth is NOT used: it places the wrist using the template
///      hand size, so it would just measure the template back.
///   4. Treat the hand as a flat plane facing the camera at that distance: ray-plane intersection -> 3D point;
///      the distance between neighbouring points = segment length. Only frames with all 9 points clear, away from
///      the image edge, and the finger STRAIGHT are used (a tilted or bent finger looks shorter).
///   5. The 90th percentile (p90) over the accepted frames is shown in the headset and written to
///      Application.persistentDataPath/glove_finger_lengths_*.csv (summarised by tools/analysis/glove_finger_lengths.py).
///      p90 because a tilted finger only ever looks SHORTER in the image, never longer.
///      Only the TOTAL (knuckle -> tip) is reliable: the glove hides the middle joints, so the split between
///      segments follows how the training images were labelled.
///      A relaxed thumb points diagonally towards the camera and reads about 10% short: rotate the hand so the
///      thumbnail faces the camera.
///
/// On the GLOVE: tip = end of the glove finger, joints = where the model places them on the glove surface.
/// The Quest's distance error with the glove on (a few cm) becomes the same percentage error in length:
/// compare with a ruler before using the numbers.
/// How to measure: glove on, hand in front of the face, back (or palm) of the hand facing the eyes, thumb and
/// index straight, hold for about 20 s.
/// </summary>
[DefaultExecutionOrder(450)] // after FingerUDPReceiver (0): ImageHandSolver has taken this frame's new image result
public class GloveFingerLengthMeter : MonoBehaviour
{
    [Tooltip("Empty = find in the scene.")]
    [SerializeField] private FingerUDPReceiver _receiver;
    [SerializeField] private GloveLiveStreamer _streamer;
    [Tooltip("Text in the headset showing the result. Empty = write the file only.")]
    [SerializeField] private TMPro.TextMeshPro _display;
    [SerializeField] private bool _logToFile = true;

    [Header("Only use good frames")]
    [Tooltip("Minimum confidence of EVERY point used (sent by the model).")]
    [SerializeField] private float _minConfidence = 0.5f;
    [Tooltip("Drop the frame if a point is closer than this to the image edge (fraction of the image): the model guesses there instead of seeing.")]
    [SerializeField] private float _edgeMargin = 0.05f;
    [Tooltip("A finger counts as straight when its segments differ in direction by less than this (degrees). A bent finger looks shorter.")]
    [SerializeField] private float _maxBendDegrees = 20f;
    [Tooltip("Valid distance to the hand (m).")]
    [SerializeField] private Vector2 _depthRange = new Vector2(0.15f, 0.9f);
    [SerializeField] private int _maxSamples = 2000;

    private const int Points = HandImageFit.Points; // 0 wrist, 1-4 thumb, 5-8 index
    private static readonly string[] Names =
    {
        "cai_dot1(MCP-IP)", "cai_dot2(IP-dau)", "cai_MCP-dau",
        "tro_dot1(MCP-PIP)", "tro_dot2(PIP-DIP)", "tro_dot3(DIP-dau)", "tro_MCP-dau",
    };

    private readonly Vector2[] _uv = new Vector2[Points];
    private readonly float[] _conf = new float[Points];
    private readonly Vector3[] _p = new Vector3[Points];
    private readonly List<float[]> _thumb = new List<float[]>(), _index = new List<float[]>();
    private int _lastFid, _seen;
    private System.IO.StreamWriter _log;
    private float _nextDisplay;

    private void Awake()
    {
        if (_receiver == null) _receiver = FindAnyObjectByType<FingerUDPReceiver>();
        if (_streamer == null) _streamer = FindAnyObjectByType<GloveLiveStreamer>();
    }

    private void OnDisable()
    {
        _log?.Dispose();
        _log = null;
    }

    private void LateUpdate()
    {
        if (_receiver != null && _streamer != null &&
            _receiver.TryGetPixelPoints(out int fid, _uv, _conf, out _, out _) && fid != _lastFid)
        {
            _lastFid = fid;
            _seen++;
            Measure(fid);
        }
        if (_display != null && Time.unscaledTime >= _nextDisplay)
        {
            _nextDisplay = Time.unscaledTime + 0.25f;
            _display.text = Summary();
        }
    }

    private void Measure(int fid)
    {
        for (int i = 0; i < Points; i++)
            if (_conf[i] < _minConfidence || _uv[i].x < _edgeMargin || _uv[i].x > 1f - _edgeMargin ||
                _uv[i].y < _edgeMargin || _uv[i].y > 1f - _edgeMargin) return;
        if (!_streamer.TryGetFramePose(fid, out Pose cam) || !_streamer.TryGetFrameHint(fid, out Vector3 center, out _)) return;

        // Hand plane: perpendicular to the camera's view axis, through the Quest's hand centre at capture time
        Vector3 fwd = cam.rotation * Vector3.forward;
        float z = Vector3.Dot(center - cam.position, fwd);
        if (z < _depthRange.x || z > _depthRange.y) return;
        for (int i = 0; i < Points; i++)
        {
            if (!_streamer.TryGetFrameRay(fid, _uv[i], out Ray ray)) return;
            float along = Vector3.Dot(ray.direction, fwd);
            if (along < 0.2f) return;
            _p[i] = ray.origin + ray.direction * (z / along);
        }

        // Thumb: 2 -> 3 -> 4 (knuckle -> joint near the tip -> tip); index: 5 -> 6 -> 7 -> 8
        float thumbBend = Bend(2, 3, 4), indexBend = Mathf.Max(Bend(5, 6, 7), Bend(6, 7, 8));
        bool thumbOk = thumbBend <= _maxBendDegrees, indexOk = indexBend <= _maxBendDegrees;
        float t1 = Mm(2, 3), t2 = Mm(3, 4), i1 = Mm(5, 6), i2 = Mm(6, 7), i3 = Mm(7, 8);
        if (thumbOk) Add(_thumb, new[] { t1, t2, t1 + t2 });
        if (indexOk) Add(_index, new[] { i1, i2, i3, i1 + i2 + i3 });
        if (_logToFile) WriteRow(fid, z, thumbBend, indexBend, thumbOk, indexOk, t1, t2, i1, i2, i3);
    }

    /// <summary>Change of direction (degrees) between segment a->b and b->c: 0 = in a straight line.</summary>
    private float Bend(int a, int b, int c) => Vector3.Angle(_p[b] - _p[a], _p[c] - _p[b]);

    private float Mm(int a, int b) => Vector3.Distance(_p[a], _p[b]) * 1000f;

    private void Add(List<float[]> list, float[] v)
    {
        list.Add(v);
        if (list.Count > _maxSamples) list.RemoveAt(0);
    }

    private void WriteRow(int fid, float z, float thumbBend, float indexBend, bool thumbOk, bool indexOk,
                          float t1, float t2, float i1, float i2, float i3)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (_log == null)
        {
            string path = System.IO.Path.Combine(Application.persistentDataPath,
                "glove_finger_lengths_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
            _log = new System.IO.StreamWriter(path, false, new System.Text.UTF8Encoding(false));
            _log.WriteLine("t,fid,depth_m,thumb_bend_deg,index_bend_deg,thumb_ok,index_ok," + string.Join(",", Names));
            Debug.Log($"[GloveFingerLengthMeter] Writing glove finger lengths (mm) to {path}", this);
        }
        _log.WriteLine(string.Join(",",
            Time.realtimeSinceStartup.ToString("F3", inv), fid.ToString(inv), z.ToString("F4", inv),
            thumbBend.ToString("F1", inv), indexBend.ToString("F1", inv), thumbOk ? "1" : "0", indexOk ? "1" : "0",
            t1.ToString("F2", inv), t2.ToString("F2", inv), (t1 + t2).ToString("F2", inv),
            i1.ToString("F2", inv), i2.ToString("F2", inv), i3.ToString("F2", inv), (i1 + i2 + i3).ToString("F2", inv)));
        if (Time.frameCount % 72 == 0) _log.Flush();
    }

    /// <summary>Percentile p (0..1) of column k. p90 is used as the length: a finger tilted towards / away from the
    /// camera only looks SHORTER in the image (never longer), so the longest frames are closest to the truth.
    /// First session: index median 85.1, p90 90.4 mm, ruler 90 mm (+0.4%).</summary>
    private static float Percentile(List<float[]> list, int k, float p)
    {
        var a = new float[list.Count];
        for (int i = 0; i < a.Length; i++) a[i] = list[i][k];
        System.Array.Sort(a);
        return a[Mathf.Clamp(Mathf.RoundToInt(p * (a.Length - 1)), 0, a.Length - 1)];
    }

    private string Summary()
    {
        // Only the TOTAL (knuckle -> tip) is reliable: both end points are easy to see on the glove. Single segments depend
        // on where the middle joints were GUESSED when labelling (the glove hides them), so they are for reference only.
        string s = $"DO NGON (GANG) -- {_seen} anh, dat: cai {_thumb.Count} / tro {_index.Count}";
        if (_thumb.Count >= 10)
            s += $"\n Cai: MCP-dau {Percentile(_thumb, 2, 0.9f):F1} mm  (trung vi {Percentile(_thumb, 2, 0.5f):F1})" +
                 "\n   -> xoay tay cho MONG ngon cai quay ve camera, ngon cai duoi thang";
        else s += "\n Cai: chua du anh (duoi thang ngon cai, mong ngon quay ve camera)";
        if (_index.Count >= 10)
            s += $"\n Tro: MCP-dau {Percentile(_index, 3, 0.9f):F1} mm  (trung vi {Percentile(_index, 3, 0.5f):F1})";
        else s += "\n Tro: chua du anh (duoi thang ngon tro, quay ve camera)";
        return s;
    }
}
