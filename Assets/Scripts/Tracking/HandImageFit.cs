using UnityEngine;

/// <summary>
/// Fits the virtual hand (wrist + thumb + index) TO THE IMAGE: every point the model finds in the image is a RAY from
/// the camera; find the wrist position / rotation and the joint angles so that each virtual joint lies on its ray.
///
/// The image is the main source and is solved once PER IMAGE. The Quest adds how far the hand is from the camera (its depth
/// for the gloved hand agrees with a two-camera measurement once the virtual hand has the glove's size, see ImageHandSolver "Glove size").
///
/// A 2D image has no depth, so it is completed with what is known for sure: bones have a fixed length (a short segment
/// in the image = tilted towards / away from the camera), joints only bend one way within limits, the hand does not flip
/// in 0.1 s (stay close to the previous answer), and the depth is close to the (smoothed) Quest depth.
///
/// Solved with Levenberg-Marquardt over 14 unknowns. Plain C# (no MonoBehaviour) so it can be tested in the Editor on recordings.
/// </summary>
public sealed class HandImageFit
{
    public const int Points = 9; // 0 wrist, 1-4 thumb (base -> tip), 5-8 index
    public const int Knuckles = 3; // base joints of the middle, ring and little fingers (the model's points 9, 13, 17)
    private const int NP = 14;   // [0..2] wrist offset (cm), [3..5] wrist rotation (degrees, world rotation vector), [6..9] thumb, [10..13] index
    // 9 points, 9 points of the second camera, depth, 14 temporal, 6 hyperextension, 8 pose prior learned from the bare hand,
    // 2 hidden bend, 3 pinch, 3 knuckles (side only; second slot unused)
    private const int NR = 2 * Points + 2 * Points + 1 + 3 + 3 + 8 + 6 + 8 + 2 + 3 + 2 * Knuckles;

    // Weights (unit: image error measured in PALM LENGTHS at the hand's depth).
    // 10 pixels of error is about 0.05. Static so they can be tuned on recordings.
    // Per palm length of depth difference from the Quest. Synthetic test (10 px noise, Quest depth jittering 4 cm, correctly sized
    // hand): 0.2 gave the lowest median errors; 0.5-1.0 cut the worst tilted-view errors but followed a 3 cm Quest bias too closely.
    public static float DepthPriorWeight = 0.2f;
    // Weight of the second camera's points (SetSecondView) relative to the main camera's. Two cameras 6.4 cm apart see the hand from
    // two angles, so the depth of every joint is MEASURED instead of guessed (one camera: hand tilted ~15 degrees, joints 3-5 cm off
    // in depth, two-camera recordings 29/10).
    public static float SecondViewWeight = 1f;
    // Weight of the three KNUCKLES (base joints of the middle, ring and little fingers; SetKnuckleView) relative to the 9 points.
    // The 9 points (wrist, thumb, index) fit a palm facing the camera and one facing away equally well (the thumb reaches across),
    // so the solver could turn the hand over: recording 30/10 14:20, palm up -> the virtual curled fingers on the thumb side. The
    // knuckles show which side of the index the other fingers are; noisy (curled in the glove) and not exactly on the virtual
    // knuckles, so they only decide the SIDE: penalty when a virtual knuckle is not at least KnuckleSideFraction of the model
    // knuckle's distance on the same side of the index base line (points 5 -> 6), used only when the model knuckle is clearly to
    // one side (KnuckleMinSide palm lengths; seen from the back of the hand the model puts them on the index itself).
    // Replay lab 30/10 14:20, palm up: virtual palm on the right side 77 -> 87% of the time, index fit +0.3-0.9 px, both-camera
    // scores unchanged. (Fitting them like the 9 points: 77 -> 98%, but the index fit 11 -> 15 px.)
    public static float KnuckleWeight = 3f;
    public static float KnuckleMinSide = 0.15f, KnuckleSideFraction = 0.5f;
    public static float TemporalPosWeight = 0.4f;    // per palm length of change from the previous image (simulated 8 px noise: 0.05 -> jumps in 56/145 images, 0.4 -> 0)
    public static float TemporalRotWeight = 0.012f;  // per degree of change from the previous image (recording: 0.006 -> rotation > 5 degrees/frame 102 times, 0.012 -> 38)
    public static float TemporalAngleWeight = 0.006f;
    public static float PinchWeight = 2f;            // fingertips further apart than PinchContactDistance while the image shows them together
    public static float PinchContactDistance = 0.016f;
    public static float PinchNearOnImage = 0.2f;     // distance between the two fingertips in the image / |wrist - index base|
    public static float PinchFarOnImage = 0.45f;
    // Switch to another interpretation only when its (static) cost is below this fraction of the current one. Replay lab 29/10 (4 recordings):
    // 0.7 -> the raw hand flipped > 40 degrees 9-13 times a minute; 0.35 (with ReseedRatio 0.5) -> 2-6, 3D error vs the second camera equal or lower.
    public static float SwitchRatio = 0.35f;
    // The answer that continues from the previous image fits worse than this (image error, palm lengths) -> try EVERY interpretation
    // in this same image. Recording 29/10 16:31: turning the hand palm up, the points were right but the solver stayed in the
    // previous interpretation (image error 0.23-0.29 for seconds; it only tried one other interpretation per image).
    public static float ReseedError = 0.10f;
    // ...and the full reseed only switches when the new interpretation is at least this much better (static cost ratio; 1 = any lower).
    public static float ReseedRatio = 0.5f;
    // ...unless the current answer fits really badly (image error above this): then any better interpretation is taken at once
    // (a real turn-over was otherwise followed 2 images late, recording 29/10 18:45 image 884).
    public static float ReseedAnyError = 0.15f;
    // Where the model's WRIST point (point 0) lies relative to the virtual hand's wrist joint (wrist frame, m). The model places it on
    // the glove cuff, below the joint (set by ImageHandSolver "Glove size").
    public static Vector3 WristPointLocal = Vector3.zero;
    // Weight of the wrist point in the image fit: it sits on the glove cuff, and its distance to the index base changes with the
    // wrist bend (two-camera measurement 29/10: 9.6-14 cm between recordings vs a fixed 12.4 cm on the virtual hand).
    public static float WristPointWeight = 1f;
    // How far the model's INDEX BASE point (point 5) is from the virtual index base joint (wrist frame, m).
    // The glove model places point 5 near point 9 (between the base joints), about 2 cm towards the little finger (+X of the
    // XRHand wrist). Two recordings with a 2 cm offset: median index spread -30 -> -4 degrees, thumb hyperextension 23% -> 1% of
    // the time, image error 0.063/0.068 -> 0.061/0.065. 30/10, with PointOffset: 1 cm fits both cameras better (29/10 16:31: index
    // bend error 35 -> 23 degrees, index direction 18 -> 14; 15:59: 29 -> 20, 17 -> 13), image error unchanged on today's recordings.
    public static Vector3 IndexMcpPointLocal = new Vector3(0.01f, 0f, 0f);
    // Size of the PALM (wrist point, thumb base, index base) relative to the fingers. Two cameras, recording 29/10 16:31 (glove size
    // 1.1): fingers as long as on the glove, but the palm triangle 13-17% too big (wrist-index base 13.7 vs 11.7 cm, thumb base-index
    // base 6.6 vs 5.8 cm) -> to fit the image the solver tilted the oversized palm ~15 degrees.
    public static float PalmScale = 1f;
    // Where each model point lies relative to its virtual joint, beyond WristPointLocal / IndexMcpPointLocal (m at Scale 1). The
    // glove model finds the points on the glove's plastic rings, not on the finger bones. Points 0, 1, 5: in the wrist frame.
    // Finger points 2-4, 6-8: in the frame of the bone ENDING at that joint, x = along the bone, y = the bend axis, z = x cross y,
    // so the offset bends with the finger. Measured with two cameras (GloveReplayLab + tools/analysis/calibrate_glove_points.py,
    // recording 29/10 16:31, round 2 -- later rounds drifted: the cuff point is not fixed to the hand). Replay lab 30/10: image error of
    // the solve 17-18 -> 8-10 px on today's one-camera recordings (index middle joint ~30 -> 4-9 px); against both cameras (16:31)
    // hand tilt error 14 -> 9 degrees, wrist depth error -4.7 -> -0.9 cm.
    public static Vector3[] PointOffset =
    {
        new Vector3(0.0142f, 0.0318f, 0.0204f),  // 0 wrist (glove cuff)
        new Vector3(-0.0026f, 0.0058f, 0.0051f), // 1 thumb base
        new Vector3(0.0063f, 0.0041f, -0.0069f), // 2
        new Vector3(0.0034f, -0.0007f, 0.0016f), // 3
        new Vector3(0.0031f, -0.0033f, 0.0020f), // 4 thumb tip
        Vector3.zero,                            // 5 index base: fixed (IndexMcpPointLocal), anchors the hand
        new Vector3(0.0009f, 0.0117f, -0.0037f), // 6 index middle joint
        new Vector3(0.0020f, 0.0039f, -0.0014f), // 7
        new Vector3(0.0028f, -0.0003f, 0.0015f), // 8 index tip
    };
    private const float HyperextensionWeight = 0.006f;

