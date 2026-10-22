using UnityEngine;

/// <summary>
/// Fits the virtual hand (wrist + thumb + index) TO THE IMAGE: every point the model finds in the image is a RAY from
/// the camera; find the wrist position / rotation and the joint angles so that each virtual joint lies on its ray.
///
/// The image is the main source and is solved once PER IMAGE. The Quest only adds what the image cannot tell (how far
/// the hand is from the camera, palm up or down), as weak constraints. (An earlier approach built on the QUEST wrist,
/// which is wrong and jittery with the glove, and re-solved 60 times per second against that jittery wrist.)
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
    private const int NP = 14;   // [0..2] wrist offset (cm), [3..5] wrist rotation (degrees, world rotation vector), [6..9] thumb, [10..13] index
    private const int NR = 2 * Points + 1 + 3 + 3 + 3 + 8 + 6 + 8 + 2 + 3 + 3; // ... + 6 hyperextension, 8 pose prior learned from the bare hand, 2 hidden bend, 3 hexagon plate, 3 pinch

    // Weights (unit: image error measured in PALM LENGTHS at the hand's depth).
    // 10 pixels of error is about 0.05. Static so they can be tuned on recordings.
    public static float DepthPriorWeight = 0.1f;     // per palm length of depth difference from the Quest
    public static float RotPriorWeight = 0.0015f;    // per degree from the Quest rotation (the Quest jitters 30-50 degrees -> only to choose palm up / down)
    public static float TemporalPosWeight = 0.4f;    // per palm length of change from the previous image (simulated 8 px noise: 0.05 -> jumps in 56/145 images, 0.4 -> 0)
    public static float TemporalRotWeight = 0.012f;  // per degree of change from the previous image (recording: 0.006 -> rotation > 5 degrees/frame 102 times, 0.012 -> 38)
    public static float TemporalAngleWeight = 0.006f;
    public static float PinchWeight = 2f;            // fingertips further apart than PinchContactDistance while the image shows them together
    public static float PinchContactDistance = 0.016f;
    public static float PinchNearOnImage = 0.2f;     // distance between the two fingertips in the image / |wrist - index base|
    public static float PinchFarOnImage = 0.45f;
    public static float SwitchRatio = 0.7f;          // switch to another interpretation when its error is at least 30% lower
    // Coloured hexagon plate on the base segments of the three curled fingers (found by colour in Python): point 10. The 9
    // wrist / thumb / index points say almost nothing about how the hand is TURNED about the wrist -> index base axis
    // (only point 1 is ~2 cm off that axis, and it is the point the model places worst) -> the virtual hand often tilted sideways.
    public static float DorsalWeight = 1.0f;         // image error of the plate centre. Recording (611 images with the plate seen): visible hand width 0.11 -> 0.29 palm lengths (matches the image), 9-point error 0.070 -> 0.074
    public static float DorsalHiddenWeight = 0.3f;   // plate not seen although it faces the camera
    public static float DorsalHiddenFacing = 0.35f;  // cos of the angle between the plate normal and the direction to the camera; above this it must be visible
    public static float DorsalOffset = 0.012f;       // the plate + glove fabric stick out from the bone (m)
    // Where the model's WRIST point (point 0) lies relative to the virtual hand's wrist joint (wrist frame, m).
    public static Vector3 WristPointLocal = Vector3.zero;
    // How far the model's INDEX BASE point (point 5) is from the virtual index base joint (wrist frame, m).
    // The glove model places point 5 near point 9 (between the base joints), about 2 cm towards the little finger (+X of the
    // XRHand wrist). Two recordings with a 2 cm offset: median index spread -30 -> -4 degrees, thumb hyperextension 23% -> 1% of
    // the time, image error 0.063/0.068 -> 0.061/0.065.
    public static Vector3 IndexMcpPointLocal = new Vector3(0.02f, 0f, 0f);
    private const float HyperextensionWeight = 0.006f;

    // HAND POSE LEARNED FROM THE BARE HAND (HandPoseRecorder: Quest-tracked left hand, two sessions, 13959 frames, 1704 frames of
    // tight pinch; converted to glove-hand joint angles, about 0.6-1.3 degrees off per segment). Two pinch styles: one with the base
    // joint bent most (47/37/18), one (holding the virtual ball) with the middle joint bent most (19/47/33); the covariance is wide
    // enough for both. It replaces earlier hand-set constraints (distal = 2/3 of middle, middle >= base, pinch pose 35/55/35):
    // in a real pinch the base joint is ~47/37/18, 54% of the time bent 10 degrees more than the middle joint, and distal ~0.55 x middle.
    // Model: mean angles interpolated by pinch level (open -> pinched) + an 8-angle covariance (thumb, index: spread / base /
    // middle / distal). Penalty = Mahalanobis distance -> joints move together like a real hand.
    // Re-learned after FingerChainFitter.StraightenFingers (angle 0 = STRAIGHT finger): index distal joint with the hand open
    // 9.4 -> 1.0 degrees, pinch 32.9 -> 24.6 degrees (before, relative to the rig's last segment bent back ~8 degrees).
    // HIDDEN BEND of the index finger: 3D flexion the image does NOT show (a finger bending towards / away from the camera still
    // looks straight). One camera cannot tell, so the solver could add hidden bend and still match the image; seen from the eyes
    // (a different angle than the camera) the finger looks crooked. Penalty = (3D bend - bend in the image) at the middle and distal
    // joints, per degree. With StraightOnImage, on a recording moving near / far / pinching: hidden distal bend p90 10-13 -> 4-5 degrees,
    // distal flexion p90 28-32 -> 21 degrees, image error unchanged, fingertip gap in a pinch 1.9 -> 2.0 cm. Synthetic test: median wrist
    // rotation error 2-6 -> 4-5 degrees (without the gate: 6). 0.032 was only slightly better and jumpier on the far-reach recording.
    public static float HiddenBendWeight = 0.016f;
    // Only penalised when the IMAGE shows a straight finger: middle + distal segments (points 6-7-8) barely bent and NOT
    // foreshortened (a finger bending towards the camera looks shorter). A uniform penalty breaks fingers that really bend in depth:
    // on the synthetic test (pinch-style bend, known answer) the wrist rotation error went 2 -> 6 degrees. Recordings: seen from the side
    // the image bend is p90 7-9 degrees with length >= 0.32 palm lengths; pointing along the view axis the length is 0.11-0.22.
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
    private bool _hasDorsalModel;
    private Vector3 _dorsalL, _dorsalNL;                       // hexagon plate centre + plate normal, in the wrist frame
    private Vector2 _dorsalTarget;
    private int _dorsalState = -1;                             // -1 unknown, 0 not seen, 1 seen

    /// <summary>Wrist -> index base of the virtual hand (m).</summary>
    public float PalmLength { get; private set; }
    /// <summary>Current hand size relative to creation time (HandVisual changes the hand size at run time).</summary>
    public float Scale { get; private set; } = 1f;
    private float _palmLength0;
    private Vector3 _thumbRootL0, _indexRootL0, _dorsalL0;

    // Data of the image being solved
    private Vector3 _camPos;
    private Quaternion _camRot, _camInv;
    private readonly Vector2[] _target = new Vector2[Points]; // normalised coordinates x/z, y/z in the camera frame
    private readonly float[] _weight = new float[Points];
    private float _imgScale;
    private float _depthPrior;
    private bool _useDepthPrior;
    private Quaternion _rotPrior;
    private bool _useRotPrior;
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
    /// <summary>Depth of the index base (camera view axis, m) in the result.</summary>
    public float Depth { get; private set; }
    /// <summary>Hexagon plate used in the latest solve: -1 unknown, 0 not seen, 1 seen.</summary>
    public int DorsalUsed => _dorsalState;
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
    private static readonly Vector2[] TiltSeeds = { Vector2.zero, new Vector2(35f, 0f), new Vector2(-35f, 0f), new Vector2(0f, 70f), new Vector2(0f, -70f) };

    private readonly float[] _x = new float[NP], _trial = new float[NP], _a8 = new float[8], _bestA = new float[8];
    private readonly float[] _r = new float[NR], _rTry = new float[NR];
    private readonly float[,] _jac = new float[NR, NP];
    private readonly float[,] _normal = new float[NP, NP + 1], _sys = new float[NP, NP + 1];
    private readonly float[] _a4 = new float[4];
    private readonly Vector3[] _chain = new Vector3[4];
    private readonly Vector3[] _model = new Vector3[Points];
    private static readonly float[] Step = { 0.05f, 0.05f, 0.05f, 0.2f, 0.2f, 0.2f, 0.3f, 0.3f, 0.3f, 0.3f, 0.3f, 0.3f, 0.3f, 0.3f };
    private static readonly float[] MaxStep = { 5f, 5f, 5f, 20f, 20f, 20f, 25f, 25f, 25f, 25f, 25f, 25f, 25f, 25f };

    /// <summary>Reads the virtual hand's shape IN ITS REST POSE (call at Awake, together with creating the two FingerChainFitters).</summary>
    public HandImageFit(Transform wrist, FingerChainFitter thumb, FingerChainFitter index)
        : this(wrist, new[] { thumb, index }, Vector3.zero, Vector3.zero) { }

    /// <param name="fitters">5 fingers (thumb, index, middle, ring, little); the last three place the hexagon plate (may be null).</param>
    /// <param name="palmTarget">A point on the PALM side (FingerChainFitter.PalmTarget), to know which side is the back of the hand.</param>
    /// <param name="closedAngles">Curl angles of the middle / ring / little fingers (FingerUDPReceiver).</param>
    public HandImageFit(Transform wrist, FingerChainFitter[] fitters, Vector3 palmTarget, Vector3 closedAngles)
    {
        FingerChainFitter thumb = fitters[0], index = fitters[1];
        _thumb = thumb;
        _index = index;
        Quaternion inv = Quaternion.Inverse(wrist.rotation);
        Transform tr = thumb.RootBone, ir = index.RootBone;
        _thumbRootL = inv * (tr.position - wrist.position);
        _indexRootL = inv * (ir.position - wrist.position);
        _thumbParentL = inv * (tr.parent != null ? tr.parent.rotation : wrist.rotation);
        _indexParentL = inv * (ir.parent != null ? ir.parent.rotation : wrist.rotation);
        PalmLength = _palmLength0 = Mathf.Max(_indexRootL.magnitude, 0.03f);
        _thumbRootL0 = _thumbRootL;
        _indexRootL0 = _indexRootL;
        for (int p = 0; p < 4; p++)
        {
            _min[p] = thumb.MinAngle(p); _max[p] = thumb.MaxAngle(p);
            _min[4 + p] = index.MinAngle(p); _max[4 + p] = index.MaxAngle(p);
        }

        // Plate centre: middle of the base segments (MCP -> PIP) of the three curled fingers, raised towards the back of the fingers.
        // The plate faces the FRONT of the fist (base segments bent ~75 degrees), not upwards like the back of the hand.
        if (fitters.Length >= 5 && palmTarget != Vector3.zero)
        {
            Vector3 mid = Vector3.zero, along = Vector3.zero, palmCenter = wrist.position + index.RootBone.position;
            int n = 0, nc = 2;
            float[] a = { 0f, closedAngles.x, closedAngles.y, closedAngles.z };
            var joints = new Vector3[4];
            for (int f = 2; f < 5; f++)
            {
                FingerChainFitter fit = fitters[f];
                if (fit == null) continue;
                Transform root = fit.RootBone;
                palmCenter += root.position; nc++;
                fit.JointsAt(a, root.position, root.parent != null ? root.parent.rotation : wrist.rotation, joints);
                mid += (joints[0] + joints[1]) * 0.5f;
                along += (joints[1] - joints[0]).normalized;
                n++;
            }
            if (n > 0)
            {
                mid /= n;
                palmCenter /= nc;
                Vector3 back = (palmCenter - palmTarget).normalized; // towards the back of the hand
                Vector3 normal = Vector3.ProjectOnPlane(back, along.normalized).normalized;
                _dorsalL = _dorsalL0 = inv * (mid + normal * DorsalOffset - wrist.position);
                _dorsalNL = inv * normal;
                _hasDorsalModel = true;
            }
        }
    }

    /// <summary>Sets the plate centre + plate normal (wrist frame, m) instead of the estimate from the rig.</summary>
    public void SetDorsalModel(Vector3 pointLocal, Vector3 normalLocal)
    {
        _dorsalL0 = pointLocal;
        _dorsalL = pointLocal * Scale;
        _dorsalNL = normalLocal.normalized;
        _hasDorsalModel = normalLocal.sqrMagnitude > 1e-8f;
    }

    /// <summary>Sets the hand size relative to creation time. HandVisual (Quest) changes the hand root scale at run time;
    /// the finger bones (FingerChainFitter) read the CURRENT size, so the palm here must follow the same scale. (It used to be
    /// captured once at start-up (0.9) while the drawn hand became ~1.0 -> the solver built a hand ~10% smaller, placed it closer to
    /// the camera, and seen from the eyes the virtual hand looked bigger and offset from the real hand.)</summary>
    public void SetScale(float k)
    {
        if (k <= 0f || Mathf.Abs(k - Scale) < 1e-4f) return;
        Scale = k;
        _thumbRootL = _thumbRootL0 * k;
        _indexRootL = _indexRootL0 * k;
        _dorsalL = _dorsalL0 * k;
        PalmLength = _palmLength0 * k;
    }

    /// <summary>Plate centre of the current result (world), for drawing / checking.</summary>
    public bool TryGetDorsalPoint(out Vector3 point)
    {
        point = Position + Rotation * _dorsalL;
        return _hasDorsalModel;
    }

    public void Reset()
    {
        HasSolution = false;
    }

    /// <summary>Solves one image.</summary>
    /// <param name="camPos">Camera position AT CAPTURE TIME.</param>
    /// <param name="camRot">Camera rotation at capture time.</param>
    /// <param name="rayDirs">Ray directions (world) from the camera through the 9 image points.</param>
    /// <param name="weights">Trust 0..1 of each point (0 = ignore). Points 0 and 5 are required.</param>
    /// <param name="depthPrior">Hand depth from the Quest (camera view axis, m); &lt;= 0 = none.</param>
    /// <param name="rotPrior">Wrist rotation from the Quest (smoothed); null = none.</param>
    /// <param name="warm">The previous answer is recent (a few hundred ms) -> start from it and stay close to it.</param>
    /// <param name="dorsalState">Hexagon plate on the back of the hand: -1 unknown, 0 Python searched but did not see it, 1 seen.</param>
    /// <param name="dorsalRayDir">Ray through the plate centre (when dorsalState = 1).</param>
    public bool Solve(Vector3 camPos, Quaternion camRot, Vector3[] rayDirs, float[] weights,
                      float depthPrior, Quaternion? rotPrior, bool warm, int dorsalState = -1, Vector3 dorsalRayDir = default)
    {
        _camPos = camPos;
        _camRot = camRot;
        _camInv = Quaternion.Inverse(camRot);
        _dorsalState = _hasDorsalModel ? dorsalState : -1;
        if (_dorsalState == 1)
        {
            Vector3 dd = _camInv * dorsalRayDir;
            if (dd.z > 1e-3f) _dorsalTarget = new Vector2(dd.x / dd.z, dd.y / dd.z);
            else _dorsalState = -1;
        }
        for (int i = 0; i < Points; i++)
        {
            Vector3 d = _camInv * rayDirs[i];
            bool ok = d.z > 1e-3f && weights[i] > 0f;
            _target[i] = ok ? new Vector2(d.x / d.z, d.y / d.z) : Vector2.zero;
            _weight[i] = ok ? weights[i] : 0f;
        }
        if (_weight[0] <= 0f || _weight[5] <= 0f) return false;

        float palmOnImage = Vector2.Distance(_target[0], _target[5]);
        float gap = Vector2.Distance(_target[4], _target[8]) / Mathf.Max(palmOnImage, 1e-4f);
        Pinch = _weight[4] > 0f && _weight[8] > 0f
            ? Mathf.Clamp01((PinchFarOnImage - gap) / Mathf.Max(PinchFarOnImage - PinchNearOnImage, 1e-3f)) : 0f;
        StraightOnImage = StraightGate(palmOnImage);

        warm &= HasSolution;
        _useDepthPrior = depthPrior > 0.05f;
        _depthPrior = depthPrior;
        _useRotPrior = rotPrior.HasValue;
        _rotPrior = rotPrior ?? Quaternion.identity;
        _useTemporal = warm;
        if (warm)
        {
            _prevP = Position;
            _prevR = Rotation;
            System.Array.Copy(Angles, _prevA, 8);
        }
        float depth = _useDepthPrior ? depthPrior : HasSolution ? Depth : 0.4f;
        _imgScale = depth / PalmLength;

        Quaternion rotGuess = _useRotPrior ? _rotPrior : HasSolution ? Rotation : FacingCamera();
        float bestCost = float.PositiveInfinity;
        Vector3 bestP = Vector3.zero;
        Quaternion bestR = Quaternion.identity;

        if (warm)
        {
            bestCost = RefineFrom(Position, Rotation, Angles, out bestP, out bestR, _bestA);
            // Each image also tries one other interpretation (cycling finger seed x tilt seed): a 2D image often has two
            // similar-looking poses, and sometimes the other one is CLEARLY better. The comparison does NOT include the
            // "differs from the previous image" penalty: with it the new interpretation (very different from the current answer)
            // could never win and the virtual hand got stuck in a wrong pose (zero switches in every recording).
            // Fair comparison: both interpretations are solved WITHOUT the temporal penalty (the current one continues from its own
            // place) before comparing. The current answer is pulled towards the previous image, so its image error is always higher;
            // comparing against it directly made the new one almost always win and the virtual hand flipped back and forth.
            int combos = FingerSeeds.GetLength(0) * TiltSeeds.Length;
            int c = _nextSeed;
            _nextSeed = (_nextSeed + 1) % combos;
            _useTemporal = false;
            float warmStatic = RefineFrom(bestP, bestR, _bestA, out _, out _, _a8);
            float cost = TrySeed(rotGuess, depth, c / TiltSeeds.Length, c % TiltSeeds.Length, out Vector3 p, out Quaternion r);
            _useTemporal = true;
            if (cost < warmStatic * SwitchRatio)
            {
                bestCost = cost; bestP = p; bestR = r;
                System.Array.Copy(_a8, _bestA, 8);
                Switches++;
            }
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

        // Image fit error alone (without the constraints), for diagnostics
        _baseP = Position; _baseR = Rotation;
        for (int k = 0; k < 6; k++) _x[k] = 0f;
        System.Array.Copy(Angles, 0, _x, 6, 8);
        Evaluate(_x, _r);
        float sum = 0f, wsum = 0f;
        for (int i = 0; i < Points; i++)
        {
            if (_weight[i] <= 0f) continue;
            sum += (_r[2 * i] * _r[2 * i] + _r[2 * i + 1] * _r[2 * i + 1]) / (_weight[i] * _weight[i]);
            wsum += 1f;
        }
        ImageError = Mathf.Sqrt(sum / Mathf.Max(wsum, 1f));
        Depth = (_camInv * (_model[5] - _camPos)).z;
        return true;
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
    public void GetModelPoints(Vector3[] outPoints)
    {
        _baseP = Position; _baseR = Rotation;
        for (int k = 0; k < 6; k++) _x[k] = 0f;
        System.Array.Copy(Angles, 0, _x, 6, 8);
        ModelPoints(_x, out _, out _);
        System.Array.Copy(_model, outPoints, Points);
    }

    /// <summary>The 9 joint positions for any wrist (p, r) and finger angles a[8], for testing.</summary>
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
        _model[0] = p + r * (WristPointLocal * Scale);
        for (int k = 0; k < 4; k++) _a4[k] = x[6 + k];
        _thumb.JointsAt(_a4, p + r * _thumbRootL, r * _thumbParentL, _chain);
        for (int k = 0; k < 4; k++) _model[1 + k] = _chain[k];
        for (int k = 0; k < 4; k++) _a4[k] = x[10 + k];
        _index.JointsAt(_a4, p + r * _indexRootL, r * _indexParentL, _chain);
        for (int k = 0; k < 4; k++) _model[5 + k] = _chain[k];
        _indexMcpJoint = _chain[0];
        _model[5] += r * (IndexMcpPointLocal * Scale);
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

        float zi = (_camInv * (_model[5] - _camPos)).z;
        r[k++] = _useDepthPrior ? DepthPriorWeight * (zi - _depthPrior) / PalmLength : 0f;

        Vector3 v = _useRotPrior ? RotVec(rot * Quaternion.Inverse(_rotPrior)) * RotPriorWeight : Vector3.zero;
        r[k++] = v.x; r[k++] = v.y; r[k++] = v.z;

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
        r[k++] = hw > 0f ? hw * HiddenBend(_indexMcpJoint, _model[6], _model[7]) : 0f;
        r[k++] = hw > 0f ? hw * HiddenBend(_model[6], _model[7], _model[8]) : 0f;

        // Hexagon plate on the back of the hand: seen -> the virtual plate centre matches the plate in the image (and the plate
        // does not face fully away); not seen -> the plate must not face the camera
        r[k] = r[k + 1] = r[k + 2] = 0f;
        if (_dorsalState >= 0)
        {
            Vector3 dp = p + rot * _dorsalL;
            float facing = Vector3.Dot(rot * _dorsalNL, (_camPos - dp).normalized);
            if (_dorsalState == 1)
            {
                Vector3 c = _camInv * (dp - _camPos);
                float z = Mathf.Max(c.z, 0.02f);
                Vector2 e = (new Vector2(c.x / z, c.y / z) - _dorsalTarget) * (_imgScale * DorsalWeight);
                r[k] = e.x; r[k + 1] = e.y;
                r[k + 2] = DorsalHiddenWeight * Mathf.Max(0f, -facing);
            }
            else r[k + 2] = DorsalHiddenWeight * Mathf.Max(0f, facing - DorsalHiddenFacing);
        }
        k += 3;

        // Pinch: the image shows the two fingertips together -> the virtual fingertips must touch (a 2D image does not tell
        // each finger's depth; this condition mostly fixes the depth)
        Vector3 gap = _model[8] - _model[4];
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
