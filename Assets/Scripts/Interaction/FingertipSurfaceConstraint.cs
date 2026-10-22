using Oculus.Interaction;
using UnityEngine;

/// <summary>
/// Keeps the VIRTUAL fingers from going inside the squeezable objects (SquishyPinchable) and rigid objects.
///
/// The real hand can close because nothing physical stops it, but the virtual hand stops right on the surface (also while
/// the surface is being compressed). Every frame, AFTER the virtual hand has been posed (by HandVisual / FingerUDPReceiver),
/// each finger is checked: if it is inside an object, the finger's SHAPE is locked (the shape just before contact,
/// straightened a little) and only the WHOLE finger rotates about its base joint until it rests on the surface, like a real
/// finger stopped by an object. (Bending each joint separately made fingers curl or bend backwards when squeezing hard.)
///
/// Only the virtual hand's APPEARANCE is changed. The hand data (where the real fingers are) is untouched, so the object still
/// knows how hard you squeeze and compresses by the right amount.
///
/// Put it on the object with the hand's HandVisual (e.g. OVRHandVisualLeft, or StaticHandModel_Right for the glove hand).
/// Bones are found by their XRHand_* names.
/// </summary>
[DefaultExecutionOrder(200)] // after FingerUDPReceiver (0) and SquishyPinchable (100)
public class FingertipSurfaceConstraint : MonoBehaviour
{
    [Tooltip("This hand's HandVisual, to run RIGHT AFTER it has written the hand pose (so it is not overwritten). Empty = find on the same object.")]
    [SerializeField] private HandVisual _handVisual;
    [Tooltip("Objects this hand must NOT go into. Empty = every squeezable object in the scene.\n\n" +
             "A head-attached hand (glove hand) is measured 'in the view plane', a bare hand by its real 3D position -- the object tells them apart, no per-hand list needed.")]
    [SerializeField] private SquishyPinchable[] _objects;
    [Tooltip("Only handle thumb + index (the two squeezing fingers). Off = handle all five fingers.")]
    [SerializeField] private bool _thumbAndIndexOnly = false;
    [Tooltip("Thumb / index tip transforms the objects READ to know this hand is holding them (e.g. LeftThumbTip of IsdkFingertipProxy). " +
             "Empty = this hand's own XRHand_ThumbTip / IndexTip (glove hand). While holding, these two tips are placed on the object's surface.")]
    [SerializeField] private Transform _dataThumbTip;
    [SerializeField] private Transform _dataIndexTip;
    [Tooltip("RIGID objects (PhysicsPinchGrabbable): check EVERY finger segment (not just the tip), so the virtual finger never sticks into the object anywhere. " +
             "Radius = finger thickness (m).")]
    [SerializeField] private float _fingerRadius = 0.008f;
    [Tooltip("How far the finger flesh extends past the XRHand_*Tip point (m, along the finger). Glove hand model: the mesh extends ~9-10 mm past " +
             "the Tip point with a fingertip radius of ~6-9 mm -> extend the finger axis by ~4 mm. 0 = only check up to the Tip point (the virtual fingertip sinks into the object).")]
    [SerializeField] private float _tipExtension = 0.004f;
    [Tooltip("Number of check points on each finger segment (including the segment's end point).")]
    [Range(1, 10)]
    [SerializeField] private int _samplesPerBone = 6;
    [Tooltip("Number of iterations rotating the base joint to push the finger out of the object.")]
    [Range(1, 20)]
    [SerializeField] private int _wholeFingerPasses = 10;

    // Per finger: the rotating bones (base to tip) + the fingertip point.
    private static readonly string[][] ChainNames =
    {
        new[] { "XRHand_ThumbMetacarpal", "XRHand_ThumbProximal", "XRHand_ThumbDistal", "XRHand_ThumbTip" },
        new[] { "XRHand_IndexProximal", "XRHand_IndexIntermediate", "XRHand_IndexDistal", "XRHand_IndexTip" },
        new[] { "XRHand_MiddleProximal", "XRHand_MiddleIntermediate", "XRHand_MiddleDistal", "XRHand_MiddleTip" },
        new[] { "XRHand_RingProximal", "XRHand_RingIntermediate", "XRHand_RingDistal", "XRHand_RingTip" },
        new[] { "XRHand_LittleProximal", "XRHand_LittleIntermediate", "XRHand_LittleDistal", "XRHand_LittleTip" },
    };

    private Transform[][] _chains;

    /// <summary>Number of fingers pushed out of an object in the latest frame.</summary>
    public int ConstrainedFingerCount { get; private set; }

    private void Awake()
    {
        if (_handVisual == null) _handVisual = GetComponent<HandVisual>();
    }

    private void OnEnable()
    {
        if (_handVisual != null) _handVisual.WhenHandVisualUpdated += Apply;
    }

    private void OnDisable()
    {
        if (_handVisual != null) _handVisual.WhenHandVisualUpdated -= Apply;
    }

    // Always also runs in LateUpdate, in case HandVisual's event does not fire (same reason as FingerUDPReceiver).
    // Running twice is harmless: the second pass sees the fingers already on the surface and does nothing.
    private void LateUpdate() => Apply();

