using System.Collections.Generic;
using Oculus.Interaction.Input;
using UnityEngine;

/// <summary>
/// Do chieu dai tung dot ngon cai + ngon tro (TAY TRAN) bang hand tracking cua Quest -- dau vao cho mo hinh luc
/// (tools/modeling, can chieu dai ngon). Thu nghiem 20/10: chua biet Quest do THAT tung ngon hay chi phong to/thu nho
/// 1 ban tay mau theo co tay -> doi chieu voi thuoc. Cot scale (IHand.Scale) de kiem tra: chieu dai / scale gan nhu
/// hang so giua nguoi khac nhau = ban tay mau.
///
/// Doc nguon THO (OVRHands/OVRHandDataSourceRight|Left, truoc moi bo loc va truoc khi he thong gang ghi de tay phai).
/// Chi lay khung Quest dang theo doi VA tin cay cao. Hien trung vi trong kinh (_display) + ghi tung khung ra
/// Application.persistentDataPath/finger_lengths_*.csv (tools/finger_lengths.py tom tat).
///
/// Moc (chuan OpenXR, la TAM khop, khong phai be mat da):
///   ngon cai: Thumb1 goc (CMC) -> Thumb2 khop dot (MCP) -> Thumb3 khop gan dau (IP) -> ThumbTip dau ngon
///   ngon tro: Index0 goc xuong ban tay -> Index1 khop dot (MCP) -> Index2 khop giua (PIP) -> Index3 khop gan dau (DIP) -> IndexTip
/// </summary>
public class FingerLengthMeter : MonoBehaviour
{
    [Tooltip("Nguon tay THO cua Quest. De trong = tu tim OVRHandDataSourceRight / OVRHandDataSourceLeft.")]
    [SerializeField] private Hand _rightHand;
    [SerializeField] private Hand _leftHand;
    [Tooltip("Chu hien trung vi trong kinh. De trong = khong hien (van ghi file).")]
    [SerializeField] private TMPro.TextMeshPro _display;
    [SerializeField] private bool _logToFile = true;
    [Tooltip("Chi giu bay nhieu khung gan nhat cho moi tay khi tinh trung vi (~72 khung/giay).")]
    [SerializeField] private int _maxSamples = 2160;

    private static readonly HandJointId[] ThumbChain = { HandJointId.HandThumb1, HandJointId.HandThumb2, HandJointId.HandThumb3, HandJointId.HandThumbTip };
    private static readonly HandJointId[] IndexChain = { HandJointId.HandIndex0, HandJointId.HandIndex1, HandJointId.HandIndex2, HandJointId.HandIndex3, HandJointId.HandIndexTip };
    // Moi khung: 3 dot ngon cai, 4 dot ngon tro (dot dau = xuong ban tay), tong khop dot -> dau ngon cua tung ngon, scale
    private const int Values = 3 + 4 + 2 + 1;
    private static readonly string[] Names =
    {
        "cai_goc_dot(CMC-MCP)", "cai_dot1(MCP-IP)", "cai_dot2(IP-dau)",
        "tro_xuongbantay", "tro_dot1(MCP-PIP)", "tro_dot2(PIP-DIP)", "tro_dot3(DIP-dau)",
        "cai_MCP-dau", "tro_MCP-dau", "scale",
    };

    private readonly List<float[]>[] _samples = { new List<float[]>(), new List<float[]>() };
    private readonly Pose[] _thumb = new Pose[4];
    private readonly Pose[] _index = new Pose[5];
    private System.IO.StreamWriter _log;
    private float _nextDisplay;

    private void Awake()
    {
        if (_rightHand == null) _rightHand = FindHand("OVRHandDataSourceRight");
        if (_leftHand == null) _leftHand = FindHand("OVRHandDataSourceLeft");
    }

    private static Hand FindHand(string objectName)
    {
        foreach (var h in FindObjectsByType<Hand>(FindObjectsInactive.Exclude))
            if (h.gameObject.name == objectName) return h;
        return null;
    }

