using UnityEngine;

/// <summary>
/// Kinematics of ONE virtual finger: 4 joint angles (base spread, base flexion, middle flexion, distal flexion)
/// -> joint positions, and writing the angles to the bones. HandImageFit uses it to fit the virtual hand to the
/// image points; FingerUDPReceiver uses it to curl the middle, ring and little fingers into a fist.
///
/// Fingers only bend the way real joints allow (towards the palm, within angle limits). That is what lets a 2D
/// image (no depth) still give a 3D pose: a short segment on the image is a bone tilted towards or away from the
/// camera, and because joints bend in ONE direction only, a single bend matches the image (Taylor, CVIU 2000).
/// </summary>
public sealed class FingerChainFitter
{
    private const int Bones = 3;
    private const int Params = 4; // [0] base spread, [1] base flexion, [2] middle-joint flexion, [3] distal-joint flexion

    private readonly Transform[] _bones;
    private readonly bool _isThumb;
    private readonly Quaternion[] _rest = new Quaternion[Bones];
    private readonly Quaternion[] _straight = new Quaternion[Bones]; // extra rotation so that angle 0 = STRAIGHT finger (see StraightenFingers)
    private readonly Vector3[] _offset = new Vector3[Bones];   // next joint, in the bone's own frame (before scale)
    private readonly Vector3[] _flexAxis = new Vector3[Bones]; // flexion axis of each bone (local)
    private readonly Vector3 _spreadAxis;                      // spread axis of the base bone (local)
    private readonly float[] _min;
    private readonly float[] _max;
    private readonly float[] _angles = new float[Params];

    /// <summary>Current angles (degrees): spread, base flexion, middle flexion, distal flexion.</summary>
    public float[] Angles => _angles;

    /// <summary>Maximum sideways spread of the four fingers (degrees). A real index finger spreads about 20-30 degrees.</summary>
    public static float FingerSpreadLimit = 30f;

    /// <summary>Four fingers: angle 0 = STRAIGHT finger (middle and distal joints on the line of the base bone). The XRHand
    /// rig's rest pose is NOT straight: the index tip bends back about 8 degrees and about 3 degrees sideways, so with
    /// "angle 0 = rest" the virtual fingertip turned up and a little middle flexion made an S shape. Read when the fitter
    /// is created (false = old behaviour).</summary>
    public static bool StraightenFingers = true;

    /// <param name="bones">The 3 bones from base to tip, IN THE REST POSE (at Awake).</param>
    /// <param name="tip">The fingertip point (child of the last bone).</param>
    /// <param name="palmTarget">A point on the PALM side, used to find the THUMB's flexion axis: flexing = the tip moves closer to it.</param>
    /// <param name="palmNormal">Direction towards the palm side (world), the flexion axis for the four fingers: flexing = the tip moves
    /// STRAIGHT towards the palm side. Default = use palmTarget for the four fingers too (old behaviour).</param>
    public FingerChainFitter(Transform[] bones, Transform tip, bool isThumb, Vector3 palmTarget, Vector3 palmNormal = default)
    {
        _bones = bones;
        _isThumb = isThumb;
        float scale = Mathf.Max(bones[0].lossyScale.x, 1e-6f);
        for (int j = 0; j < Bones; j++)
        {
            _rest[j] = bones[j].localRotation;
            Transform next = j + 1 < Bones ? bones[j + 1] : tip;
            _offset[j] = Quaternion.Inverse(bones[j].rotation) * (next.position - bones[j].position) / scale;
            _straight[j] = Quaternion.identity;
        }
        // Straighten: bone j points along bone j-1 (smallest rotation, in bone j's frame)
        if (!isThumb && StraightenFingers)
            for (int j = 1; j < Bones; j++)
                _straight[j] = Quaternion.FromToRotation(_offset[j], Quaternion.Inverse(_rest[j]) * _offset[j - 1]);

        // Find each bone's flexion axis (no need to know whether the rig uses X or Y):
        //  - four fingers: the axis whose positive rotation moves the tip furthest towards the palm side.
        //  - thumb: the axis that brings the tip closest to the middle of the palm (the thumb bends across the palm).
        // The four fingers used the "closest to the palm centre" rule too, but the index finger sits at the edge of the hand,
        // so for the DISTAL joint bending SIDEWAYS towards the centre was "closer" than bending down: 40 degrees of distal
        // flexion moved the tip 7 mm sideways and 0 mm down, and the virtual index finger curled like a snake.
        bool byNormal = !isThumb && palmNormal.sqrMagnitude > 1e-8f;
        for (int j = 0; j < Bones; j++)
            _flexAxis[j] = byNormal ? FindFlexAxisByNormal(j, palmNormal.normalized) : FindFlexAxis(j, palmTarget);
        Vector3 along = _offset[0].normalized;
        _spreadAxis = Vector3.Cross(along, _flexAxis[0]).normalized;

        // Joint angle limits (degrees; four fingers: relative to the STRAIGHT finger, thumb: relative to the rig's rest pose).
        // The thumb range follows real motion (its base joint only bends back a little).
        _min = isThumb ? new[] { -35f, -20f, -10f, -10f } : new[] { -FingerSpreadLimit, -15f, 0f, -5f };
        _max = isThumb ? new[] { 35f, 45f, 70f, 85f } : new[] { FingerSpreadLimit, 95f, 110f, 90f };
    }

    public float MinAngle(int p) => _min[p];
    public float MaxAngle(int p) => _max[p];
    public bool IsThumb => _isThumb;
    public Transform RootBone => _bones[0];