    // HAND POSE LEARNED FROM THE BARE HAND (HandPoseRecorder: Quest-tracked left hand, two sessions, 13959 frames, 1704 frames of
    // tight pinch; converted to glove-hand joint angles, about 0.6-1.3 degrees off per segment). Two pinch styles: one with the base
    // joint bent most (47/37/18), one (holding the virtual ball) with the middle joint bent most (19/47/33); the covariance is wide
    // enough for both. Model: mean angles interpolated by pinch level (open -> pinched) + an 8-angle covariance (thumb, index: spread /
    // base / middle / distal). Penalty = Mahalanobis distance -> joints move together like a real hand.
    // Re-learned after FingerChainFitter.StraightenFingers (angle 0 = STRAIGHT finger).
    // HIDDEN BEND of the index finger: 3D flexion the image does NOT show (a finger bending towards / away from the camera still
    // looks straight). One camera cannot tell, so the solver could add hidden bend and still match the image; seen from the eyes
    // (a different angle than the camera) the finger looks crooked. Penalty = (3D bend - bend in the image) at the middle and distal
    // joints, per degree, only when the image shows a straight finger (StraightOnImage).
    public static float HiddenBendWeight = 0.016f;
    // Only penalised when the IMAGE shows a straight finger: middle + distal segments (points 6-7-8) barely bent and NOT
    // foreshortened (a finger bending towards the camera looks shorter).
    public static float StraightBendFull = 8f, StraightBendZero = 15f;      // bend in the image (degrees) at point 7
    public static float StraightLenZero = 0.22f, StraightLenFull = 0.30f;   // (|6-7| + |7-8|) / |0-5| in the image
    public static float PosePriorWeight = 0.02f;     // per standard deviation (2 SD ~ 0.04 palm lengths of image error)
    private static readonly float[] PoseMeanOpen = { 0.8791f, 4.2680f, 27.0197f, 0.9191f, 2.7536f, 11.5433f, 18.6156f, 1.0288f };
    private static readonly float[] PoseMeanPinch = { 7.0680f, 11.1249f, 38.0017f, 15.8167f, 2.2528f, 18.5744f, 47.1248f, 24.5913f };
    private static readonly float[,] PoseWhiten = // inverse Cholesky factor of the covariance matrix (unit: 1/degree)
    {
        { 0.0670f, 0.0000f, 0.0000f, 0.0000f, 0.0000f, 0.0000f, 0.0000f, 0.0000f },
        { 0.0236f, 0.1975f, 0.0000f, 0.0000f, 0.0000f, 0.0000f, 0.0000f, 0.0000f },
        { -0.0074f, 0.0392f, 0.1342f, 0.0000f, 0.0000f, 0.0000f, 0.0000f, 0.0000f },
        { -0.0255f, -0.0233f, -0.1364f, 0.1442f, 0.0000f, 0.0000f, 0.0000f, 0.0000f },
        { 0.0238f, -0.0537f, 0.0245f, -0.0116f, 0.2629f, 0.0000f, 0.0000f, 0.0000f },
        { 0.0496f, -0.0119f, 0.0081f, 0.0123f, -0.0563f, 0.0913f, 0.0000f, 0.0000f },
        { 0.0355f, -0.0108f, -0.0217f, -0.0143f, 0.0412f, 0.0032f, 0.1173f, 0.0000f },
        { -0.0627f, -0.0315f, 0.0120f, -0.0200f, 0.0055f, 0.0025f, -0.1728f, 0.3121f },
    };
    private readonly float[] _poseDev = new float[8];
    private Vector3 _indexMcpJoint; // the REAL index base joint (point 5 in the image is offset from it, see IndexMcpPointLocal)

