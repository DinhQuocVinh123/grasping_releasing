using UnityEngine;

/// <summary>
/// Giai đoạn 1 — test xác nhận định vị: gắn script này lên 1 Cube,
/// cube sẽ bám theo vị trí của Touch Plus (đọc qua OVRInput) mỗi frame.
/// Không làm gì khác — không rotation, không smoothing — để quan sát
/// đúng hành vi thô của tracking (tracking loss / trôi / trễ).
///
/// Cách gắn:
/// 1. Tạo 1 GameObject Cube trong scene (GameObject > 3D Object > Cube).
/// 2. Kéo script này vào Cube đó.
/// 3. Chọn đúng tay đang đeo Touch Plus ở ô "Controller" trong Inspector.
/// 4. Play, đeo Touch Plus lên mu bàn tay, quan sát cube theo 3 tiêu chí:
///    - Đưa tay khắp phòng: có mất tracking (cube biến mất/đứng hình) không
///    - Giữ yên 30s: cube có tự trôi không
///    - Vẫy tay nhanh: có thấy độ trễ giữa tay thật và cube không
/// </summary>
public class ControllerPositionCubeTest : MonoBehaviour
{
    [Tooltip("Tay đang đeo Touch Plus.")]
    [SerializeField] private OVRInput.Controller _controller = OVRInput.Controller.RTouch;

    private float _logTimer = 0f;

    private void Update()
    {
        Vector3 pos = OVRInput.GetLocalControllerPosition(_controller);
        transform.position = pos;

        // In giá trị thô ra Logcat mỗi giây, để xem con số có tăng bất thường không.
        _logTimer += Time.deltaTime;
        if (_logTimer >= 1f)
        {
            _logTimer = 0f;
            Debug.Log($"[ControllerPositionCubeTest] {_controller} raw position: {pos}");
        }
    }
}
