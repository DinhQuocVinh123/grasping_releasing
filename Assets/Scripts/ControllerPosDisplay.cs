using System.Net.Sockets;
using System.Text;
using UnityEngine;

/// <summary>
/// Hiển thị vị trí tay cầm Touch Plus (trong không gian tracking của
/// Quest) bằng 1 TextMesh luôn nổi trước mắt -- dùng TẠM THỜI để đọc số
/// lúc hiệu chuẩn camera-to-world -- VÀ đồng thời gửi số đó qua UDP sang
/// máy tính (calibrate_camera_to_world.py), để script hiệu chuẩn tự
/// động lấy đúng vị trí mới nhất khi bấm SPACE, khỏi phải đọc/gõ tay.
///
/// Không phụ thuộc Immersive Debugger (gói đó đang lỗi build ở project
/// này) -- chỉ dùng TextMesh + UdpClient có sẵn của .NET/Unity.
///
/// Gắn script này vào bất kỳ GameObject nào đang active trong Scene.
/// </summary>
public class ControllerPosDisplay : MonoBehaviour
{
    [SerializeField] private Vector3 _offsetFromCamera = new Vector3(0f, -0.1f, 0.6f);
    [SerializeField] private Color _textColor = Color.green;

    [Header("Gửi vị trí controller qua UDP sang máy tính (dùng cho calibrate_camera_to_world.py)")]
    [Tooltip("Địa chỉ IP của máy tính đang chạy script Python hiệu chuẩn -- PHẢI cùng mạng với Quest.")]
    [SerializeField] private string _pcIp = "10.42.0.166";
    [SerializeField] private int _pcPort = 5006;

    private TextMesh _text;
    private Transform _cam;
    private UdpClient _sendClient;
    private Vector3 _avgPos;
    private Vector3 _avgRotEuler;
    private bool _initialized;

    private void Awake()
    {
        _cam = Camera.main != null ? Camera.main.transform : null;

        var go = new GameObject("ControllerPosText");
        go.transform.SetParent(transform, false);
        _text = go.AddComponent<TextMesh>();

        // AddComponent<TextMesh> khong tu gan Font/Material -- neu bo qua
        // buoc nay, mesh chu se KHONG duoc tao ra (vo hinh hoan toan) du
        // component van chay binh thuong. Phai gan tay Font + Material
        // khop voi Font do thi chu moi hien ra duoc.
        Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        _text.font = font;
        go.GetComponent<MeshRenderer>().sharedMaterial = font.material;

        _text.characterSize = 0.008f;
        _text.fontSize = 48;
        _text.color = _textColor;
        _text.anchor = TextAnchor.MiddleCenter;
        _text.alignment = TextAlignment.Center;

        _sendClient = new UdpClient();
    }

    private void Update()
    {
        // QUAN TRONG: webcam gan TREN DAU KINH VR, di chuyen CUNG voi dau --
        // khong dung yen trong phong. Vi vay phai tinh vi tri/xoay cua tay
        // cam TUONG DOI SO VOI DAU (CenterEyeAnchor), khong phai so voi
        // TrackingSpace/phong -- gia tri nay se KHONG DOI dung theo dau
        // xoay/di chuyen, vi webcam va dau gan chet voi nhau.
        Vector3 worldPos = OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
        Quaternion worldRot = OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch);

        Vector3 pos = _cam != null ? _cam.InverseTransformPoint(worldPos) : worldPos;
        Vector3 rotEuler = _cam != null
            ? (Quaternion.Inverse(_cam.rotation) * worldRot).eulerAngles
            : worldRot.eulerAngles;

        // Lam muot (trung binh truot) -- vi tay cam se duoc gan CO DINH sat
        // webcam (khong cam tay), gia tri se gan nhu dung yen, chi con
        // nhieu do sensor -- lam muot de de doc so on dinh hon.
        if (!_initialized)
        {
            _avgPos = pos;
            _avgRotEuler = rotEuler;
            _initialized = true;
        }
        else
        {
            _avgPos = Vector3.Lerp(pos, _avgPos, 0.95f);
            _avgRotEuler = Vector3.Lerp(rotEuler, _avgRotEuler, 0.95f);
        }

        _text.text = $"Controller so voi DAU (m / deg)\n"
            + $"Pos X:{_avgPos.x:F3} Y:{_avgPos.y:F3} Z:{_avgPos.z:F3}\n"
            + $"Rot X:{_avgRotEuler.x:F1} Y:{_avgRotEuler.y:F1} Z:{_avgRotEuler.z:F1}";

        try
        {
            string msg = $"{pos.x:F4},{pos.y:F4},{pos.z:F4}";
            byte[] data = Encoding.UTF8.GetBytes(msg);
            _sendClient.Send(data, data.Length, _pcIp, _pcPort);
        }
        catch
        {
            // Khong chan Update neu gui loi (vd chua co mang) -- bo qua.
        }

        if (_cam == null) return;
        transform.position = _cam.position + _cam.rotation * _offsetFromCamera;
        transform.rotation = _cam.rotation;
    }

    private void OnDestroy()
    {
        _sendClient?.Close();
    }
}