    private readonly FingerChainFitter _thumb, _index;
    private Vector3 _thumbRootL, _indexRootL;                  // finger bases in the wrist frame (m, with Scale)
    private readonly Quaternion _thumbParentL, _indexParentL;  // rotation of each finger base's parent bone, in the wrist frame
    private readonly float[] _min = new float[8], _max = new float[8];

    /// <summary>Wrist -> index base of the virtual hand (m).</summary>
    public float PalmLength { get; private set; }
    /// <summary>Current hand size relative to creation time (HandVisual changes the hand size at run time).</summary>
    public float Scale { get; private set; } = 1f;
    private readonly float _palmLength0;
    private readonly Vector3 _thumbRootL0, _indexRootL0;
    private float _palmScaleSet = 1f;

    // Data of the image being solved
    private Vector3 _camPos;
    private Quaternion _camRot, _camInv;
    private readonly Vector2[] _target = new Vector2[Points]; // normalised coordinates x/z, y/z in the camera frame
    private readonly float[] _weight = new float[Points];
    private float _imgScale;
    // Second camera (only for the next Solve; see SetSecondView)
    private bool _useView2;
    private Vector3 _cam2Pos;
    private Quaternion _cam2Inv;
    private readonly Vector2[] _target2 = new Vector2[Points];
    private readonly float[] _weight2 = new float[Points];
    // Knuckles: rest positions in the wrist frame (SetKnuckleJoints) and the image targets of the next Solve (SetKnuckleView)
    private bool _hasKnuckleJoints, _useKnuckles;
    private readonly Vector3[] _knuckleL0 = new Vector3[Knuckles], _knuckleL = new Vector3[Knuckles];
    private readonly Vector2[] _ktarget = new Vector2[Knuckles];
    private readonly float[] _kweight = new float[Knuckles];
    private readonly float[] _kside = new float[Knuckles], _kdist = new float[Knuckles]; // side (+-1) and distance of each model knuckle
    private Vector2 _kLineO, _kLineD;                                                     // index base line in the image (point 5 -> 6)
    private float _kPalmImg;
    private float _depthPrior;
    private bool _useDepthPrior;
    private bool _useTemporal;
    private Vector3 _prevP;
    private Quaternion _prevR;
    private readonly float[] _prevA = new float[8];
    private Vector3 _baseP;
    private Quaternion _baseR;

    /// <summary>Result: the wrist (world).</summary>
    public Vector3 Position { get; private set; }
    public Quaternion Rotation { get; private set; } = Quaternion.identity;
    /// <summary>[0..3] thumb, [4..7] index: spread, base flexion, middle flexion, distal flexion (degrees).</summary>
    public readonly float[] Angles = new float[8];
    public bool HasSolution { get; private set; }
    /// <summary>Mean image fit error (palm lengths; 0.05 is about 4-5 mm).</summary>
    public float ImageError { get; private set; }
    public float LastCost { get; private set; }
    public float Pinch { get; private set; }
    public int Switches { get; private set; }
    /// <summary>Number of images where every interpretation was tried again because the fit was bad (ReseedError).</summary>
    public int Reseeds { get; private set; }
    /// <summary>Depth of the index base (camera view axis, m) in the result.</summary>
    public float Depth { get; private set; }
    /// <summary>0..1: how clearly the image shows a straight index finger (see StraightBendFull); scales the hidden bend penalty.</summary>
    public float StraightOnImage { get; private set; }

    private int _nextSeed;
    private static readonly float[,] FingerSeeds =
    {
        { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f },        // straight
        { 0f, 15f, 30f, 20f, 0f, 30f, 40f, 25f },  // half closed / about to pinch
        { 0f, 30f, 50f, 40f, 0f, 60f, 80f, 50f },  // pinched
    };
    // Seed rotations (degrees): x = tilt about the horizontal axis in the image, y = turn about the wrist -> index base axis
    // (+-180 = the hand turned over: palm towards / away from the camera)
    private static readonly Vector2[] TiltSeeds =
        { Vector2.zero, new Vector2(35f, 0f), new Vector2(-35f, 0f), new Vector2(0f, 70f), new Vector2(0f, -70f), new Vector2(0f, 180f) };

    private readonly float[] _x = new float[NP], _trial = new float[NP], _a8 = new float[8], _bestA = new float[8];
    private readonly float[] _r = new float[NR], _rTry = new float[NR];
    private readonly float[,] _jac = new float[NR, NP];
    private readonly float[,] _normal = new float[NP, NP + 1], _sys = new float[NP, NP + 1];
    private readonly float[] _a4 = new float[4];
    private readonly Vector3[] _chain = new Vector3[4];
    private readonly Vector3[] _model = new Vector3[Points];
    private readonly Vector3[] _joint = new Vector3[Points]; // the virtual joints themselves (0 = wrist joint, 5 = real index base joint)
    private static readonly float[] Step = { 0.05f, 0.05f, 0.05f, 0.2f, 0.2f, 0.2f, 0.3f, 0.3f, 0.3f, 0.3f, 0.3f, 0.3f, 0.3f, 0.3f };
    private static readonly float[] MaxStep = { 5f, 5f, 5f, 20f, 20f, 20f, 25f, 25f, 25f, 25f, 25f, 25f, 25f, 25f };

