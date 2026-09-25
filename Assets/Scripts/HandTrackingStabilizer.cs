using Oculus.Interaction.Input;
using UnityEngine;

/// <summary>
/// Làm mượt vị trí/góc xoay của bàn tay do Meta's HandVisual tự gán
/// (từ native hand-tracking quang học của Quest) -- KHÔNG đụng vào code
/// gốc của Meta, chỉ đọc kết quả thô mỗi khung hình rồi ghi đè bằng
/// phiên bản đã lọc.
///
/// Dùng thuật toán "One Euro Filter" (Casiez, Roussel, Vogel -- công bố
/// công khai, được chính Meta Interaction SDK tích hợp sẵn tại
/// Oculus.Interaction.Input.OneEuroFilter) thay vì Lerp đơn giản --
/// giảm rung tốt hơn ở tốc độ chậm nhưng vẫn phản hồi nhanh khi tay di
/// chuyển nhanh.
///
/// Chạy ở LateUpdate() -- Unity luôn thực thi toàn bộ Update() của mọi
/// script trước, rồi mới đến toàn bộ LateUpdate(), nên script này tự
/// động chạy SAU khi HandVisual (dùng Update()) đã gán xong vị trí thô.
///
/// Gắn cùng GameObject với HandVisual (StaticHandModel_Right). Dùng khi
/// BẬT Hand Tracking Support (Hands Only / Controllers And Hands) và để
/// HandVisual's Update Root Pose/Visibility đang TICK (native tracking
/// đang điều khiển vị trí) -- script này chỉ lọc thêm, không thay thế
/// nguồn dữ liệu.
/// </summary>
public class HandTrackingStabilizer : MonoBehaviour
{
    [Header("Nguồn IHand của Meta (kéo đúng object 'Synthetic Hand' mà Hand Visual đang tham chiếu ở field 'Hand')")]
    [Tooltip("Neu bo trong, script van chay nhung khong the tu dong dung lai khi Meta bao do tin cay thap.")]
    [SerializeField] private Hand _hand;

    [Header("Tham số One Euro Filter -- vị trí")]
    [Tooltip("Giảm giá trị này nếu vẫn còn rung khi tay đứng yên.")]
    [SerializeField] private float _positionMinCutoff = 1.0f;
    [Tooltip("Tăng giá trị này nếu thấy tay bị trễ/ì khi di chuyển nhanh.")]
    [SerializeField] private float _positionBeta = 0.3f;

    [Header("Tham số One Euro Filter -- góc xoay")]
    [SerializeField] private float _rotationMinCutoff = 1.0f;
    [SerializeField] private float _rotationBeta = 0.3f;

    [Header("Ngưỡng từ chối cú nhảy đột ngột (dấu hiệu mất tracking tạm thời)")]
    [Tooltip("Nếu vị trí đổi quá xa trong 1 khung hình (mét), giữ nguyên giá trị đã lọc, không cho nhảy theo.")]
    [SerializeField] private float _maxPositionJumpMeters = 0.15f;
    [Tooltip("Nếu góc xoay đổi quá nhiều trong 1 khung hình (độ), giữ nguyên giá trị đã lọc, không cho nhảy theo.")]
    [SerializeField] private float _maxRotationJumpDegrees = 45f;

    [Header("Hiệu chỉnh lệch cố định thủ công (bao tay cơ khí làm Quest nhận sai vị trí cổ tay so với tay trần)")]
    [Tooltip("Chỉnh trực tiếp trong Inspector lúc đang Play (đeo kính, nhìn tay ảo) cho tới khi khớp đúng tay thật. Đơn vị: mét, theo trục local của object này.")]
    [SerializeField] private Vector3 _manualPositionOffset = Vector3.zero;
    [Tooltip("Tương tự nhưng cho góc xoay (độ, local Euler).")]
    [SerializeField] private Vector3 _manualRotationOffset = Vector3.zero;

    private IOneEuroFilter<Vector3> _positionFilter;
    private IOneEuroFilter<Quaternion> _rotationFilter;
    private Vector3 _lastRawForJumpCheck;
    private Quaternion _lastRawRotForJumpCheck;
    private Vector3 _lastAppliedPos;   // gia tri DA LOC, thuc su duoc gan vao transform lan cuoi
    private Quaternion _lastAppliedRot;
    private bool _initialized;

    private void Awake()
    {
        _positionFilter = OneEuroFilter.CreateVector3();
        _rotationFilter = OneEuroFilter.CreateQuaternion();
    }

    private void LateUpdate()
    {
        // Neu Meta tu bao do tin cay THAP cho ca ban tay (gang tay lam
        // nhieu tracking) -- BO QUA hoan toan khung hinh nay, giu nguyen
        // gia tri DA LOC lan truoc, KHONG cho du lieu nhieu/sai di vao bo
        // loc (khac voi cach cu chi doan qua nguong nhay -- day la tin
        // hieu THAT tu chinh he thong tracking cua Quest, dang tin cay
        // hon nhieu).
        if (_hand != null && _initialized && !_hand.IsHighConfidence)
        {
            transform.localPosition = _lastAppliedPos;
            transform.localRotation = _lastAppliedRot;
            return;
        }

        Vector3 rawPos = transform.localPosition;
        Quaternion rawRot = transform.localRotation;

        if (!_initialized)
        {
            _lastAppliedPos = _positionFilter.Step(rawPos, Time.deltaTime) + _manualPositionOffset;
            _lastAppliedRot = _rotationFilter.Step(rawRot, Time.deltaTime) * Quaternion.Euler(_manualRotationOffset);
            _lastRawForJumpCheck = rawPos;
            _lastRawRotForJumpCheck = rawRot;
            _initialized = true;
            transform.localPosition = _lastAppliedPos;
            transform.localRotation = _lastAppliedRot;
            return;
        }

        // Nhay qua lon trong 1 khung hinh (mat tracking tam thoi) -- bo
        // qua gia tri nay, dung lai gia tri thô tot gan nhat lam vao filter
        // (giu nguyen ket qua da loc, khong keo theo cu nhay).
        Vector3 posToFilter = Vector3.Distance(rawPos, _lastRawForJumpCheck) <= _maxPositionJumpMeters
            ? rawPos : _lastRawForJumpCheck;
        Quaternion rotToFilter = Quaternion.Angle(rawRot, _lastRawRotForJumpCheck) <= _maxRotationJumpDegrees
            ? rawRot : _lastRawRotForJumpCheck;

        _lastRawForJumpCheck = posToFilter;
        _lastRawRotForJumpCheck = rotToFilter;

        _positionFilter.SetProperties(new OneEuroFilterPropertyBlock(_positionMinCutoff, _positionBeta));
        _rotationFilter.SetProperties(new OneEuroFilterPropertyBlock(_rotationMinCutoff, _rotationBeta));

        _lastAppliedPos = _positionFilter.Step(posToFilter, Time.deltaTime) + _manualPositionOffset;
        _lastAppliedRot = _rotationFilter.Step(rotToFilter, Time.deltaTime) * Quaternion.Euler(_manualRotationOffset);

        transform.localPosition = _lastAppliedPos;
        transform.localRotation = _lastAppliedRot;
    }
}
