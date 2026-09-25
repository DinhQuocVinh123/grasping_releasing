using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem; // Keyboard, cho việc đọc phím Space
using Oculus.Interaction; // PointerEvent, PointableUnityEventWrapper

/// <summary>
/// Ghi log dữ liệu tay + trạng thái grab ra CSV, mỗi frame một dòng.
/// Xem hướng dẫn gắn component ở cuối file (comment) hoặc trong tin nhắn đi kèm.
/// </summary>
public class GraspLogger : MonoBehaviour
{
    [Header("Nguồn dữ liệu tay (kéo OVRHand và OVRSkeleton của MỘT tay vào đây)")]
    [Tooltip("Component OVRHand nằm trên object Hand Tracking left/right dưới CameraRig.")]
    [SerializeField] private OVRHand _hand;

    [Tooltip("Component OVRSkeleton nằm cùng object với OVRHand ở trên.")]
    [SerializeField] private OVRSkeleton _skeleton;

    [Header("Nhãn object đang theo dõi (điền tên object bạn muốn ghi vào cột grabbedObject)")]
    [SerializeField] private string _trackedObjectLabel = "Sphere";

    [Tooltip("Kéo chính GameObject Sphere vào đây — dùng để log parent/vị trí thật của nó mỗi frame, phục vụ điều tra state leak.")]
    [SerializeField] private Transform _trackedObject;

    // Trạng thái grab hiện tại, được set bởi 2 hàm public OnGrabStart/OnGrabEnd
    // mà bạn sẽ nối vào WhenSelect/WhenUnselect của PointableUnityEventWrapper (xem hướng dẫn Inspector).
    private bool _isGrabbing = false;
    private string _grabbedObjectName = "";

    private StreamWriter _writer;
    private int _framesSinceFlush = 0;
    private const int FLUSH_INTERVAL_FRAMES = 60;

    private int _trialId = 0;

    private void Start()
    {
        // Tên file có timestamp để không bị ghi đè giữa các lần Play khác nhau.
        string fileName = $"GraspLog_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        string fullPath = Path.Combine(Application.persistentDataPath, fileName);

        _writer = new StreamWriter(fullPath, false, new UTF8Encoding(false));
        WriteHeader();

        // In full path ra Console để biết tìm file ở đâu (bắt buộc theo yêu cầu).
        Debug.Log($"[GraspLogger] Đang ghi log vào: {fullPath}");
    }

    private void WriteHeader()
    {
        _writer.WriteLine(string.Join(",", new[]
        {
            "time", "frame", "trialId", "isTracked", "handConfidence",
            "pinch_thumb", "pinch_index", "pinch_middle", "pinch_ring", "pinch_pinky",
            "conf_thumb", "conf_index", "conf_middle", "conf_ring", "conf_pinky",
            "tipdist_thumb", "tipdist_index", "tipdist_middle", "tipdist_ring", "tipdist_pinky",
            "flex_index_mcp", "flex_index_pip", "flex_middle_mcp",
            "isGrabbing", "grabbedObject",
            "objectParent", "objectWorldPos_x", "objectWorldPos_y", "objectWorldPos_z",
            "handWorldPos_x", "handWorldPos_y", "handWorldPos_z"
        }));
    }

    private void Update()
    {
        // Space = đánh dấu bắt đầu một lần thử mới (trialId tăng lên 1).
        // Dùng Input System mới (Keyboard.current) vì project đã tắt UnityEngine.Input cũ
        // (Project Settings > Player > Active Input Handling = Input System Package).
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            _trialId++;
            Debug.Log($"[GraspLogger] Trial mới: {_trialId}");
        }

        if (!TryGatherHandData(out HandFrameData data))
        {
            // OVRHand/OVRSkeleton chưa init xong (đầu Play, hoặc mất tracking tạm thời) -> bỏ qua frame này, không ghi, không crash.
            return;
        }

        WriteRow(data);