    /// <summary>Reads the virtual hand's shape IN ITS REST POSE (call at Awake, together with creating the two FingerChainFitters).</summary>
    public HandImageFit(Transform wrist, FingerChainFitter thumb, FingerChainFitter index)
    {
        _thumb = thumb;
        _index = index;
        Quaternion inv = Quaternion.Inverse(wrist.rotation);
        Transform tr = thumb.RootBone, ir = index.RootBone;
        _thumbRootL = _thumbRootL0 = inv * (tr.position - wrist.position);
        _indexRootL = _indexRootL0 = inv * (ir.position - wrist.position);
        _thumbParentL = inv * (tr.parent != null ? tr.parent.rotation : wrist.rotation);
        _indexParentL = inv * (ir.parent != null ? ir.parent.rotation : wrist.rotation);
        PalmLength = _palmLength0 = Mathf.Max(_indexRootL.magnitude, 0.03f);
        for (int p = 0; p < 4; p++)
        {
            _min[p] = thumb.MinAngle(p); _max[p] = thumb.MaxAngle(p);
            _min[4 + p] = index.MinAngle(p); _max[4 + p] = index.MaxAngle(p);
        }
    }

    /// <summary>Sets the hand size relative to creation time. HandVisual (Quest) changes the hand root scale at run time;
    /// the finger bones (FingerChainFitter) read the CURRENT size, so the palm here must follow the same scale. (It used to be
    /// captured once at start-up (0.9) while the drawn hand became ~1.0 -> the solver built a hand ~10% smaller, placed it closer to
    /// the camera, and seen from the eyes the virtual hand looked bigger and offset from the real hand.)</summary>
    public void SetScale(float k)
    {
        if (k <= 0f || (Mathf.Abs(k - Scale) < 1e-4f && _palmScaleSet == PalmScale)) return;
        Scale = k;
        _palmScaleSet = PalmScale;
        _thumbRootL = _thumbRootL0 * (k * PalmScale);
        for (int n = 0; n < Knuckles; n++) _knuckleL[n] = _knuckleL0[n] * (k * PalmScale);
        _indexRootL = _indexRootL0 * (k * PalmScale);
        PalmLength = _palmLength0 * (k * PalmScale);
    }

    public void Reset()
    {
        HasSolution = false;
    }

    /// <summary>Adds the same 9 points seen by a second camera to the NEXT Solve only (pose at capture time, rays in world space,
    /// trust 0..1 per point).</summary>
    public void SetSecondView(Vector3 camPos, Quaternion camRot, Vector3[] rayDirs, float[] weights)
    {
        _cam2Pos = camPos;
        _cam2Inv = Quaternion.Inverse(camRot);
        int used = 0;
        for (int i = 0; i < Points; i++)
        {
            Vector3 d = _cam2Inv * rayDirs[i];
            bool ok = d.z > 1e-3f && weights[i] > 0f;
            _target2[i] = ok ? new Vector2(d.x / d.z, d.y / d.z) : Vector2.zero;
            _weight2[i] = ok ? weights[i] * SecondViewWeight * (i == 0 ? WristPointWeight : 1f) : 0f;
            if (ok) used++;
        }
        _useView2 = used >= 3;
    }

    /// <summary>Reads where the middle, ring and little finger base joints are in the wrist frame (rig in its rest pose, right after
    /// creating this object).</summary>
    public void SetKnuckleJoints(Transform wrist, Transform[] proximals)
    {
        if (wrist == null || proximals == null || proximals.Length < Knuckles) return;
        Quaternion inv = Quaternion.Inverse(wrist.rotation);
        for (int k = 0; k < Knuckles; k++)
        {
            if (proximals[k] == null) return;
            _knuckleL0[k] = inv * (proximals[k].position - wrist.position) / Scale;
            _knuckleL[k] = _knuckleL0[k] * (Scale * PalmScale);
        }
        _hasKnuckleJoints = true;
    }

    /// <summary>The model's knuckle points (9, 13, 17) as world rays from the main camera, for the NEXT Solve only (trust 0..1 each).</summary>
    public void SetKnuckleView(Vector3[] rayDirs, float[] weights)
    {
        for (int k = 0; k < Knuckles; k++)
        {
            _kdir[k] = rayDirs[k];
            _kweight[k] = weights[k];
        }
        _useKnuckles = _hasKnuckleJoints;
    }
    private readonly Vector3[] _kdir = new Vector3[Knuckles];

