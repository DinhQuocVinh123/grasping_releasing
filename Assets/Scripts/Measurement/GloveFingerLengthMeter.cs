using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Do chieu dai tung dot ngon cai + ngon tro KHI DEO GANG, tu chinh he thong gang (21/10) -- dau vao cho mo hinh luc.
/// FingerLengthMeter (tay tran, hand tracking Quest) chi ra ban tay MAU phong to/thu nho (chieu dai / scale khong doi
/// tren 8018 khung, 20/10), nen khong do duoc ti le rieng tung ngon.
///
/// Moi anh co ket qua tu Python:
///   1. 9 diem model gang (gang2010) tren anh: co tay, ngon cai 1-4, ngon tro 5-8 (FingerUDPReceiver).
///   2. Tia tu camera LUC CHUP qua tung diem (GloveLiveStreamer.TryGetFrameRay -- dung tieu cu + huong camera).
///   3. Khoang cach toi tay = tam tay THEO QUEST luc chup (goi y vi tri, theo truc nhin camera) -- doc lap voi model.
///      KHONG dung chieu sau cua ImageHandSolver: bo giai tu dat co tay theo ban tay mau -> do lai chi ra co mau.
///   4. Coi ban tay phang, quay ve camera, o khoang cach do: tia cat mat phang -> diem 3D; khoang cach 2 diem lien
///      nhau = chieu dai dot. Chi lay anh du 9 diem ro, khong sat mep, ngon DUOI THANG (ngon nghieng/gap trong ngan hon).
///   5. Phan vi 90 (p90) qua cac anh dat -> hien trong kinh + ghi Application.persistentDataPath/glove_finger_lengths_*.csv
///      (tools/glove_finger_lengths.py tom tat, so voi thuoc). p90 vi ngon nghieng chi lam ngan tren anh.
///      Chi so TONG (khop dot -> dau ngon) dang tin; tung dot theo cho khop giua duoc doan luc gan nhan (gang che khop).
///      Ngon cai giu tu nhien thi cheo ve camera -> ngan hon that ~10% (21/10): xoay tay cho mong ngon cai quay ve camera.
///
/// Do tren GANG: dau ngon = dau nap gang, khop = cho model doan tren mat gang (theo nhan 20/10). Sai so khoang cach
/// cua Quest khi deo gang (vai cm) -> sai bay nhieu % chieu dai: so voi thuoc truoc khi dung.
/// Huong dan do: deo gang, gio tay truoc mat, mu (hoac long) ban tay quay ve mat, ngon cai + tro duoi thang, giu ~20 s.
/// </summary>
[DefaultExecutionOrder(450)] // sau FingerUDPReceiver (0): ImageHandSolver da lay ket qua anh moi trong khung nay
public class GloveFingerLengthMeter : MonoBehaviour
{
    [Tooltip("De trong = tu tim trong scene.")]
    [SerializeField] private FingerUDPReceiver _receiver;
    [SerializeField] private GloveLiveStreamer _streamer;
    [Tooltip("Chu hien trung vi trong kinh. De trong = chi ghi file.")]
    [SerializeField] private TMPro.TextMeshPro _display;
    [SerializeField] private bool _logToFile = true;

    [Header("Chi lay anh tot")]
    [Tooltip("Do tin cay toi thieu cua MOI diem dung de do (model gui ve).")]
    [SerializeField] private float _minConfidence = 0.5f;
    [Tooltip("Diem cach mep anh it hon khoang nay (ti le anh) thi bo anh -- model doan chu khong nhin thay.")]
    [SerializeField] private float _edgeMargin = 0.05f;
    [Tooltip("Ngon coi la duoi thang khi cac dot lech huong nhau it hon goc nay (do). Ngon gap -> chieu dai tren anh ngan hon.")]
    [SerializeField] private float _maxBendDegrees = 20f;
    [Tooltip("Khoang cach hop le toi tay (met).")]
    [SerializeField] private Vector2 _depthRange = new Vector2(0.15f, 0.9f);
    [SerializeField] private int _maxSamples = 2000;

    private const int Points = HandImageFit.Points; // 0 co tay, 1-4 ngon cai, 5-8 ngon tro
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

        // Mat phang ban tay: vuong goc truc nhin camera, qua tam tay theo Quest luc chup
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

        // Ngon cai: 2 -> 3 -> 4 (khop dot -> khop gan dau -> dau); ngon tro: 5 -> 6 -> 7 -> 8
        float thumbBend = Bend(2, 3, 4), indexBend = Mathf.Max(Bend(5, 6, 7), Bend(6, 7, 8));
        bool thumbOk = thumbBend <= _maxBendDegrees, indexOk = indexBend <= _maxBendDegrees;
        float t1 = Mm(2, 3), t2 = Mm(3, 4), i1 = Mm(5, 6), i2 = Mm(6, 7), i3 = Mm(7, 8);
        if (thumbOk) Add(_thumb, new[] { t1, t2, t1 + t2 });
        if (indexOk) Add(_index, new[] { i1, i2, i3, i1 + i2 + i3 });
        if (_logToFile) WriteRow(fid, z, thumbBend, indexBend, thumbOk, indexOk, t1, t2, i1, i2, i3);
    }

    /// <summary>Goc lech huong (do) giua doan a->b va b->c: 0 = thang hang.</summary>
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
            Debug.Log($"[GloveFingerLengthMeter] Ghi chieu dai ngon (gang, mm) vao {path}", this);
        }
        _log.WriteLine(string.Join(",",
            Time.realtimeSinceStartup.ToString("F3", inv), fid.ToString(inv), z.ToString("F4", inv),
            thumbBend.ToString("F1", inv), indexBend.ToString("F1", inv), thumbOk ? "1" : "0", indexOk ? "1" : "0",
            t1.ToString("F2", inv), t2.ToString("F2", inv), (t1 + t2).ToString("F2", inv),
            i1.ToString("F2", inv), i2.ToString("F2", inv), i3.ToString("F2", inv), (i1 + i2 + i3).ToString("F2", inv)));
        if (Time.frameCount % 72 == 0) _log.Flush();
    }

    /// <summary>Phan vi p (0..1) cua cot k. Dung p90 lam chieu dai: ngon nghieng ve / ra khoi camera chi lam NGAN di
    /// tren anh (khong bao gio dai ra), nen cac anh dai nhat gan that nhat. Buoi 21/10 11:47: ngon tro trung vi 85.1,
    /// p90 90.4 mm, thuoc 90 mm (+0.4%).</summary>
    private static float Percentile(List<float[]> list, int k, float p)
    {
        var a = new float[list.Count];
        for (int i = 0; i < a.Length; i++) a[i] = list[i][k];
        System.Array.Sort(a);
        return a[Mathf.Clamp(Mathf.RoundToInt(p * (a.Length - 1)), 0, a.Length - 1)];
    }

    private string Summary()
    {
        // Chi so TONG (khop dot -> dau ngon) dang tin: 2 dau mut de thay tren gang. Tung dot phu thuoc cho khop giua
        // duoc DOAN luc gan nhan (khop bi gang che) -> chi de tham khao.
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