    [Header("Finger shape on contact (no curling)")]
    [Tooltip("At the moment of contact, the finger shape (middle / distal joint angles) is KEPT as it was just before contact, then straightened " +
             "by this fraction (0 = keep the old shape, 1 = fully straight). After that only the WHOLE FINGER rotates about its base joint to rest on the surface, " +
             "so squeezing the real hand harder does not make the virtual finger curl or bend backwards.")]
    [Range(0f, 1f)]
    [SerializeField] private float _straightenOnContact = 0.6f;
    [Tooltip("Maximum rotation (degrees) of the base joint away from the real hand to push the finger out of the object.")]
    [SerializeField] private float _maxRootCorrectionDeg = 70f;
    [Tooltip("After leaving the object, the virtual finger returns to the real hand over this time (s).")]
    [SerializeField] private float _releaseBlendSeconds = 0.1f;
    [Tooltip("Keep treating the finger as IN CONTACT for this long (s) after contact ends. The real finger trembles around the surface, so contact used " +
             "to toggle every few frames, and each new contact snapped the finger to the locked shape (60% straightened) -> the thumb jerked just before pinching.")]
    [SerializeField] private float _contactHoldSeconds = 0.12f;
    [Tooltip("While in contact: the virtual finger glides to the corrected shape over this time (s) instead of jumping in one frame.")]
    [SerializeField] private float _contactSmoothSeconds = 0.05f;

    private const int Bones = 3; // 3 rotating segments per finger (+ the fingertip point)

    private sealed class FingerState
    {
        public readonly Quaternion[] Free = new Quaternion[Bones];   // latest real-hand shape while NOT in contact
        public readonly Quaternion[] Frozen = new Quaternion[Bones]; // shape kept during contact
        public readonly Quaternion[] Out = new Quaternion[Bones];    // shape just written out
        public bool HasFree, HasOut, Contact, Blending;
        // Rigid object in contact + the locked face: any part of the finger inside the object is always pushed out through THIS face
        // (not the nearest face -- near a box edge the nearest face flips back and forth and the finger jumps).
        public PhysicsPinchGrabbable FaceBody;
        public int Face = -1;
        public bool Held; // this finger was HOLDING an object in the previous frame
        public readonly Quaternion[] Prev = new Quaternion[Bones];   // shape written out LAST time (before this pass)
        public bool HasPrev;
        public float LastContactTime = -999f;
    }

    private FingerState[] _state;

    // Fingertip position from the REAL hand (right before it is corrected), for objects deciding grab / release.
    private static readonly System.Collections.Generic.Dictionary<Transform, Vector3> s_trackedTip =
        new System.Collections.Generic.Dictionary<Transform, Vector3>();

    /// <summary>Real-hand fingertip position from the latest pass. A fingertip this script does not correct
    /// (e.g. the left hand's data point) returns its current position.</summary>
    public static Vector3 TrackedPosition(Transform tip)
    {
        if (tip == null) return Vector3.zero;
        return s_trackedTip.TryGetValue(tip, out Vector3 p) ? p : tip.position;
    }

    // Full REAL pose (position + rotation) of every thumb/index chain bone, captured on the same line as
    // s_trackedTip, i.e. before this script bends the finger onto the surface. GloveForceEsp32Output needs
    // the real orientation of the fingertip (pad normal) and the real joint positions (dorsal torque).
    private struct TrackedBone { public Pose Pose; public FingertipSurfaceConstraint Owner; }
    private static readonly System.Collections.Generic.Dictionary<Transform, TrackedBone> s_trackedPose =
        new System.Collections.Generic.Dictionary<Transform, TrackedBone>();
    private int _poseFrame = -100; // last frame THIS hand's pass ran (disabled / no objects = stops)

    /// <summary>Real (unconstrained) world pose of a thumb/index chain bone (Metacarpal/Proximal ... Tip)
    /// from the most recent pass. Bones this script never touched, or whose hand's constraint is no
    /// longer running (disabled, no objects in the scene), return the bone's current pose.</summary>
    public static Pose TrackedPose(Transform bone)
    {
        if (bone == null) return Pose.identity;
        if (s_trackedPose.TryGetValue(bone, out TrackedBone e) && e.Owner != null && e.Owner.isActiveAndEnabled &&
            Time.frameCount - e.Owner._poseFrame <= 2)
            return e.Pose;
        return new Pose(bone.position, bone.rotation);
    }
    private readonly Quaternion[] _scratch = new Quaternion[Bones];

    /// <summary>Total time (ms) all FingertipSurfaceConstraints ran in the PREVIOUS frame, written to glove_diag.</summary>
    public static float FrameMs { get; private set; }
    private static int s_msFrame = -1;
    private static float s_msAcc;