    /// <summary>Solves one image.</summary>
    /// <param name="camPos">Camera position AT CAPTURE TIME.</param>
    /// <param name="camRot">Camera rotation at capture time.</param>
    /// <param name="rayDirs">Ray directions (world) from the camera through the 9 image points.</param>
    /// <param name="weights">Trust 0..1 of each point (0 = ignore). Points 0 and 5 are required.</param>
    /// <param name="depthPrior">Hand depth from the Quest (camera view axis, m); &lt;= 0 = none.</param>
    /// <param name="warm">The previous answer is recent (a few hundred ms) -> start from it and stay close to it.</param>
    public bool Solve(Vector3 camPos, Quaternion camRot, Vector3[] rayDirs, float[] weights, float depthPrior, bool warm)
    {
        _camPos = camPos;
        _camRot = camRot;
        _camInv = Quaternion.Inverse(camRot);
        for (int i = 0; i < Points; i++)
        {
            Vector3 d = _camInv * rayDirs[i];
            bool ok = d.z > 1e-3f && weights[i] > 0f;
            _target[i] = ok ? new Vector2(d.x / d.z, d.y / d.z) : Vector2.zero;
            _weight[i] = ok ? weights[i] : 0f;
        }
        if (_weight[0] <= 0f || _weight[5] <= 0f) { _useView2 = _useKnuckles = false; return false; }
        if (_useKnuckles)
        {
            int used = 0;
            for (int k = 0; k < Knuckles; k++)
            {
                Vector3 d = _camInv * _kdir[k];
                bool ok = d.z > 1e-3f && _kweight[k] > 0f;
                _ktarget[k] = ok ? new Vector2(d.x / d.z, d.y / d.z) : Vector2.zero;
                if (!ok) _kweight[k] = 0f;
                else used++;
            }
            _useKnuckles = used > 0 && KnuckleWeight > 0f;
        }
        _weight[0] *= WristPointWeight;

        float palmOnImage = Vector2.Distance(_target[0], _target[5]);
        if (_useKnuckles) KnuckleSides(palmOnImage);
        float gap = Vector2.Distance(_target[4], _target[8]) / Mathf.Max(palmOnImage, 1e-4f);
        Pinch = _weight[4] > 0f && _weight[8] > 0f
            ? Mathf.Clamp01((PinchFarOnImage - gap) / Mathf.Max(PinchFarOnImage - PinchNearOnImage, 1e-3f)) : 0f;
        StraightOnImage = StraightGate(palmOnImage);

        warm &= HasSolution;
        _useDepthPrior = depthPrior > 0.05f;
        _depthPrior = depthPrior;
        _useTemporal = warm;
        if (warm)
        {
            _prevP = Position;
            _prevR = Rotation;
            System.Array.Copy(Angles, _prevA, 8);
        }
        float depth = _useDepthPrior ? depthPrior : HasSolution ? Depth : 0.4f;
        _imgScale = depth / PalmLength;

        Quaternion rotGuess = HasSolution ? Rotation : FacingCamera();
        float bestCost = float.PositiveInfinity;
        Vector3 bestP = Vector3.zero;
        Quaternion bestR = Quaternion.identity;

        if (warm)
        {
            bestCost = RefineFrom(Position, Rotation, Angles, out bestP, out bestR, _bestA);
            // Each image also tries one other interpretation (cycling finger seed x tilt seed): a 2D image often has two
            // similar-looking poses, and sometimes the other one is CLEARLY better. Fair comparison: both interpretations are solved
            // WITHOUT the temporal penalty (the current one continues from its own place) before comparing; with it the new
            // interpretation could never win, and comparing against the temporally pulled answer made the hand flip back and forth.
            int combos = FingerSeeds.GetLength(0) * TiltSeeds.Length;
            int c = _nextSeed;
            _nextSeed = (_nextSeed + 1) % combos;
            _useTemporal = false;
            float warmStatic = RefineFrom(bestP, bestR, _bestA, out _, out _, _a8);
            float cost = TrySeed(rotGuess, depth, c / TiltSeeds.Length, c % TiltSeeds.Length, out Vector3 p, out Quaternion r);
            bool switched = cost < warmStatic * SwitchRatio;
            if (switched)
            {
                bestCost = cost; bestP = p; bestR = r;
                System.Array.Copy(_a8, _bestA, 8);
                warmStatic = cost;
            }
            // Still a bad fit (e.g. the hand just turned over): try every interpretation in this image and keep the best
            if (ImageErrorOf(bestP, bestR, _bestA) > ReseedError)
            {
                Reseeds++;
                float bar = ImageErrorOf(bestP, bestR, _bestA) > ReseedAnyError ? warmStatic : warmStatic * ReseedRatio;
                for (int s = 0; s < FingerSeeds.GetLength(0); s++)
                    for (int t = 0; t < TiltSeeds.Length; t++)
                    {
                        cost = TrySeed(rotGuess, depth, s, t, out p, out r);
                        if (cost < bar && cost < warmStatic)
                        {
                            warmStatic = cost;
                            bestCost = cost; bestP = p; bestR = r;
                            System.Array.Copy(_a8, _bestA, 8);
                            switched = true;
                        }
                    }
            }
            if (switched) Switches++;
            _useTemporal = true;
        }
        else
        {
            for (int s = 0; s < FingerSeeds.GetLength(0); s++)
                for (int t = 0; t < TiltSeeds.Length; t++)
                {
                    float cost = TrySeed(rotGuess, depth, s, t, out Vector3 p, out Quaternion r);
                    if (cost < bestCost)
                    {
                        bestCost = cost; bestP = p; bestR = r;
                        System.Array.Copy(_a8, _bestA, 8);
                    }
                }
        }

        Position = bestP;
        Rotation = bestR;
        System.Array.Copy(_bestA, Angles, 8);
        LastCost = bestCost;
        HasSolution = true;
        ImageError = ImageErrorOf(Position, Rotation, Angles);
        Depth = (_camInv * (_model[5] - _camPos)).z;
        _useView2 = _useKnuckles = false;
        return true;
    }

    /// <summary>Which side of the index base line (points 5 -> 6) each model knuckle is on; knuckles too close to the line are dropped.</summary>
    private void KnuckleSides(float palmOnImage)
    {
        _useKnuckles = false;
        Vector2 d = _target[6] - _target[5];
        if (_weight[6] <= 0f || d.sqrMagnitude < 1e-10f || palmOnImage < 1e-4f) return;
        _kLineO = _target[5];
        _kLineD = d.normalized;
        _kPalmImg = palmOnImage;
        for (int n = 0; n < Knuckles; n++)
        {
            if (_kweight[n] <= 0f) continue;
            Vector2 q = _ktarget[n] - _kLineO;
            float side = (_kLineD.x * q.y - _kLineD.y * q.x) / palmOnImage;
            if (Mathf.Abs(side) < KnuckleMinSide) { _kweight[n] = 0f; continue; }
            _kside[n] = Mathf.Sign(side);
            _kdist[n] = Mathf.Abs(side);
            _useKnuckles = true;
        }
    }

