using UnityEngine;

/// <summary>
/// Giai đoạn 2 (Bước 6) — xoay ngón tay bằng hàm sine giả, chưa cần ESP32,
/// để xác nhận trục xoay đúng, chiều đúng, biên độ hợp lý trước khi cắm
/// dữ liệu cảm biến thật vào (chỉ cần đổi nguồn dữ liệu, không đổi cách
/// xoay xương -- xem ghi chú ở cuối file).
///
/// Gắn cùng GameObject với HandFingerRig (StaticHandModel_Right).
/// Tất cả tham số chỉnh trực tiếp trong Inspector, không cần sửa code.
/// </summary>
[RequireComponent(typeof(HandFingerRig))]
public class FingerSineTest : MonoBehaviour
{
    private enum Axis { X, Y, Z }

    [Header("Trục cong ngón (thử X trước, đổi nếu cong sai hướng)")]
    [SerializeField] private Axis _curlAxis = Axis.X;

    [Header("Biên độ cong (độ) -- 0 = duỗi thẳng, giá trị này = cong hết cỡ")]
    [SerializeField] private float _amplitudeDegrees = 70f;

    [Header("Tốc độ lặp xòe/nắm (chu kỳ mỗi giây)")]
    [SerializeField] private float _speed = 0.5f;

    [Tooltip("Bật nếu ngón cong ngược hướng mong muốn (ví dụ cong ra sau thay vì vào lòng bàn tay).")]
    [SerializeField] private bool _invert = false;

    private HandFingerRig _rig;
    private Transform[][] _fingerJoints;
    private float[][] _baseAngleOnAxis; // góc gốc (tư thế tĩnh đã calibrate) của đúng trục đang test, giữ nguyên 2 trục còn lại

    private void Awake()
    {
        _rig = GetComponent<HandFingerRig>();

        _fingerJoints = new[]
        {
            new[] { _rig.thumbMetacarpal, _rig.thumbProximal, _rig.thumbDistal },
            new[] { _rig.indexProximal, _rig.indexIntermediate, _rig.indexDistal },
            new[] { _rig.middleProximal, _rig.middleIntermediate, _rig.middleDistal },
            new[] { _rig.ringProximal, _rig.ringIntermediate, _rig.ringDistal },
            new[] { _rig.pinkyProximal, _rig.pinkyIntermediate, _rig.pinkyDistal },
        };

        _baseAngleOnAxis = new float[_fingerJoints.Length][];
        for (int f = 0; f < _fingerJoints.Length; f++)
        {
            _baseAngleOnAxis[f] = new float[_fingerJoints[f].Length];
            for (int j = 0; j < _fingerJoints[f].Length; j++)
            {
                Transform joint = _fingerJoints[f][j];
                if (joint != null)
                    _baseAngleOnAxis[f][j] = GetAxis(joint.localEulerAngles);
            }
        }
    }

    private void Update()
    {
        // Chỉ cong theo 1 chiều (0 -> biên độ), giống ngón tay thật -- không duỗi ngược ra sau.
        float t = (Mathf.Sin(Time.time * _speed * Mathf.PI * 2f) + 1f) * 0.5f; // 0..1
        float offset = t * _amplitudeDegrees * (_invert ? -1f : 1f);

        for (int f = 0; f < _fingerJoints.Length; f++)
        {
            for (int j = 0; j < _fingerJoints[f].Length; j++)
            {
                Transform joint = _fingerJoints[f][j];
                if (joint == null) continue;

                Vector3 e = joint.localEulerAngles;
                SetAxis(ref e, _baseAngleOnAxis[f][j] + offset);
                joint.localEulerAngles = e;
            }
        }
    }

    private float GetAxis(Vector3 e) => _curlAxis switch
    {
        Axis.X => e.x,
        Axis.Y => e.y,
        _ => e.z,
    };

    private void SetAxis(ref Vector3 e, float value)
    {
        switch (_curlAxis)
        {
            case Axis.X: e.x = value; break;
            case Axis.Y: e.y = value; break;
            default: e.z = value; break;
        }
    }
}

// Ghi chú cho bước sau (thay sine bằng cảm biến thật):
// Chỉ cần thay dòng tính "offset" ở Update() bằng giá trị đọc được từ
// ESP32 (đã map từ analogRead() sang độ), giữ nguyên toàn bộ phần còn
// lại (vòng lặp gán vào từng khớp) -- đúng như kế hoạch "chỉ đổi 1 dòng".
