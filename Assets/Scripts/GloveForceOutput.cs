using System;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

/// <summary>
/// Gui LUC MUC TIEU cua ngon cai + ngon tro (tay gang) sang may tinh qua UDP --
/// de MATLAB doi ra ap suat cho actuator McKibben (thuy luc, bom bang xi-lanh +
/// motor DC, dieu khien qua Arduino).
///
/// Luc lay tu HapticRenderer theo phuong phap god-object: vat day nguoc len
/// ngon theo PHAP TUYEN be mat, do lon ti le do ngon THAT lun vao vat (xem
/// HapticRenderer, HapticMaterial.MaxForceNewton). Vector duoc doi sang he toa
/// do BAN TAY (theo xuong co tay) -- actuator gan tren tay nen can luc so voi
/// ban tay, khong phai so voi can phong.
///
/// Moi goi tin la 1 dong chu (de doc trong MATLAB):
///   GF,seq,t,thumbFx,thumbFy,thumbFz,indexFx,indexFy,indexFz,thumbN,indexN,thumbFlexDeg,indexFlexDeg,thumbContact,indexContact
///   - F (N): luc vat day len dau ngon, toa do ban tay (truc x/y/z cua xuong co tay)
///   - N (N): do lon luc (da chan tren boi _safetyMaxNewton)
///   - FlexDeg: tong goc gap cua ngon (0 = duoi thang) -- de uoc luong muc co cua ong McKibben
///   - Contact: 1 neu dang cham vat
///
/// AN TOAN: goi tin duoc gui DEU (ca khi luc = 0) nhu nhip tim. Phia Arduino/MATLAB
/// PHAI tu xa ap neu khong nhan goi tin nao trong ~200 ms (Unity treo, mat WiFi...).
///
/// Gan len tay gang (cung object voi HapticRenderer cua tay do).
/// </summary>
[DefaultExecutionOrder(170)] // sau HapticRenderer (150): doc luc da tinh cua khung nay
public class GloveForceOutput : MonoBehaviour
{
    [SerializeField] private HapticRenderer _haptics;
    [Tooltip("Lay goc gap ngon (de uoc luong muc co actuator). De trong = tim tren cung object.")]
    [SerializeField] private FingerUDPReceiver _fingers;
    [Tooltip("He toa do de bieu dien luc: xuong CO TAY cua tay gang (XRHand_Wrist). De trong = tu tim.")]
    [SerializeField] private Transform _handFrame;

    [Header("Đích gửi")]
    [Tooltip("IP may chay MATLAB. De trong = gui ve may dang nhan anh camera (GloveLiveStreamer).")]
    [SerializeField] private string _targetIp = "";
    [SerializeField] private int _targetPort = 5010;
    [SerializeField] private GloveLiveStreamer _streamer;
    [Tooltip("So goi tin moi giay (gui deu ca khi khong cham -- lam nhip tim cho co che tu xa ap).")]
    [SerializeField] private float _sendRateHz = 60f;

    [Header("An toàn")]
    [Tooltip("Luc toi da gui di (N) -- chan tren, phong truong hop tinh sai.")]
    [SerializeField] private float _safetyMaxNewton = 6f;

    private UdpClient _udp;
    private float _nextSend;
    private int _seq;
    private readonly StringBuilder _sb = new StringBuilder(256);

    /// <summary>Goi tin gan nhat (de xem / kiem tra).</summary>
    public string LastPacket { get; private set; } = "";

    private void Awake()
    {
        if (_haptics == null) _haptics = GetComponent<HapticRenderer>();
        if (_fingers == null) _fingers = GetComponent<FingerUDPReceiver>();
        if (_streamer == null) _streamer = FindAnyObjectByType<GloveLiveStreamer>();
        if (_handFrame == null) _handFrame = FindDeepChild(transform, "XRHand_Wrist");
    }

    private void OnDisable()
    {
        _udp?.Close();
        _udp = null;
    }

    private void LateUpdate()
    {
        if (Time.unscaledTime < _nextSend) return;
        _nextSend = Time.unscaledTime + 1f / Mathf.Max(_sendRateHz, 1f);

        string ip = !string.IsNullOrEmpty(_targetIp) ? _targetIp : _streamer != null ? _streamer.ServerIp : null;
        if (string.IsNullOrEmpty(ip) || _haptics == null) return;

        string packet = BuildPacket();
        try
        {
            _udp ??= new UdpClient();
            byte[] bytes = Encoding.ASCII.GetBytes(packet);
            _udp.Send(bytes, bytes.Length, ip, _targetPort);
        }
        catch (Exception)
        {
            _udp?.Close(); // mang loi -> lan sau tao lai
            _udp = null;
        }
    }

    /// <summary>Dung goi tin cho khung hien tai. Public de kiem tra ngoai Play mode.</summary>
    public string BuildPacket()
    {
        var inv = CultureInfo.InvariantCulture;
        HapticRenderer.FingerState thumb = _haptics.GetState(HapticFinger.Thumb);
        HapticRenderer.FingerState index = _haptics.GetState(HapticFinger.Index);
        Vector3 ft = ToHand(Vector3.ClampMagnitude(thumb.forceNewton, _safetyMaxNewton));
        Vector3 fi = ToHand(Vector3.ClampMagnitude(index.forceNewton, _safetyMaxNewton));

        _sb.Clear();
        _sb.Append("GF,").Append(_seq++).Append(',').Append(Time.unscaledTime.ToString("F3", inv));
        foreach (float v in new[] { ft.x, ft.y, ft.z, fi.x, fi.y, fi.z, ft.magnitude, fi.magnitude })
            _sb.Append(',').Append(v.ToString("F3", inv));
        _sb.Append(',').Append((_fingers != null ? _fingers.FingerFlexionDegrees(0) : 0f).ToString("F1", inv));
        _sb.Append(',').Append((_fingers != null ? _fingers.FingerFlexionDegrees(1) : 0f).ToString("F1", inv));
        _sb.Append(',').Append(thumb.touching ? 1 : 0).Append(',').Append(index.touching ? 1 : 0);
        LastPacket = _sb.ToString();
        return LastPacket;
    }

    private Vector3 ToHand(Vector3 worldForce) =>
        _handFrame != null ? _handFrame.InverseTransformDirection(worldForce) : worldForce;

    private static Transform FindDeepChild(Transform parent, string name)
    {
        foreach (Transform child in parent)
        {
            if (child.name == name) return child;
            Transform result = FindDeepChild(child, name);
            if (result != null) return result;
        }
        return null;
    }
}