    /// <summary>Image fit error alone (without the constraints) of a pose, palm lengths. Leaves _model at that pose.</summary>
    private float ImageErrorOf(Vector3 p, Quaternion r, float[] a)
    {
        _baseP = p; _baseR = r;
        for (int k = 0; k < 6; k++) _x[k] = 0f;
        System.Array.Copy(a, 0, _x, 6, 8);
        Evaluate(_x, _r);
        float sum = 0f, n = 0f;
        for (int i = 0; i < Points; i++)
        {
            if (_weight[i] <= 0f) continue;
            sum += (_r[2 * i] * _r[2 * i] + _r[2 * i + 1] * _r[2 * i + 1]) / (_weight[i] * _weight[i]);
            n += 1f;
        }
        return Mathf.Sqrt(sum / Mathf.Max(n, 1f));
    }

    /// <summary>Sets the current answer (e.g. for testing: start from a wrong pose the headset chose).</summary>
    public void SetSolution(Vector3 p, Quaternion r, float[] a)
    {
        Position = p;
        Rotation = r;
        System.Array.Copy(a, Angles, 8);
        HasSolution = true;
    }

    /// <summary>The 9 joint positions of the current result (world), for drawing / checking.</summary>
    public void GetModelPoints(Vector3[] outPoints) => ComputePoints(Position, Rotation, Angles, outPoints);

    /// <summary>The 9 joint positions for any wrist (p, r) and finger angles a[8].</summary>
    public void ComputePoints(Vector3 p, Quaternion r, float[] a, Vector3[] outPoints)
    {
        _baseP = p; _baseR = r;
        for (int k = 0; k < 6; k++) _x[k] = 0f;
        System.Array.Copy(a, 0, _x, 6, 8);
        ModelPoints(_x, out _, out _);
        System.Array.Copy(_model, outPoints, Points);
    }

    private Quaternion FacingCamera()
    {
        // No hint at all: index finger pointing up in the image, palm facing the camera
        return Quaternion.LookRotation(_camRot * Vector3.up, -(_camRot * Vector3.forward));
    }

    /// <summary>Places the virtual hand along the rays of points 0 and 5 with one finger seed and one tilt seed, then solves.</summary>
    private float TrySeed(Quaternion rotGuess, float depth, int fingerSeed, int tiltSeed, out Vector3 p, out Quaternion r)
    {
        Vector3 camFwd = _camRot * Vector3.forward;
        // Rotate in the image plane so the wrist -> index base direction matches the image
        Vector3 imgDir = _camRot * new Vector3(_target[5].x - _target[0].x, _target[5].y - _target[0].y, 0f);
        Vector3 palm = Vector3.ProjectOnPlane(rotGuess * _indexRootL, camFwd);
        Quaternion rot = rotGuess;
        if (palm.sqrMagnitude > 1e-8f && imgDir.sqrMagnitude > 1e-10f)
            rot = Quaternion.FromToRotation(palm, imgDir) * rot;
        // Seed rotation: tilt about the horizontal axis (perpendicular to the palm direction in the image), turn about
        // the wrist -> index base axis
        Vector3 across = Vector3.Cross(camFwd, imgDir);
        Vector2 seed = TiltSeeds[tiltSeed];
        if (across.sqrMagnitude > 1e-10f && seed.x != 0f)
            rot = Quaternion.AngleAxis(seed.x, across.normalized) * rot;
        Vector3 palmAxis = rot * _indexRootL;
        if (palmAxis.sqrMagnitude > 1e-10f && seed.y != 0f)
            rot = Quaternion.AngleAxis(seed.y, palmAxis.normalized) * rot;
        // Index base on the ray of point 5 at the hinted depth
        Vector3 x5 = _camPos + _camRot * (new Vector3(_target[5].x, _target[5].y, 1f) * depth);
        Vector3 p0 = x5 - rot * _indexRootL;
        for (int k = 0; k < 8; k++) _a8[k] = FingerSeeds[fingerSeed, k];
        return RefineFrom(p0, rot, _a8, out p, out r, _a8);
    }

    private float RefineFrom(Vector3 p0, Quaternion r0, float[] a0, out Vector3 p, out Quaternion r, float[] aOut)
    {
        _baseP = p0;
        _baseR = r0;
        for (int k = 0; k < 6; k++) _x[k] = 0f;
        System.Array.Copy(a0, 0, _x, 6, 8);
        ClampAngles(_x);
        float cost = Refine(_x);
        Pose(_x, out p, out r);
        System.Array.Copy(_x, 6, aOut, 0, 8);
        return cost;
    }

    private void Pose(float[] x, out Vector3 p, out Quaternion r)
    {
        p = _baseP + new Vector3(x[0], x[1], x[2]) * 0.01f;
        Vector3 rv = new Vector3(x[3], x[4], x[5]);
        float ang = rv.magnitude;
        r = ang > 1e-6f ? Quaternion.AngleAxis(ang, rv / ang) * _baseR : _baseR;
    }

    private void ModelPoints(float[] x, out Vector3 p, out Quaternion r)
    {
        Pose(x, out p, out r);
        _joint[0] = p;
        for (int k = 0; k < 4; k++) _a4[k] = x[6 + k];
        _thumb.JointsAt(_a4, p + r * _thumbRootL, r * _thumbParentL, _chain);
        for (int k = 0; k < 4; k++) _joint[1 + k] = _chain[k];
        for (int k = 0; k < 4; k++) _a4[k] = x[10 + k];
        _index.JointsAt(_a4, p + r * _indexRootL, r * _indexParentL, _chain);
        for (int k = 0; k < 4; k++) _joint[5 + k] = _chain[k];
        _indexMcpJoint = _chain[0];
        for (int i = 0; i < Points; i++) _model[i] = PointFromJoints(i, _joint, r, Scale * PalmScale, Scale);
    }