    private void OnDisable()
    {
        _log?.Dispose();
        _log = null;
    }

    private void Update()
    {
        Sample(_rightHand, 0, "R");
        Sample(_leftHand, 1, "L");
        if (_display != null && Time.unscaledTime >= _nextDisplay)
        {
            _nextDisplay = Time.unscaledTime + 0.25f;
            _display.text = Summary(0, "TAY PHAI") + "\n" + Summary(1, "TAY TRAI");
        }
    }

    private void Sample(Hand hand, int side, string tag)
    {
        if (hand == null || !hand.IsTrackedDataValid || !hand.IsHighConfidence) return;
        for (int i = 0; i < ThumbChain.Length; i++) if (!hand.GetJointPose(ThumbChain[i], out _thumb[i])) return;
        for (int i = 0; i < IndexChain.Length; i++) if (!hand.GetJointPose(IndexChain[i], out _index[i])) return;

        var v = new float[Values];
        for (int i = 0; i < 3; i++) v[i] = Mm(_thumb[i], _thumb[i + 1]);
        for (int i = 0; i < 4; i++) v[3 + i] = Mm(_index[i], _index[i + 1]);
        v[7] = v[1] + v[2];        // ngon cai: khop dot -> dau ngon (theo duong khop)
        v[8] = v[4] + v[5] + v[6]; // ngon tro: khop dot -> dau ngon
        v[9] = hand.Scale;

        var list = _samples[side];
        list.Add(v);
        if (list.Count > _maxSamples) list.RemoveAt(0);
        if (_logToFile) WriteRow(tag, v);
    }

    private static float Mm(Pose a, Pose b) => Vector3.Distance(a.position, b.position) * 1000f;

    private void WriteRow(string tag, float[] v)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (_log == null)
        {
            string path = System.IO.Path.Combine(Application.persistentDataPath,
                "finger_lengths_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
            _log = new System.IO.StreamWriter(path, false, new System.Text.UTF8Encoding(false));
            _log.WriteLine("t,hand," + string.Join(",", Names));
            Debug.Log($"[FingerLengthMeter] Ghi chieu dai ngon (mm) vao {path}", this);
        }
        var sb = new System.Text.StringBuilder(128);
        sb.Append(Time.realtimeSinceStartup.ToString("F3", inv)).Append(',').Append(tag);
        for (int i = 0; i < v.Length; i++) sb.Append(',').Append(v[i].ToString(i == 9 ? "F4" : "F2", inv));
        _log.WriteLine(sb);
        if (Time.frameCount % 72 == 0) _log.Flush();
    }

    /// <summary>Trung vi tung dot (mm) va do lech (khoang tu phan vi 25 den 75) cua cac khung da lay.</summary>
    private string Summary(int side, string title)
    {
        var list = _samples[side];
        if (list.Count < 10) return $"{title}: chua thay tay (tay tran, xoe ngon, trong tam nhin)";
        float Med(int k, out float spread)
        {
            var a = new float[list.Count];
            for (int i = 0; i < a.Length; i++) a[i] = list[i][k];
            System.Array.Sort(a);
            spread = a[a.Length * 3 / 4] - a[a.Length / 4];
            return a[a.Length / 2];
        }
        float c0 = Med(0, out _), c1 = Med(1, out _), c2 = Med(2, out _), ct = Med(7, out float cs);
        float i1 = Med(4, out _), i2 = Med(5, out _), i3 = Med(6, out _), it = Med(8, out float isp);
        float sc = Med(9, out _);
        return $"{title} ({list.Count} khung, scale {sc:F3})\n" +
               $" Cai: goc {c0:F1} | dot1 {c1:F1} | dot2 {c2:F1}  -> MCP-dau {ct:F1} mm (lech {cs:F1})\n" +
               $" Tro: dot1 {i1:F1} | dot2 {i2:F1} | dot3 {i3:F1}  -> MCP-dau {it:F1} mm (lech {isp:F1})";
    }
}