    public void Apply()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        ApplyCore();
        float ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000f / System.Diagnostics.Stopwatch.Frequency;
        if (Time.frameCount != s_msFrame) { FrameMs = s_msAcc; s_msAcc = 0f; s_msFrame = Time.frameCount; }
        s_msAcc += ms;
    }

    private void ApplyCore()
    {
        ConstrainedFingerCount = 0;
        System.Collections.Generic.IReadOnlyList<SquishyPinchable> objects =
            _objects != null && _objects.Length > 0 ? _objects : SquishyPinchable.Active;
        if (objects.Count == 0 && PhysicsPinchGrabbable.Active.Count == 0) return;
        EnsureChains();
        _poseFrame = Time.frameCount;
        if (_state == null || _state.Length != _chains.Length)
        {
            _state = new FingerState[_chains.Length];
            for (int i = 0; i < _state.Length; i++) _state[i] = new FingerState();
        }

        int count = _thumbAndIndexOnly ? 2 : _chains.Length;
        bool pinchFingersPosed = false;
        for (int f = 0; f < count; f++)
        {
            Transform[] chain = _chains[f];
            if (chain == null) continue;
            FingerState st = _state[f];

            // This runs twice per frame (HandVisual event + LateUpdate). If the finger shape is still what we just wrote
            // (nobody has reset it), it has already been handled -- skip.
            if (st.HasOut && SameAsOut(chain, st)) continue;
            if (f < 2) pinchFingersPosed = true;
            st.HasPrev = st.HasOut;
            if (st.HasOut) System.Array.Copy(st.Out, st.Prev, Bones);

            Transform tip = chain[Bones];
            if (f < 2) s_trackedTip[tip] = tip.position; // the finger shape now = the real hand just written
            if (f < 2)
                for (int j = 0; j <= Bones; j++)
                    s_trackedPose[chain[j]] = new TrackedBone { Pose = new Pose(chain[j].position, chain[j].rotation), Owner = this };
            // Thumb / index of a HOLDING hand: always placed on the surface (whether inside or away from it).
            Transform dataTip = f == 0 ? (_dataThumbTip != null ? _dataThumbTip : tip)
                              : f == 1 ? (_dataIndexTip != null ? _dataIndexTip : tip) : null;

            // 1. Does the REAL hand (the shape just written) touch any object?
            bool held = HeldTarget(chain, dataTip, objects, out _, out _);
            bool touching = held || Penetrates(chain, objects);
            float now = Time.time;
            if (touching) st.LastContactTime = now;
            // Delayed release of contact: just after leaving the object, stay in contact for _contactHoldSeconds (no toggling)
            bool contact = touching || (st.Contact && now - st.LastContactTime < _contactHoldSeconds);

            if (!contact)
            {
                st.Contact = false;
                st.Held = false;
                st.FaceBody = null;
                st.Face = -1;
                for (int j = 0; j < Bones; j++) st.Free[j] = chain[j].localRotation;
                st.HasFree = true;
                if (st.Blending) BlendBack(chain, st); // just left the object: glide back to the real hand
                SaveOut(chain, st);
                continue;
            }

            // 2. Contact just started: lock the finger shape (the real-hand shape just before contact, straightened a little)
            if (!st.Contact)
            {
                st.Contact = true;
                FreezeShape(chain, st);
            }

            // 3. Keep the locked shape for the middle / distal joints; the base joint starts from the real hand and then
            //    rotates the whole finger out of the object.
            //    While HOLDING continuously: start from the previous frame's finger pose (not the real hand) -> the finger rests
            //    on the object's surface and only rotates as little as the contact point needs; real-finger tracking noise
            //    (the thumb often oscillates) no longer makes the virtual finger open and close.
            Quaternion trackedRoot = chain[0].localRotation;
            bool keepPose = held && st.Held && st.HasOut;
            if (keepPose) chain[0].localRotation = st.Out[0];
            for (int j = 1; j < Bones; j++) chain[j].localRotation = st.Frozen[j];
            LockFace(chain, dataTip, st);
            SolveRoot(chain, dataTip, objects, st);
            if (!held) ClampRoot(chain, trackedRoot); // while holding the target is on the object's surface, no limit needed
            st.Held = held;
            if (st.HasPrev) SmoothToward(chain, st.Prev, _contactSmoothSeconds);

            st.Blending = true;
            ConstrainedFingerCount++;
            SaveOut(chain, st);
        }
        if (pinchFingersPosed) ParallelPinch(objects);
    }

    [Header("Pinching a ball: two parallel fingertips")]
    [Tooltip("While pinching a ball with thumb + index: rotate the middle + distal segments of both fingers so the two DISTAL SEGMENTS are parallel, " +
             "with the finger pads facing each other through the ball centre (like pinching a real round object). 0 = off (fingers follow the real hand), 1 = fully parallel.")]
    [Range(0f, 1f)]
    [SerializeField] private float _parallelPinch = 0.85f;
    [Tooltip("Just grabbed: blend into the parallel pose over this time (s), without a jerk.")]
    [SerializeField] private float _parallelBlendSeconds = 0.15f;
    [Tooltip("Each joint (middle, distal) rotates at most this many degrees to become parallel, so the finger does not bend backwards.")]
    [SerializeField] private float _parallelMaxDeg = 35f;

    private float _parallelWeight;
    private int _parallelFrame = -1;

    /// <summary>Ball pinch in the latest frame: 0 = not checked, 1 = a finger not touching / holding, 2 = both fingers touching but not holding the same ball,
    /// 3 = holding (transition not finished), 4 = the two distal segments point in opposite directions, 5 = old-style parallel, 6 = symmetric pinch (3 joints).</summary>
    public int PinchStatus { get; private set; }

    /// <summary>Pinching the SAME ball with both thumb and index: shared direction t = mean of the two distal segments projected onto the
    /// plane perpendicular to the pinch axis (through the two contact points) -> rotate each finger so its distal segment follows t (half at
    /// the middle joint, half at the distal joint), then rotate the whole finger about its base joint so the pad rests on the ball again.</summary>
    private void ParallelPinch(System.Collections.Generic.IReadOnlyList<SquishyPinchable> objects)
    {
        Transform[] thumb = _chains[0], index = _chains[1];
        PinchStatus = 0;
        if (thumb == null || index == null) return;
        Transform dThumb = _dataThumbTip != null ? _dataThumbTip : thumb[Bones];
        Transform dIndex = _dataIndexTip != null ? _dataIndexTip : index[Bones];
        SquishyPinchable ball = null;
        if (_parallelPinch > 0f && _state[0].Held && _state[1].Held)
            foreach (var obj in objects)
                if (obj != null && obj.isActiveAndEnabled && !obj.MeasuresInViewPlane(dThumb) &&
                    obj.SnapsHeldTip(dThumb) && obj.SnapsHeldTip(dIndex)) { ball = obj; break; }

        if (Time.frameCount != _parallelFrame || !Application.isPlaying) // outside Play mode frameCount stands still (Editor tests)
        {
            _parallelFrame = Time.frameCount;
            _parallelWeight = Mathf.MoveTowards(_parallelWeight, ball != null ? 1f : 0f,
                Time.deltaTime / Mathf.Max(_parallelBlendSeconds, 1e-3f));
        }
        PinchStatus = ball == null ? (_state[0].Held && _state[1].Held ? 2 : 1) : 3;
        if (ball == null || _parallelWeight <= 0f) return;
        // Pinch axis: symmetric pinch -> the ball's stable axis; otherwise the line between the two contact points
        Vector3 grip;
        if (!ball.TryGetPinchTarget(dThumb, _fingerRadius, out _, out grip))
        {
            if (!SquishyHeldTarget(thumb, dThumb, ball, out _, out Vector3 cT) ||
                !SquishyHeldTarget(index, dIndex, ball, out _, out Vector3 cI)) return;
            grip = cI - cT;
            if (grip.sqrMagnitude < 1e-8f) return;
            grip.Normalize();
        }
        Vector3 t = Vector3.ProjectOnPlane(DistalDir(thumb), grip) + Vector3.ProjectOnPlane(DistalDir(index), grip);
        if (t.sqrMagnitude < 1e-4f) { PinchStatus = 4; return; } // the distal segments point opposite ways -- no clear shared direction
        t.Normalize();
        PinchStatus = 5;

        float w = _parallelPinch * _parallelWeight;
        if (ball.TryGetPinchTarget(dThumb, _fingerRadius, out Vector3 tgT, out _) &&
            ball.TryGetPinchTarget(dIndex, _fingerRadius, out Vector3 tgI, out _))
        {
            // Symmetric pinch: each finger solves its 3 joints (bending about its flexion axes) so its pad sits EXACTLY at the bottom of
            // its dent, with the distal segment parallel to the other finger. (Rotating only the base joint left the pad up to 19 mm off
            // target, with the two fingers only 161-164 degrees apart in simulation.)
            PinchStatus = 6;
            if (AdjustPinchShift(ball, thumb, index, tgT, tgI, grip))
            {
                ball.TryGetPinchTarget(dThumb, _fingerRadius, out tgT, out _);
                ball.TryGetPinchTarget(dIndex, _fingerRadius, out tgI, out _);
            }
            Vector3 c = ball.CenterWorld;
            PinchPose(thumb, true, tgT, t, w, c, -grip, _state[0]); // the thumb is on the -grip side
            PinchPose(index, false, tgI, t, w, c, grip, _state[1]);
        }
        else
        {
            for (int pass = 0; pass < 2; pass++)
            {
                AlignDistal(thumb, t, w);
                AlignDistal(index, t, w);
                SolveRoot(thumb, dThumb, objects, _state[0]);
                SolveRoot(index, dIndex, objects, _state[1]);
            }
        }
        SaveOut(thumb, _state[0]);
        SaveOut(index, _state[1]);
    }

    /// <summary>Flexion axis (world) of a joint: the XRHand rig bends the four fingers about local +X, the thumb about +Y.</summary>
    private static Vector3 FlexAxis(Transform joint, bool thumb) => joint.rotation * (thumb ? Vector3.up : Vector3.right);

    [Header("Ball slides so both fingers reach comfortably")]
    [Tooltip("Comfortable reach when pinching, as a fraction of the straight-finger reach (base joint -> pad). If a finger has to reach further, " +
             "the ball slides along the pinch axis (SquishyPinchable.SetPinchShift) until both fingers are within reach -- with the glove there is no " +
             "real ball, so the real fingers close narrower than the virtual ball, and with the ball in the middle the index finger had to stretch fully (hooked shape).")]
    [Range(0.6f, 1f)]
    [SerializeField] private float _comfortReach = 0.88f;
    [Tooltip("Maximum ball slide (m) along the pinch axis.")]
    [SerializeField] private float _maxPinchShift = 0.02f;
    [Tooltip("How slowly the ball slide follows what is needed (s).")]
    [SerializeField] private float _pinchShiftSeconds = 0.15f;

    private float StraightReach(Transform[] chain) =>
        Vector3.Distance(chain[0].position, chain[1].position) + Vector3.Distance(chain[1].position, chain[2].position) +
        (Vector3.Distance(chain[2].position, chain[3].position) + _tipExtension) * _padAlongDistal;

    /// <summary>Computes how far the ball slides along the pinch axis so neither finger reaches beyond its comfortable reach. Sliding the
    /// ball by delta (m, + = towards the index) changes finger X's reach by about gX*delta (gX = cos of the angle between the pinch axis and
    /// base joint -> target); pick the smallest delta that removes the overreach (least squares, with a light penalty on the slide so it returns
    /// to 0 when not needed). true = slid.</summary>
    private bool AdjustPinchShift(SquishyPinchable ball, Transform[] thumb, Transform[] index, Vector3 tgT, Vector3 tgI, Vector3 grip)
    {
        Vector3 vT = tgT - thumb[0].position, vI = tgI - index[0].position;
        float overT = vT.magnitude - _comfortReach * StraightReach(thumb);
        float overI = vI.magnitude - _comfortReach * StraightReach(index);
        float gT = Vector3.Dot(vT.normalized, grip), gI = Vector3.Dot(vI.normalized, grip);
        float shift = ball.PinchShift;
        const float lambda = 0.05f; // light penalty on the slide: when both fingers reach easily the ball returns to the middle of the real fingers
        float num = lambda * shift, den = lambda;
        if (overT > 0f) { num += overT * gT; den += gT * gT; }
        if (overI > 0f) { num += overI * gI; den += gI * gI; }
        float want = Mathf.Clamp(shift - num / den, -_maxPinchShift, _maxPinchShift);
        float dt = Application.isPlaying ? Time.deltaTime : 1f / 72f;
        float next = Mathf.Lerp(shift, want, 1f - Mathf.Exp(-dt / Mathf.Max(_pinchShiftSeconds, 1e-3f)));
        if (Mathf.Abs(next - shift) < 1e-6f) return false;
        ball.SetPinchShift(next);
        return true;
    }

    [Header("Finger shape when pinching a ball")]
    [Tooltip("Index finger: the middle joint must be HIGHER (further from the ball surface) than the distal joint by at least this (m), a natural arch. " +
             "Forcing 'reach the target' used to stretch the middle joint and tip the whole finger down at the base joint -> the middle joint sagged below the distal joint.")]
    [SerializeField] private float _pipAboveDip = 0.003f;
    [Tooltip("Index finger: allowed middle-joint flexion when pinching (degrees), neither straight / bent back nor hooked.")]
    [SerializeField] private Vector2 _pipFlexRange = new Vector2(15f, 95f);
    [Tooltip("Index finger: allowed distal-joint flexion when pinching (degrees).")]
    [SerializeField] private Vector2 _dipFlexRange = new Vector2(0f, 60f);
    [Tooltip("The pinch finger shape glides to each new solution over this time (s), so the finger rests in place without trembling.")]
    [SerializeField] private float _pinchSmoothSeconds = 0.08f;

    /// <summary>Poses one finger pinching the ball. Searches (2 passes: coarse then fine) the middle-joint flexion a + distal-joint flexion b about the
    /// flexion axes; for each pair, simulates the base-joint rotation that brings the pad to the target, then scores:
    ///   reach (base joint -> pad distance = base joint -> target distance), distal segment parallel to the shared direction t,
    ///   index finger: middle joint above the distal joint (_pipAboveDip) + flexion within limits, and small change from the current shape.
    /// Picks the best pair, rotates the base joint, then smooths over time (_pinchSmoothSeconds).</summary>
    private void PinchPose(Transform[] chain, bool thumb, Vector3 target, Vector3 t, float w, Vector3 center, Vector3 outward, FingerState st)
    {
        Transform root = chain[0], mid = chain[Bones - 2], end = chain[Bones - 1];
        for (int pass = 0; pass < 2; pass++)
        {
            float range = pass == 0 ? 40f : 10f, step = pass == 0 ? 5f : 2f;
            Vector3 r = root.position, m = mid.position, e = end.position, tip = chain[Bones].position;
            Vector3 axM = FlexAxis(mid, thumb), axE = FlexAxis(end, thumb);
            Vector3 prox = (m - r).normalized;
            float wantDist = Vector3.Distance(target, r);
            float bestA = 0f, bestB = 0f, bestCost = float.MaxValue;
            for (float a = -range; a <= range + 1e-3f; a += step)
            {
                Quaternion qa = Quaternion.AngleAxis(a, axM);
                Vector3 e1 = m + qa * (e - m), tip1 = m + qa * (tip - m), axE1 = qa * axE;
                Vector3 interDir = (e1 - m).normalized;
                float pipFlex = Vector3.SignedAngle(prox, interDir, axM);
                for (float b = -range; b <= range + 1e-3f; b += step)
                {
                    Vector3 tip2 = e1 + Quaternion.AngleAxis(b, axE1) * (tip1 - e1);
                    Vector3 d = (tip2 - e1).normalized;
                    Vector3 pad = Vector3.Lerp(e1, tip2 + d * _tipExtension, _padAlongDistal);
                    float reach = (Vector3.Distance(pad, r) - wantDist) * 1000f;               // mm
                    Quaternion qr = Quaternion.FromToRotation(pad - r, target - r);            // base-joint rotation step
                    float par = Vector3.Angle(qr * d, t) / 8f;
                    float cost = reach * reach + w * par * par + (a * a + b * b) / (25f * 25f) * 0.5f;
                    if (!thumb)
                    {
                        float hM = Vector3.Dot(qr * (m - r) + r - center, outward);
                        float hE = Vector3.Dot(qr * (e1 - r) + r - center, outward);
                        float low = Mathf.Max(0f, hE + _pipAboveDip - hM) * 1000f;             // mm the middle joint is too low
                        float dipFlex = Vector3.SignedAngle(interDir, d, axE1);
                        float lim = Mathf.Max(0f, _pipFlexRange.x - pipFlex) + Mathf.Max(0f, pipFlex - _pipFlexRange.y)
                                  + Mathf.Max(0f, _dipFlexRange.x - dipFlex) + Mathf.Max(0f, dipFlex - _dipFlexRange.y);
                        cost += 4f * low * low + (lim / 3f) * (lim / 3f);
                    }
                    if (cost < bestCost) { bestCost = cost; bestA = a; bestB = b; }
                }
            }
            mid.rotation = Quaternion.AngleAxis(bestA, axM) * mid.rotation;
            end.rotation = Quaternion.AngleAxis(bestB, FlexAxis(end, thumb)) * end.rotation;
            RotateRoot(root, PadPoint(chain), target);
        }
        if (st.HasPrev) SmoothToward(chain, st.Prev, _pinchSmoothSeconds);
    }

    private static Vector3 DistalDir(Transform[] chain) => (chain[Bones].position - chain[Bones - 1].position).normalized;

    [Tooltip("Pad point placed in the dent when pinching a ball: position along the distal segment, from the distal joint (0) to the end of the fingertip flesh (1).")]
    [Range(0f, 1f)]
    [SerializeField] private float _padAlongDistal = 0.65f;

    /// <summary>Point on the distal segment's axis for the finger pad (between the distal joint and the end of the fingertip flesh).</summary>
    private Vector3 PadPoint(Transform[] chain)
    {
        Vector3 end = chain[Bones].position + DistalDir(chain) * _tipExtension;
        return Vector3.Lerp(chain[Bones - 1].position, end, _padAlongDistal);
    }

    /// <summary>Rotates the finger's distal segment towards direction `t` (by fraction w): half the angle at the middle joint, the rest at the
    /// distal joint, each joint at most _parallelMaxDeg.</summary>
    private void AlignDistal(Transform[] chain, Vector3 t, float w)
    {
        Vector3 want = Vector3.Slerp(DistalDir(chain), t, w);
        for (int step = 0; step < 2; step++)
        {
            Vector3 d = DistalDir(chain);
            Quaternion.FromToRotation(d, want).ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f) angle -= 360f;
            if (Mathf.Abs(angle) < 0.05f || !float.IsFinite(axis.x)) return;
            float part = step == 0 ? angle * 0.5f : angle;
            part = Mathf.Clamp(part, -_parallelMaxDeg, _parallelMaxDeg);
            Transform joint = chain[step == 0 ? Bones - 2 : Bones - 1];
            joint.rotation = Quaternion.AngleAxis(part, axis) * joint.rotation;
        }
    }

    /// <summary>While holding: the point `from` on the virtual finger must move to point `to` on the surface (also when it is away from it).
    /// Ball: the DISTAL SEGMENT point closest to the surface (pad or tip); box: the fingertip.</summary>
    private bool HeldTarget(Transform[] chain, Transform dataTip,
        System.Collections.Generic.IReadOnlyList<SquishyPinchable> objects, out Vector3 from, out Vector3 to)
    {
        from = to = chain[Bones].position;
        if (dataTip == null) return false;
        Vector3 tipPos = chain[Bones].position;
        foreach (var obj in objects)
        {
            if (obj == null || !obj.isActiveAndEnabled) continue;
            if (obj.MeasuresInViewPlane(dataTip))
            {
                if (obj.TryGetHeldContact(dataTip, tipPos, out to)) return true;
            }
            else if (SquishyHeldTarget(chain, dataTip, obj, out from, out to)) return true;
        }
        from = tipPos;
        foreach (var body in PhysicsPinchGrabbable.Active)
            if (body != null && body.TryGetHeldContact(dataTip, tipPos, out to)) return true;
        return false;
    }

    /// <summary>Whether any part of the finger is inside an object, checked along the WHOLE finger (including the flesh past the Tip point).
    /// A hand measured in the view plane (head-attached): balls only check the fingertip, as before.</summary>
    private bool Penetrates(Transform[] chain, System.Collections.Generic.IReadOnlyList<SquishyPinchable> objects)
    {
        foreach (var obj in objects)
        {
            if (obj == null || !obj.isActiveAndEnabled) continue;
            if (obj.MeasuresInViewPlane(chain[Bones]) ? obj.TryResolvePenetration(chain[Bones], out _)
                                                      : DeepestSquishy(chain, obj, out _, out _) > 0f) return true;
        }
        foreach (var body in PhysicsPinchGrabbable.Active)
            if (body != null && DeepestPoint(chain, body, -1, out _, out _) > 0f) return true;
        return false;
    }

    /// <summary>Points on the finger's AXIS: _samplesPerBone per segment, plus 2 points past the fingertip
    /// (_tipExtension). The distal segment's points + the extension are at the end of the array (from DistalStart).</summary>
    private int FillSamples(Transform[] chain)
    {
        int n = 0;
        for (int j = 0; j < Bones; j++)
            for (int k = 1; k <= _samplesPerBone; k++)
                _samples[n++] = Vector3.Lerp(chain[j].position, chain[j + 1].position, (float)k / _samplesPerBone);
        if (_tipExtension > 0f)
        {
            Vector3 tip = chain[Bones].position, dir = tip - chain[Bones - 1].position;
            if (dir.sqrMagnitude > 1e-10f)
            {
                dir.Normalize();
                _samples[n++] = tip + dir * (_tipExtension * 0.5f);
                _samples[n++] = tip + dir * _tipExtension;
            }
        }
        return n;
    }

    private int DistalStart => (Bones - 1) * _samplesPerBone;
    private readonly Vector3[] _samples = new Vector3[Bones * 10 + 2];

    /// <summary>Deepest point along the finger inside the ball (compressed surface). Returns the depth (m), 0 = not inside.</summary>
    private float DeepestSquishy(Transform[] chain, SquishyPinchable obj, out Vector3 point, out Vector3 resolved)
    {
        point = resolved = default;
        float deepest = 0f;
        int n = FillSamples(chain);
        for (int i = 0; i < n; i++)
        {
            float gap = obj.SkinGap(_samples[i], _fingerRadius, out Vector3 s);
            if (-gap > deepest) { deepest = -gap; point = _samples[i]; resolved = s; }
        }
        return deepest;
    }

    /// <summary>Holding a ball: the DISTAL SEGMENT point (pad / tip) closest to the surface -> placed on the surface, unless it is further than
    /// SnapMaxGap. (Placing only the Tip point let the pad and the flesh past the Tip sink into the ball.)</summary>
    private bool SquishyHeldTarget(Transform[] chain, Transform dataTip, SquishyPinchable obj, out Vector3 from, out Vector3 to)
    {
        from = to = chain[Bones].position;
        if (!obj.SnapsHeldTip(dataTip)) return false;
        // Symmetric pinch: a FIXED PAD POINT placed at the bottom of this finger's dent (the two fingers face each other through the ball centre).
        // A fixed point does not jump between points the way "the point closest to the surface" does every frame.
        if (obj.TryGetPinchTarget(dataTip, _fingerRadius, out to, out _))
        {
            from = PadPoint(chain);
            return true;
        }
        int n = FillSamples(chain);
        float best = float.PositiveInfinity;
        for (int i = DistalStart; i < n; i++)
        {
            float gap = obj.SkinGap(_samples[i], _fingerRadius, out Vector3 s);
            if (gap < best) { best = gap; from = _samples[i]; to = s; }
        }
        return best <= obj.SnapMaxGap;
    }

    /// <summary>Locks the rigid-object face for this finger: while holding -> the face chosen at grab time; on first contact ->
    /// the face near the deepest point, kept until the finger leaves the object.</summary>
    private void LockFace(Transform[] chain, Transform dataTip, FingerState st)
    {
        foreach (var body in PhysicsPinchGrabbable.Active)
        {
            if (body == null) continue;
            int held = body.HeldFace(dataTip);
            if (held >= 0) { st.FaceBody = body; st.Face = held; return; }
        }
        if (st.FaceBody != null && st.FaceBody.isActiveAndEnabled) return;
        st.FaceBody = null;
        st.Face = -1;
        foreach (var body in PhysicsPinchGrabbable.Active)
            if (body != null && DeepestPoint(chain, body, -1, out Vector3 q, out _) > 0f)
            {
                st.FaceBody = body;
                st.Face = body.FaceOf(q);
                return;
            }
    }

    /// <summary>Deepest point along the finger inside a rigid object. Returns the depth (m), 0 = not inside.</summary>
    private float DeepestPoint(Transform[] chain, PhysicsPinchGrabbable body, int face, out Vector3 point, out Vector3 resolved)
    {
        point = resolved = default;
        float deepest = 0f;
        int n = FillSamples(chain);
        for (int i = 0; i < n; i++)
        {
            Vector3 q = _samples[i];
            if (!body.ResolvePoint(q, _fingerRadius, face, out Vector3 r)) continue;
            float d = (r - q).sqrMagnitude;
            if (d > deepest) { deepest = d; point = q; resolved = r; }
        }
        return Mathf.Sqrt(deepest);
    }

    /// <summary>Rotates the WHOLE FINGER about its base joint (shape unchanged) until the fingertip rests on the
    /// surface (while holding) and no part of it is inside an object.</summary>
    private void SolveRoot(Transform[] chain, Transform dataTip,
        System.Collections.Generic.IReadOnlyList<SquishyPinchable> objects, FingerState st)
    {
        Transform root = chain[0];
        for (int iter = 0; iter < _wholeFingerPasses; iter++)
        {
            bool moved = false;
            if (HeldTarget(chain, dataTip, objects, out Vector3 from, out Vector3 held))
                moved |= RotateRoot(root, from, held);
            foreach (var obj in objects)
            {
                if (obj == null || !obj.isActiveAndEnabled) continue;
                if (obj.MeasuresInViewPlane(chain[Bones]))
                {
                    if (obj.TryResolvePenetration(chain[Bones], out Vector3 t)) moved |= RotateRoot(root, chain[Bones].position, t);
                }
                else if (DeepestSquishy(chain, obj, out Vector3 q, out Vector3 r) > 0f)
                    moved |= RotateRoot(root, q, r);
            }
            foreach (var body in PhysicsPinchGrabbable.Active)
                if (body != null && DeepestPoint(chain, body, body == st.FaceBody ? st.Face : -1, out Vector3 q, out Vector3 r) > 0f)
                    moved |= RotateRoot(root, q, r);
            if (!moved) break;
        }
    }

    private static bool RotateRoot(Transform root, Vector3 from, Vector3 to)
    {
        Vector3 a = from - root.position, b = to - root.position;
        if (a.sqrMagnitude < 1e-10f || b.sqrMagnitude < 1e-10f || (to - from).sqrMagnitude < 1e-8f) return false;
        root.rotation = Quaternion.FromToRotation(a, b) * root.rotation;
        return true;
    }

    private void ClampRoot(Transform[] chain, Quaternion trackedRoot)
    {
        float angle = Quaternion.Angle(trackedRoot, chain[0].localRotation);
        if (angle > _maxRootCorrectionDeg)
            chain[0].localRotation = Quaternion.Slerp(trackedRoot, chain[0].localRotation, _maxRootCorrectionDeg / angle);
    }

    /// <summary>Locks the finger shape at first contact: the real-hand shape just before contact (if known), then straightens
    /// each joint by _straightenOnContact (computed geometrically, without needing the rig's rest pose).</summary>
    private void FreezeShape(Transform[] chain, FingerState st)
    {
        for (int j = 0; j < Bones; j++) _scratch[j] = chain[j].localRotation;
        if (st.HasFree)
            for (int j = 1; j < Bones; j++) chain[j].localRotation = st.Free[j];

        for (int j = 1; j < Bones; j++)
        {
            Vector3 parentDir = chain[j].position - chain[j - 1].position;
            Vector3 dir = chain[j + 1].position - chain[j].position;
            if (parentDir.sqrMagnitude < 1e-10f || dir.sqrMagnitude < 1e-10f) continue;
            Quaternion straighten = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(dir, parentDir), _straightenOnContact);
            chain[j].rotation = straighten * chain[j].rotation;
        }
        for (int j = 0; j < Bones; j++) st.Frozen[j] = chain[j].localRotation;
        for (int j = 0; j < Bones; j++) chain[j].localRotation = _scratch[j];
    }

    /// <summary>The newly computed finger shape -> glide from the previous shape towards it (exponential filter, time constant `seconds`).</summary>
    private static void SmoothToward(Transform[] chain, Quaternion[] prev, float seconds)
    {
        if (seconds <= 0f) return;
        float dt = Application.isPlaying ? Time.deltaTime : 1f / 72f;
        float k = 1f - Mathf.Exp(-dt / seconds);
        for (int j = 0; j < Bones; j++) chain[j].localRotation = Quaternion.Slerp(prev[j], chain[j].localRotation, k);
    }

    private void BlendBack(Transform[] chain, FingerState st)
    {
        float k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(_releaseBlendSeconds, 1e-3f));
        bool done = true;
        for (int j = 0; j < Bones; j++)
        {
            Quaternion tracked = chain[j].localRotation;
            chain[j].localRotation = Quaternion.Slerp(st.Out[j], tracked, k);
            if (Quaternion.Angle(chain[j].localRotation, tracked) > 0.5f) done = false;
        }
        if (done) st.Blending = false;
    }

    private static void SaveOut(Transform[] chain, FingerState st)
    {
        for (int j = 0; j < Bones; j++) st.Out[j] = chain[j].localRotation;
        st.HasOut = true;
    }

    private static bool SameAsOut(Transform[] chain, FingerState st)
    {
        for (int j = 0; j < Bones; j++)
            if (Quaternion.Angle(chain[j].localRotation, st.Out[j]) > 0.01f) return false;
        return true;
    }

    /// <summary>Finds the bones by name, again if the skeleton in use was deactivated --
    /// HandVisual can switch between two skeletons (OpenXR / Oculus) at run time.</summary>
    private void EnsureChains()
    {
        if (_chains != null && _chains[0] != null && _chains[0][0].gameObject.activeInHierarchy) return;

        _chains = new Transform[ChainNames.Length][];
        for (int f = 0; f < ChainNames.Length; f++)
        {
            var chain = new Transform[ChainNames[f].Length];
            bool complete = true;
            for (int j = 0; j < chain.Length; j++)
            {
                chain[j] = FindActiveDeepChild(transform, ChainNames[f][j]);
                if (chain[j] == null) { complete = false; break; }
            }
            _chains[f] = complete ? chain : null;
        }
    }

    private static Transform FindActiveDeepChild(Transform parent, string name)
    {
        foreach (Transform child in parent)
        {
            if (!child.gameObject.activeInHierarchy) continue;
            if (child.name == name) return child;
            Transform result = FindActiveDeepChild(child, name);
            if (result != null) return result;
        }
        return null;
    }
}