    /// <summary>Where the model's point i lies for the given virtual joints (0 = wrist joint, 1-4 thumb, 5-8 index) and wrist rotation:
    /// the joint plus WristPointLocal / IndexMcpPointLocal / PointOffset. palmScale scales the palm points (0, 1, 5), fingerScale the rest.</summary>
    public static Vector3 PointFromJoints(int i, Vector3[] joints, Quaternion wrist, float palmScale, float fingerScale)
    {
        Vector3 o = PointOffset[i];
        if (i == 0) return joints[0] + wrist * ((WristPointLocal + o) * palmScale);
        if (i == 5) return joints[5] + wrist * ((IndexMcpPointLocal + o) * palmScale);
        if (o == Vector3.zero) return joints[i];
        if (i == 1) return joints[1] + wrist * (o * palmScale);
        Vector3 along = joints[i] - joints[i - 1];
        if (along.sqrMagnitude < 1e-10f) return joints[i];
        along.Normalize();
        // Bend axis: index (and the other fingers) bend about the wrist's +X, the thumb about +Y
        Vector3 hinge = wrist * (i < 5 ? Vector3.up : Vector3.right);
        Vector3 side = hinge - along * Vector3.Dot(along, hinge);
        if (side.sqrMagnitude < 1e-10f) return joints[i];
        side.Normalize();
        Vector3 up = Vector3.Cross(along, side);
        return joints[i] + (along * o.x + side * o.y + up * o.z) * fingerScale;
    }

    /// <summary>The 9 virtual joints of the current result (world; no point offsets), for calibrating PointOffset.</summary>
    public void GetJointPoints(Vector3[] outJoints)
    {
        ComputePoints(Position, Rotation, Angles, _model);
        System.Array.Copy(_joint, outJoints, Points);
    }

    /// <summary>The 9 virtual joints (no point offsets) for any wrist (p, r) and finger angles a[8].</summary>
    public void ComputeJoints(Vector3 p, Quaternion r, float[] a, Vector3[] outJoints)
    {
        ComputePoints(p, r, a, _model);
        System.Array.Copy(_joint, outJoints, Points);
    }

    private static Vector3 RotVec(Quaternion q)
    {
        if (q.w < 0f) { q.x = -q.x; q.y = -q.y; q.z = -q.z; q.w = -q.w; }
        Vector3 v = new Vector3(q.x, q.y, q.z);
        float s = v.magnitude;
        if (s < 1e-7f) return Vector3.zero;
        float ang = 2f * Mathf.Atan2(s, q.w) * Mathf.Rad2Deg;
        return v * (ang / s);
    }

    private float Evaluate(float[] x, float[] r)
    {
        ModelPoints(x, out Vector3 p, out Quaternion rot);
        int k = 0;
        for (int i = 0; i < Points; i++)
        {
            Vector3 c = _camInv * (_model[i] - _camPos);
            float z = Mathf.Max(c.z, 0.02f);
            Vector2 e = (new Vector2(c.x / z, c.y / z) - _target[i]) * (_imgScale * _weight[i]);
            r[k++] = e.x;
            r[k++] = e.y;
        }
        for (int i = 0; i < Points; i++)
        {
            if (!_useView2 || _weight2[i] <= 0f) { r[k++] = 0f; r[k++] = 0f; continue; }
            Vector3 c = _cam2Inv * (_model[i] - _cam2Pos);
            float z = Mathf.Max(c.z, 0.02f);
            Vector2 e = (new Vector2(c.x / z, c.y / z) - _target2[i]) * (_imgScale * _weight2[i]);
            r[k++] = e.x;
            r[k++] = e.y;
        }

        float zi = (_camInv * (_model[5] - _camPos)).z;
        r[k++] = _useDepthPrior ? DepthPriorWeight * (zi - _depthPrior) / PalmLength : 0f;

        Vector3 v;
        if (_useTemporal)
        {
            v = (p - _prevP) * (TemporalPosWeight / PalmLength);
            r[k++] = v.x; r[k++] = v.y; r[k++] = v.z;
            v = RotVec(rot * Quaternion.Inverse(_prevR)) * TemporalRotWeight;
            r[k++] = v.x; r[k++] = v.y; r[k++] = v.z;
            for (int a = 0; a < 8; a++) r[k++] = TemporalAngleWeight * (x[6 + a] - _prevA[a]);
        }
        else
        {
            for (int a = 0; a < 14; a++) r[k++] = 0f;
        }

        // No hyperextension (thumb, index)
        for (int f = 0; f < 2; f++)
            for (int a = 1; a < 4; a++) r[k++] = HyperextensionWeight * Mathf.Min(0f, x[6 + 4 * f + a]);
        // Pose prior learned from the bare hand (only decides when the image cannot tell)
        for (int i = 0; i < 8; i++) _poseDev[i] = x[6 + i] - Mathf.Lerp(PoseMeanOpen[i], PoseMeanPinch[i], Pinch);
        for (int i = 0; i < 8; i++)
        {
            float w = 0f;
            for (int c = 0; c <= i; c++) w += PoseWhiten[i, c] * _poseDev[c];
            r[k++] = PosePriorWeight * w;
        }
        // Hidden bend of the index finger (middle and distal joints)
        float hw = HiddenBendWeight * StraightOnImage;
        r[k++] = hw > 0f ? hw * HiddenBend(_indexMcpJoint, _joint[6], _joint[7]) : 0f;
        r[k++] = hw > 0f ? hw * HiddenBend(_joint[6], _joint[7], _joint[8]) : 0f;

        // Knuckles (middle, ring, little finger base joints) on their rays
        for (int n = 0; n < Knuckles; n++)
        {
            r[k + 1] = 0f;
            if (!_useKnuckles || _kweight[n] <= 0f) { r[k] = 0f; k += 2; continue; }
            Vector3 c = _camInv * (p + rot * _knuckleL[n] - _camPos);
            float z = Mathf.Max(c.z, 0.02f);
            Vector2 q = new Vector2(c.x / z, c.y / z) - _kLineO;
            float side = (_kLineD.x * q.y - _kLineD.y * q.x) / _kPalmImg;
            r[k] = KnuckleWeight * _kweight[n] * Mathf.Max(0f, KnuckleSideFraction * _kdist[n] - _kside[n] * side);
            k += 2;
        }

        // Pinch: the image shows the two fingertips together -> the virtual fingertips must touch (a 2D image does not tell
        // each finger's depth; this condition mostly fixes the depth)
        Vector3 gap = _joint[8] - _joint[4];
        float d = gap.magnitude;
        v = Pinch > 0f && d > PinchContactDistance
            ? gap * ((1f - PinchContactDistance / d) * PinchWeight * Pinch / PalmLength) : Vector3.zero;
        r[k++] = v.x; r[k++] = v.y; r[k++] = v.z;

        float sum = 0f;
        for (int i = 0; i < NR; i++) sum += r[i] * r[i];
        return sum;
    }