    public void SetAngles(float spread, float flex0, float flex1, float flex2)
    {
        _angles[0] = spread;
        _angles[1] = flex0;
        _angles[2] = flex1;
        _angles[3] = flex2;
        for (int p = 0; p < Params; p++) _angles[p] = Mathf.Clamp(_angles[p], _min[p], _max[p]);
    }

    /// <summary>Writes the angles to the bones. blend: 1 = current angles, 0 = rest pose.</summary>
    public void Apply(float blend)
    {
        for (int j = 0; j < Bones; j++)
        {
            Quaternion posed = _rest[j] * _straight[j] * LocalDelta(j, _angles);
            _bones[j].localRotation = blend >= 1f ? posed : Quaternion.Slerp(_rest[j], posed, blend);
        }
    }

    /// <summary>Joint positions for angles a, with the finger base at <paramref name="root"/> and the parent bone
    /// (e.g. the hand bone) at rotation <paramref name="parentRot"/>. Pure computation, does not touch any Transform.
    /// joints[0] = finger base, joints[3] = fingertip.</summary>
    public void JointsAt(float[] a, Vector3 root, Quaternion parentRot, Vector3[] joints)
    {
        float scale = _bones[0].lossyScale.x;
        Quaternion rot = parentRot;
        joints[0] = root;
        for (int j = 0; j < Bones; j++)
        {
            rot = rot * _rest[j] * _straight[j] * LocalDelta(j, a);
            joints[j + 1] = joints[j] + rot * (_offset[j] * scale);
        }
    }

    private Quaternion LocalDelta(int bone, float[] a) => bone switch
    {
        0 => Quaternion.AngleAxis(a[0], _spreadAxis) * Quaternion.AngleAxis(a[1], _flexAxis[0]),
        1 => Quaternion.AngleAxis(a[2], _flexAxis[1]),
        _ => Quaternion.AngleAxis(a[3], _flexAxis[2]),
    };

    private Vector3 FindFlexAxis(int bone, Vector3 palmTarget)
    {
        Vector3 along = _offset[bone].normalized;
        Vector3 best = Vector3.right;
        float bestGain = float.NegativeInfinity;
        Vector3[] candidates = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };

        foreach (Vector3 axis in candidates)
        {
            if (Mathf.Abs(Vector3.Dot(axis, along)) > 0.5f) continue; // axis along the bone -> twist only, no flexion
            float before = TipDistance(bone, Quaternion.identity, palmTarget);
            float after = TipDistance(bone, Quaternion.AngleAxis(20f, axis), palmTarget);
            float gain = before - after;
            if (gain > bestGain)
            {
                bestGain = gain;
                best = axis;
            }
        }
        // Make the axis exactly perpendicular to the bone
        return Vector3.ProjectOnPlane(best, along).normalized;
    }

    private Vector3 FindFlexAxisByNormal(int bone, Vector3 palmNormal)
    {
        Vector3 along = _offset[bone].normalized;
        Vector3 best = Vector3.right;
        float bestGain = float.NegativeInfinity;
        Vector3[] candidates = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        Vector3 before = TipPosition(bone, Quaternion.identity);
        foreach (Vector3 axis in candidates)
        {
            if (Mathf.Abs(Vector3.Dot(axis, along)) > 0.5f) continue;
            float gain = Vector3.Dot(TipPosition(bone, Quaternion.AngleAxis(20f, axis)) - before, palmNormal);
            if (gain > bestGain)
            {
                bestGain = gain;
                best = axis;
            }
        }
        return Vector3.ProjectOnPlane(best, along).normalized;
    }

    private float TipDistance(int bone, Quaternion delta, Vector3 target) => Vector3.Distance(TipPosition(bone, delta), target);

    private Vector3 TipPosition(int bone, Quaternion delta)
    {
        Quaternion rot = _bones[0].parent != null ? _bones[0].parent.rotation : Quaternion.identity;
        float scale = _bones[0].lossyScale.x;
        Vector3 p = _bones[0].position;
        for (int j = 0; j < Bones; j++)
        {
            rot = rot * _rest[j] * _straight[j] * (j == bone ? delta : Quaternion.identity);
            p += rot * (_offset[j] * scale);
        }
        return p;
    }

    /// <summary>Direction towards the PALM side (world). Needs to know left or right hand.</summary>
    public static Vector3 PalmNormal(Transform wrist, Transform indexMcp, Transform middleMcp, Transform pinkyMcp, bool leftHand)
    {
        Vector3 fingerDir = middleMcp.position - wrist.position;
        Vector3 across = indexMcp.position - pinkyMcp.position;
        return Vector3.Cross(fingerDir, across).normalized * (leftHand ? -1f : 1f);
    }

    /// <summary>A point on the PALM side (palm centre, 3 cm out), used to find flexion axes.
    /// Needs to know left or right hand to know which side is the palm.</summary>
    public static Vector3 PalmTarget(Transform wrist, Transform indexMcp, Transform middleMcp, Transform pinkyMcp, bool leftHand)
    {
        Vector3 fingerDir = middleMcp.position - wrist.position;
        Vector3 across = indexMcp.position - pinkyMcp.position;
        Vector3 palmFacing = Vector3.Cross(fingerDir, across).normalized * (leftHand ? -1f : 1f);
        Vector3 center = (wrist.position + indexMcp.position + middleMcp.position + pinkyMcp.position) * 0.25f;
        return center + palmFacing * 0.03f;
    }
}
