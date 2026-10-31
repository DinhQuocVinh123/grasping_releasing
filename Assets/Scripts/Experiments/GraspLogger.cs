using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem; // Keyboard, to read the Space key
using Oculus.Interaction; // PointerEvent, PointableUnityEventWrapper

/// <summary>
/// Logs bare-hand tracking data and the grab state to a CSV file, one row per frame
/// (Application.persistentDataPath/GraspLog_*.csv).
///
/// Setup: assign the OVRHand and OVRSkeleton of ONE hand, and connect OnGrabStart / OnGrabEnd to the
/// WhenSelect / WhenUnselect events of the object's PointableUnityEventWrapper. Space starts a new trial.
/// </summary>
public class GraspLogger : MonoBehaviour
{
    [Header("Hand data source (OVRHand and OVRSkeleton of ONE hand)")]
    [Tooltip("The OVRHand component on the left/right Hand Tracking object under the CameraRig.")]
    [SerializeField] private OVRHand _hand;

    [Tooltip("The OVRSkeleton component on the same object as the OVRHand above.")]
    [SerializeField] private OVRSkeleton _skeleton;

    [Header("Label of the tracked object (written to the grabbedObject column)")]
    [SerializeField] private string _trackedObjectLabel = "Sphere";

    [Tooltip("The tracked object itself; its parent and world position are logged every frame (to find 'state leaks').")]
    [SerializeField] private Transform _trackedObject;

    // Current grab state, set by OnGrabStart / OnGrabEnd, which are connected to
    // WhenSelect / WhenUnselect of PointableUnityEventWrapper in the Inspector.
    private bool _isGrabbing = false;
    private string _grabbedObjectName = "";

    private StreamWriter _writer;
    private int _framesSinceFlush = 0;
    private const int FLUSH_INTERVAL_FRAMES = 60;

    private int _trialId = 0;

    private void Start()
    {
        // Timestamp in the file name so runs do not overwrite each other.
        string fileName = $"GraspLog_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        string fullPath = Path.Combine(Application.persistentDataPath, fileName);

        _writer = new StreamWriter(fullPath, false, new UTF8Encoding(false));
        WriteHeader();

        // Print the full path so the file can be found.
        Debug.Log($"[GraspLogger] Logging to: {fullPath}");
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
        // Space = start a new trial (trialId + 1).
        // Uses the new Input System (Keyboard.current): the project has the old UnityEngine.Input turned off
        // (Project Settings > Player > Active Input Handling = Input System Package).
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            _trialId++;
            Debug.Log($"[GraspLogger] New trial: {_trialId}");
        }

        if (!TryGatherHandData(out HandFrameData data))
        {
            // OVRHand/OVRSkeleton not initialised yet (start of Play, or a short tracking loss) -> skip this frame.
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

    // All data of one frame, to keep the parameter lists short.
    private struct HandFrameData
    {
        public bool isTracked;
        public OVRHand.TrackingConfidence handConfidence;
        public float[] pinch; // order: Thumb, Index, Middle, Ring, Pinky
        public OVRHand.TrackingConfidence[] fingerConfidence;
        public float[] tipDist;
        public float flexIndexMcp;
        public float flexIndexPip;
        public float flexMiddleMcp;

        // For investigating "state leaks": the object stays attached to the hand although isGrabbing = false.
        public string objectParentName; // name of the object's parent transform, "None" if it has none
        public Vector3 objectWorldPos;  // the object's real world position
        public Vector3 handWorldPos;    // world position of the wrist (stands for the hand position)
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

        // OVRSkeleton.IsInitialized = false means the Bones list is not ready yet (start of Play).
        // Reading _skeleton.Bones now would throw.
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

        // New builds use the OpenXR hand skeleton (XRHand_*) by default, not the old Hand_* names.
        // FindBone picks the right set from GetSkeletonType(), so nothing has to be changed by hand.
        Transform wrist = FindBone(OVRSkeleton.BoneId.Hand_WristRoot, OVRSkeleton.BoneId.XRHand_Wrist);
        Transform thumbTip = FindBone(OVRSkeleton.BoneId.Hand_ThumbTip, OVRSkeleton.BoneId.XRHand_ThumbTip);
        Transform indexTip = FindBone(OVRSkeleton.BoneId.Hand_IndexTip, OVRSkeleton.BoneId.XRHand_IndexTip);
        Transform middleTip = FindBone(OVRSkeleton.BoneId.Hand_MiddleTip, OVRSkeleton.BoneId.XRHand_MiddleTip);
        Transform ringTip = FindBone(OVRSkeleton.BoneId.Hand_RingTip, OVRSkeleton.BoneId.XRHand_RingTip);
        // Note: in the OpenXR names the little finger is "Little", not "Pinky".
        Transform pinkyTip = FindBone(OVRSkeleton.BoneId.Hand_PinkyTip, OVRSkeleton.BoneId.XRHand_LittleTip);

        data.tipDist = new float[5];
        data.tipDist[0] = DistanceOrZero(wrist, thumbTip);
        data.tipDist[1] = DistanceOrZero(wrist, indexTip);
        data.tipDist[2] = DistanceOrZero(wrist, middleTip);
        data.tipDist[3] = DistanceOrZero(wrist, ringTip);
        data.tipDist[4] = DistanceOrZero(wrist, pinkyTip);

        // MCP = finger base joint (proximal), PIP = middle joint (intermediate).
        Transform indexMcp = FindBone(OVRSkeleton.BoneId.Hand_Index1, OVRSkeleton.BoneId.XRHand_IndexProximal);
        Transform indexPip = FindBone(OVRSkeleton.BoneId.Hand_Index2, OVRSkeleton.BoneId.XRHand_IndexIntermediate);
        Transform middleMcp = FindBone(OVRSkeleton.BoneId.Hand_Middle1, OVRSkeleton.BoneId.XRHand_MiddleProximal);

        data.flexIndexMcp = FlexAngleOrZero(indexMcp);
        data.flexIndexPip = FlexAngleOrZero(indexPip);
        data.flexMiddleMcp = FlexAngleOrZero(middleMcp);

        // Hand position from the wrist bone found above (independent of isTracked, so it is still logged
        // during a tracking loss and the last "frozen" position can be compared).
        data.handWorldPos = wrist != null ? wrist.position : Vector3.zero;

        // State of the tracked object, logged whatever isGrabbing says, to catch the object staying attached
        // to the hand after the game logic released it (state leak).
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

    // Finds a bone by Id, choosing the old (OVR) or new (OpenXR) Id set depending on which skeleton the OVRSkeleton
    // runs (Project Settings > Meta XR > OVRManager > Hand Skeleton Version).
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

    // Angle away from the "straight" direction (localRotation identity), in degrees.
    // This is the MAGNITUDE of flexion (0 = straight, larger = more bent), without the bend direction.
    // For a signed angle about one axis, use joint.localEulerAngles.x
    // (or .y / .z, whichever axis matches the bending motion in the Scene view).
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

    // Number format: 4 decimals, always a dot (a comma decimal separator would break the CSV).
    private static string F(float v) => v.ToString("F4", CultureInfo.InvariantCulture);

    // Upper-case TRUE/FALSE, easy to filter in Excel.
    private static string BoolStr(bool b) => b ? "TRUE" : "FALSE";

    // ----- Connect these two to WhenSelect / WhenUnselect of PointableUnityEventWrapper -----

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

    // ----- Close the file safely -----

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