    private float StraightGate(float palmOnImage)
    {
        if (_weight[6] <= 0f || _weight[7] <= 0f || _weight[8] <= 0f || palmOnImage < 1e-4f) return 0f;
        Vector2 u = _target[7] - _target[6], w = _target[8] - _target[7];
        float bend = Mathf.Abs(Mathf.Atan2(u.x * w.y - u.y * w.x, Vector2.Dot(u, w))) * Mathf.Rad2Deg;
        float len = (u.magnitude + w.magnitude) / palmOnImage;
        return Mathf.Clamp01((StraightBendZero - bend) / (StraightBendZero - StraightBendFull)) *
               Mathf.Clamp01((len - StraightLenZero) / (StraightLenFull - StraightLenZero));
    }

    /// <summary>3D bend at b (degrees) minus the bend of its projection in the image: the part of the bend the camera cannot see.</summary>
    private float HiddenBend(Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 u = b - a, w = c - b;
        float bend3 = Mathf.Atan2(Vector3.Cross(u, w).magnitude, Vector3.Dot(u, w)) * Mathf.Rad2Deg;
        Vector2 pa = Project(a), pb = Project(b), pc = Project(c);
        Vector2 u2 = pb - pa, w2 = pc - pb;
        float bend2 = Mathf.Abs(Mathf.Atan2(u2.x * w2.y - u2.y * w2.x, Vector2.Dot(u2, w2))) * Mathf.Rad2Deg;
        return Mathf.Max(0f, bend3 - bend2);
    }

    private Vector2 Project(Vector3 world)
    {
        Vector3 c = _camInv * (world - _camPos);
        float z = Mathf.Max(c.z, 0.02f);
        return new Vector2(c.x / z, c.y / z);
    }

    private void ClampAngles(float[] x)
    {
        for (int a = 0; a < 8; a++) x[6 + a] = Mathf.Clamp(x[6 + a], _min[a], _max[a]);
    }

    // Levenberg-Marquardt with finite-difference derivatives (14 unknowns)
    private float Refine(float[] x)
    {
        float cost = Evaluate(x, _r);
        float lambda = 1e-2f;
        for (int iter = 0; iter < 15; iter++)
        {
            for (int q = 0; q < NP; q++)
            {
                System.Array.Copy(x, _trial, NP);
                _trial[q] += Step[q];
                Evaluate(_trial, _rTry);
                float inv = 1f / Step[q];
                for (int i = 0; i < NR; i++) _jac[i, q] = (_rTry[i] - _r[i]) * inv;
            }
            for (int a = 0; a < NP; a++)
            {
                for (int b = a; b < NP; b++)
                {
                    float s = 0f;
                    for (int i = 0; i < NR; i++) s += _jac[i, a] * _jac[i, b];
                    _normal[a, b] = s;
                    _normal[b, a] = s;
                }
                float g = 0f;
                for (int i = 0; i < NR; i++) g += _jac[i, a] * _r[i];
                _normal[a, NP] = -g;
            }

            bool improved = false;
            for (int attempt = 0; attempt < 4 && !improved; attempt++)
            {
                System.Array.Copy(_normal, _sys, _normal.Length);
                for (int a = 0; a < NP; a++) _sys[a, a] += lambda * (_normal[a, a] + 1e-6f);
                if (!SolveLinear(_sys)) { lambda *= 10f; continue; }
                for (int a = 0; a < NP; a++) _trial[a] = x[a] + Mathf.Clamp(_sys[a, NP], -MaxStep[a], MaxStep[a]);
                ClampAngles(_trial);
                float trialCost = Evaluate(_trial, _rTry);
                if (trialCost < cost)
                {
                    bool converged = cost - trialCost < 1e-7f;
                    System.Array.Copy(_trial, x, NP);
                    System.Array.Copy(_rTry, _r, NR);
                    cost = trialCost;
                    lambda = Mathf.Max(lambda * 0.3f, 1e-4f);
                    improved = true;
                    if (converged) return cost;
                }
                else lambda *= 10f;
            }
            if (!improved) break;
        }
        return cost;
    }

    private static bool SolveLinear(float[,] m)
    {
        int n = NP;
        for (int c = 0; c < n; c++)
        {
            int pivot = c;
            for (int r = c + 1; r < n; r++) if (Mathf.Abs(m[r, c]) > Mathf.Abs(m[pivot, c])) pivot = r;
            if (Mathf.Abs(m[pivot, c]) < 1e-12f) return false;
            if (pivot != c)
                for (int k = c; k <= n; k++) (m[c, k], m[pivot, k]) = (m[pivot, k], m[c, k]);
            for (int r = 0; r < n; r++)
            {
                if (r == c) continue;
                float f = m[r, c] / m[c, c];
                if (f == 0f) continue;
                for (int k = c; k <= n; k++) m[r, k] -= f * m[c, k];
            }
        }
        for (int r = 0; r < n; r++) m[r, n] /= m[r, r];
        return true;
    }
}