        _framesSinceFlush++;
        if (_framesSinceFlush >= FLUSH_INTERVAL_FRAMES)
        {
            _writer.Flush();
            _framesSinceFlush = 0;
        }
    }

    // Gói toàn bộ dữ liệu 1 frame lại cho gọn, tránh tham số dài dòng.
    private struct HandFrameData
    {
        public bool isTracked;
        public OVRHand.TrackingConfidence handConfidence;
        public float[] pinch; // theo thứ tự Thumb,Index,Middle,Ring,Pinky
        public OVRHand.TrackingConfidence[] fingerConfidence;
        public float[] tipDist;
        public float flexIndexMcp;
        public float flexIndexPip;
        public float flexMiddleMcp;

        // Phục vụ điều tra "state leak": vật vẫn dính vào tay dù isGrabbing=false.
        public string objectParentName; // tên transform cha của vật, "None" nếu không có cha
        public Vector3 objectWorldPos;  // vị trí thế giới thật của vật (Sphere)
        public Vector3 handWorldPos;    // vị trí thế giới của cổ tay (đại diện cho vị trí bàn tay)
    }

    private static readonly OVRHand.HandFinger[] FingerOrder =
    {
        OVRHand.HandFinger.Thumb,
        OVRHand.HandFinger.Index,
        OVRHand.HandFinger.Middle,
        OVRHand.HandFinger.Ring,
        OVRHand.HandFinger.Pinky
    };

    private bool TryGatherHandData(out HandFrameData data)
    {
        data = default;

        if (_hand == null || _skeleton == null)
        {
            return false;
        }

        // OVRSkeleton.IsInitialized = false nghĩa là danh sách Bones chưa sẵn sàng (đầu Play).
        // Không được đọc _skeleton.Bones lúc này, sẽ ném exception.
        if (!_skeleton.IsInitialized || _skeleton.Bones == null)
        {
            return false;
        }

        data.isTracked = _hand.IsTracked;
        data.handConfidence = _hand.HandConfidence;

        data.pinch = new float[5];
        data.fingerConfidence = new OVRHand.TrackingConfidence[5];
        for (int i = 0; i < FingerOrder.Length; i++)
        {
            data.pinch[i] = _hand.GetFingerPinchStrength(FingerOrder[i]);
            data.fingerConfidence[i] = _hand.GetFingerConfidence(FingerOrder[i]);
        }

        // Bản build mới mặc định dùng OpenXR hand skeleton (XRHand_*), không phải Hand_* kiểu cũ.
        // Hàm FindBone tự chọn đúng bộ tên theo GetSkeletonType() nên không cần tự tay chỉnh gì thêm.
        Transform wrist = FindBone(OVRSkeleton.BoneId.Hand_WristRoot, OVRSkeleton.BoneId.XRHand_Wrist);
        Transform thumbTip = FindBone(OVRSkeleton.BoneId.Hand_ThumbTip, OVRSkeleton.BoneId.XRHand_ThumbTip);
        Transform indexTip = FindBone(OVRSkeleton.BoneId.Hand_IndexTip, OVRSkeleton.BoneId.XRHand_IndexTip);
        Transform middleTip = FindBone(OVRSkeleton.BoneId.Hand_MiddleTip, OVRSkeleton.BoneId.XRHand_MiddleTip);
        Transform ringTip = FindBone(OVRSkeleton.BoneId.Hand_RingTip, OVRSkeleton.BoneId.XRHand_RingTip);
        // Chú ý: bên bộ tên OpenXR, ngón út gọi là "Little" chứ không phải "Pinky".
        Transform pinkyTip = FindBone(OVRSkeleton.BoneId.Hand_PinkyTip, OVRSkeleton.BoneId.XRHand_LittleTip);

        data.tipDist = new float[5];
        data.tipDist[0] = DistanceOrZero(wrist, thumbTip);
        data.tipDist[1] = DistanceOrZero(wrist, indexTip);
        data.tipDist[2] = DistanceOrZero(wrist, middleTip);
        data.tipDist[3] = DistanceOrZero(wrist, ringTip);
        data.tipDist[4] = DistanceOrZero(wrist, pinkyTip);

        // MCP = khớp gốc ngón (proximal), PIP = khớp giữa (intermediate).
        Transform indexMcp = FindBone(OVRSkeleton.BoneId.Hand_Index1, OVRSkeleton.BoneId.XRHand_IndexProximal);
        Transform indexPip = FindBone(OVRSkeleton.BoneId.Hand_Index2, OVRSkeleton.BoneId.XRHand_IndexIntermediate);
        Transform middleMcp = FindBone(OVRSkeleton.BoneId.Hand_Middle1, OVRSkeleton.BoneId.XRHand_MiddleProximal);

        data.flexIndexMcp = FlexAngleOrZero(indexMcp);
        data.flexIndexPip = FlexAngleOrZero(indexPip);
        data.flexMiddleMcp = FlexAngleOrZero(middleMcp);

        // Vị trí tay: lấy từ xương cổ tay đã tìm ở trên (độc lập với isTracked -
        // vẫn ghi được dù đang mất tracking, để so sánh vị trí "đóng băng" cuối cùng).
        data.handWorldPos = wrist != null ? wrist.position : Vector3.zero;

        // Trạng thái của vật đang theo dõi (Sphere) - ghi dù isGrabbing đang là gì,
        // để phát hiện trường hợp vật vẫn dính vào tay dù logic game nói là đã thả (state leak).
        if (_trackedObject != null)
        {
            data.objectParentName = _trackedObject.parent != null ? _trackedObject.parent.name : "None";
            data.objectWorldPos = _trackedObject.position;
        }
        else
        {
            data.objectParentName = "N/A";
            data.objectWorldPos = Vector3.zero;
        }

        return true;
    }

    // Tìm bone theo Id, tự chọn bộ Id "kiểu cũ" (OVR) hay "kiểu mới" (OpenXR) tuỳ theo
    // OVRSkeleton đang chạy skeleton nào (xem Project Settings > Meta XR > OVRManager > Hand Skeleton Version).
    private Transform FindBone(OVRSkeleton.BoneId legacyId, OVRSkeleton.BoneId openXrId)
    {
        OVRSkeleton.SkeletonType type = _skeleton.GetSkeletonType();
        bool useOpenXrIds = type == OVRSkeleton.SkeletonType.XRHandLeft
                             || type == OVRSkeleton.SkeletonType.XRHandRight;
        OVRSkeleton.BoneId targetId = useOpenXrIds ? openXrId : legacyId;

        var bones = _skeleton.Bones;
        for (int i = 0; i < bones.Count; i++)
        {
            if (bones[i].Id == targetId)
            {
                return bones[i].Transform;
            }
        }

        return null;
    }

    private static float DistanceOrZero(Transform a, Transform b)
    {
        if (a == null || b == null) return 0f;
        return Vector3.Distance(a.position, b.position);
    }

    // Góc lệch so với hướng "duỗi thẳng" (localRotation identity), đơn vị độ.
    // Đây là ĐỘ LỚN góc gập (0 = thẳng, càng lớn càng gập) chứ không phân biệt chiều gập.
    // Nếu sau này cần góc có dấu theo 1 trục cụ thể, đổi sang joint.localEulerAngles.x
    // (hoặc .y/.z tuỳ trục nào khớp với chuyển động gập khi bạn quan sát trong Scene view).
    private static float FlexAngleOrZero(Transform joint)
    {
        if (joint == null) return 0f;
        return Quaternion.Angle(Quaternion.identity, joint.localRotation);
    }

    private void WriteRow(HandFrameData d)
    {
        string row = string.Join(",", new[]
        {
            F(Time.time),
            Time.frameCount.ToString(CultureInfo.InvariantCulture),
            _trialId.ToString(CultureInfo.InvariantCulture),
            BoolStr(d.isTracked),
            d.handConfidence.ToString(),
            F(d.pinch[0]), F(d.pinch[1]), F(d.pinch[2]), F(d.pinch[3]), F(d.pinch[4]),
            d.fingerConfidence[0].ToString(), d.fingerConfidence[1].ToString(),
            d.fingerConfidence[2].ToString(), d.fingerConfidence[3].ToString(), d.fingerConfidence[4].ToString(),
            F(d.tipDist[0]), F(d.tipDist[1]), F(d.tipDist[2]), F(d.tipDist[3]), F(d.tipDist[4]),
            F(d.flexIndexMcp), F(d.flexIndexPip), F(d.flexMiddleMcp),
            BoolStr(_isGrabbing), _grabbedObjectName,
            d.objectParentName,
            F(d.objectWorldPos.x), F(d.objectWorldPos.y), F(d.objectWorldPos.z),
            F(d.handWorldPos.x), F(d.handWorldPos.y), F(d.handWorldPos.z)
        });

        _writer.WriteLine(row);
    }

    // Format số thực: 4 chữ số thập phân, luôn dùng dấu chấm (tránh máy để dấu phẩy làm hỏng CSV).
    private static string F(float v) => v.ToString("F4", CultureInfo.InvariantCulture);

    // TRUE/FALSE viết hoa cho dễ lọc trong Excel.
    private static string BoolStr(bool b) => b ? "TRUE" : "FALSE";

    // ----- Nối 2 hàm này vào WhenSelect / WhenUnselect của PointableUnityEventWrapper (xem hướng dẫn Inspector) -----

    public void OnGrabStart(PointerEvent evt)
    {
        _isGrabbing = true;
        _grabbedObjectName = _trackedObjectLabel;
    }

    public void OnGrabEnd(PointerEvent evt)
    {
        _isGrabbing = false;
        _grabbedObjectName = "";
    }

    // ----- Đóng file an toàn -----

    private void OnApplicationQuit()
    {
        CloseWriter();
    }

    private void OnDestroy()
    {
        CloseWriter();
    }

    private void CloseWriter()
    {
        if (_writer == null) return;
        _writer.Flush();
        _writer.Close();
        _writer = null;
    }
}
