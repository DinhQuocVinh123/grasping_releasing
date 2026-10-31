using UnityEngine;

/// <summary>
/// Builds the virtual glove hand FROM THE CAMERA IMAGE (see HandImageFit): for each image result from Python, the
/// 9 points become 9 rays from the camera at capture time -> solve once -> between two images the hand only glides
/// smoothly towards the new result. When Python loses the hand, the last pose is kept for _holdSeconds, then the wrist
/// blends back to the Quest hand.
///
/// FingerUDPReceiver calls Drive() every time HandVisual has written the joints. Disabling this component gives the
/// plain Quest hand (only the middle, ring and little fingers stay curled).
///
/// Per image result: a One Euro filter (strong when the hand is still, light when it moves), then one display smoothing
/// stage per rendered frame plus extrapolation along the filtered velocity.
///
/// A bad fit (e.g. the hand turning over, the model unsure) never stops the hand: the wrist POSITION keeps following (the
/// palm rays fix where the hand is even when its rotation is wrong), only the rotation and the finger angles wait for a good
/// result. (Before 29/10 a bad fit held the whole hand still: turning the hand palm up, the real hand moved 83 cm while the
/// virtual one moved 23 cm.)
/// </summary>
public sealed class ImageHandSolver : MonoBehaviour
{
    [Tooltip("Time constant (s) of the display smoothing, from the filtered result to the rendered frame, so the hand moves evenly " +
             "between images (~15-18 images/s).")]
    [SerializeField] private float _displaySeconds = 0.025f;
    [Header("One Euro filter (per solve): hand still -> strong smoothing, hand moving -> follows quickly")]
    [Tooltip("Cutoff frequency with the hand still (Hz). Lower = smoother but more lag.")]
    [SerializeField] private float _minCutoff = 0.7f;
    [Tooltip("Extra cutoff per unit of wrist speed (Hz per m/s).")]
    [SerializeField] private float _betaPosition = 200f;
    [Tooltip("Extra cutoff per unit of wrist rotation speed (Hz per degree/s).")]
    [SerializeField] private float _betaRotation = 0.1f;
    [Tooltip("Extra cutoff per unit of finger joint speed (Hz per degree/s), per joint.")]
    [SerializeField] private float _betaAngle = 0.05f;
    [Tooltip("Cutoff used when estimating speed (Hz).")]
    [SerializeField] private float _speedCutoff = 1f;
    [Tooltip("Between two images, push the wrist forward along its velocity by this fraction (0 = wait for the next image).")]
    [SerializeField] private float _extrapolate = 1f;
    [Tooltip("Same as above, for the wrist rotation and finger angles.")]
    [SerializeField] private float _extrapolateRotation = 0.5f;
    [Tooltip("Extrapolate at most this long after the last solve (images arrive about every 0.06 s).")]
    [SerializeField] private float _maxExtrapolateSeconds = 0.06f;

    [Header("Trust of each result")]
    [Tooltip("Image error (palm lengths) below this = fully trusted; above _errBad = rotation and fingers wait for a better result.")]
    [SerializeField] private float _errGood = 0.07f;
    [SerializeField] private float _errBad = 0.20f;
    [Tooltip("Wrist rotation jumping more than this (degrees) from the filtered result, NOT confirmed by the previous result -> the rotation waits.")]
    [SerializeField] private float _jumpDegrees = 15f;
    [Tooltip("Two consecutive results within this (degrees, less than 0.2 s apart) = a real motion is confirmed (e.g. a fast wrist turn).")]
    [SerializeField] private float _confirmDegrees = 10f;
    [Tooltip("...and at most this far apart (s). It was 0.2 s: when the PC slowed to 3-5 images/s (recording 29/10 15:59), no turn was ever " +
             "confirmed and the virtual hand stayed 146 degrees off for 3 s while every result agreed.")]
    [SerializeField] private float _confirmSeconds = 0.5f;
    [Tooltip("Mean confidence of the model's points, from low to high: below = not trusted, above = fully trusted.")]
    [SerializeField] private float _confLow = 0.25f;
    [SerializeField] private float _confHigh = 0.6f;
    [Tooltip("Rotation / finger results trusted below this are dropped; accepted ones are followed at least at _acceptFloor.")]
    [SerializeField] private float _rejectBelow = 0.1f;
    [Range(0.1f, 1f)]
    [SerializeField] private float _acceptFloor = 0.9f; // replay lab 29/10: 0.6 -> 0.9 cut frames > 0.2 palm lengths off 4.2% -> 3.1% (163121), same jitter
    [Tooltip("The wrist POSITION follows at least this fast even when the fit is bad (0 = it waits like the rotation).")]
    [Range(0f, 1f)]
    [SerializeField] private float _positionFloor = 0.5f;
    [Tooltip("The hand counts as SEEN while the 3 palm points (wrist, thumb base, index base) reach this confidence trust; the fit error does " +
             "not matter for it. Not seen for _holdSeconds -> the wrist goes back to the Quest hand, the force gate closes.")]
    [SerializeField] private float _seenTrust = 0.3f;

    [Header("Hand leaving the camera frame")]
    [Tooltip("ON: points in the image edge band or outside the image (the model GUESSES them) get weight 0 in the solve. " +
             "A finger with fewer than _minFingerPoints points in frame KEEPS its last good pose. The whole hand with fewer than _minInFramePoints " +
             "points (or none of the 3 palm points) -> no solve, the hand counts as not seen.")]
    [SerializeField] private bool _rejectEdgePoints = true;
    [Tooltip("Image edge band (fraction of width / height) treated as 'too close to the edge, not trusted'.")]
    [Range(0f, 0.15f)]
    [SerializeField] private float _edgeMargin = 0.03f;
    [Range(3, 9)]
    [SerializeField] private int _minInFramePoints = 5;
    [Range(1, 4)]
    [SerializeField] private int _minFingerPoints = 2;
    [Tooltip("ON: while the thumb or index pose is HELD (out of frame) -> ForceWeight goes to 0 and GloveForceEsp32Output's force gate fades " +
             "the force out (no pushing on a finger that is frozen).")]
    [SerializeField] private bool _cutForceWhenFingerFrozen = true;

    [Tooltip("When the solver switches to a different interpretation (e.g. the hand turned over), glide to the new pose over this time (s).")]
    [SerializeField] private float _switchSeconds = 0.2f;
    [Tooltip("Python lost the hand: keep the last pose this long (s) before the wrist goes back to the Quest hand.")]
    [SerializeField] private float _holdSeconds = 1.0f;
    [Tooltip("Blend time between the image hand and the Quest hand (s).")]
    [SerializeField] private float _blendSeconds = 0.3f;
    [Tooltip("The Quest's hand depth is averaged over this many seconds before being used as the depth hint.")]
    [SerializeField] private float _depthPriorSeconds = 0.4f;
    [Tooltip("If the previous answer is older than this (s), solve from scratch instead of starting from it.")]
    [SerializeField] private float _warmSeconds = 0.5f;
    [Tooltip("Points with a confidence below this are ignored.")]
    [SerializeField] private float _minConfidence = 0.2f;
    [Tooltip("Mean confidence of the 9 points below this = the model is not looking at the glove (bare hand, controller, desk) -> no solve, " +
             "the hand counts as not seen. Recordings 29/10: glove in view 0.70+ (10th percentile), no glove 0.30 (median), 0.42 (90th percentile).")]
    [SerializeField] private float _minMeanConfidence = 0.5f;
    [Tooltip("The wrist rotation has been waiting this long (s) for a trusted result while consecutive results agree with each other -> " +
             "follow them anyway at _staleFollow (a pose the solver cannot fit well, e.g. fingers pointing away, otherwise froze the rotation for seconds).")]
    [SerializeField] private float _maxRotationWait = 0.3f;
    [Range(0f, 1f)]
    [SerializeField] private float _staleFollow = 0.35f;
    [Tooltip("Weight of point 1 (thumb base near the wrist; no tape marker there, so the model places it less reliably).")]
    [SerializeField] private float _thumbBaseWeight = 0.5f;
    [Tooltip("Glove hand size = the size the Quest measures for the BARE LEFT HAND (Hand.Scale); the Quest's size estimate for the gloved " +
             "hand is unreliable. Until the left hand has been seen, keep the size the Quest set. The glove's own proportions: see Glove size.")]
    [SerializeField] private bool _useLeftHandScale = true;

    [Header("Glove size (the gloved hand as the model sees it is bigger than the bare-hand rig)")]
    [Tooltip("Thumb / index length relative to the rig. Measured 29/10 with both passthrough cameras (recording 16:02, 210 frames where the " +
             "model was sure of every point; each frame's points cut at the two-camera depth, 80th percentile): thumb chain 82 mm = rig, index " +
             "chain 89 mm vs 80 mm (a 14:25 recording gave x1.2). With the rig's size the solver pulled the hand ~7 cm closer to make it look big enough.")]
    [SerializeField] private float _thumbLength = 1f;
    [SerializeField] private float _indexLength = 1.12f;
    [Tooltip("The model's wrist point sits on the glove cuff, this far down the forearm from the rig's wrist joint (wrist frame, m, before the hand " +
             "scale): wrist point -> index base measured 140-147 mm vs the rig's 98 mm.")]
    [SerializeField] private float _wristPointDown = 0.0376f;
    [Tooltip("Whole gloved hand vs the bare-hand size (on top of the left hand's size): a hand too small for the image is solved too close " +
             "to the camera. 1 = bare-hand size. Replay lab 30/10 against both passthrough cameras (163121): 1 -> hand 2.5 cm too close, " +
             "3D error 3.2 cm; 1.1 -> 0.9 cm, 2.6 cm (1.15: 0.3 cm, 2.8 cm).")]
    [SerializeField] private float _gloveScale = 1.1f;

    [Header("Natural finger shape (display only)")]
    [Tooltip("ON: before the angles are written to the bones, reshape thumb and index so they look like a real finger (the solve and its " +
             "filters are untouched): the glove hides the middle joints, so the model's points there carry a bias (index middle joint " +
             "bent ~30 degrees with a stiff tip, never fully straight). OFF since 30/10: the reshaped index stuck out past the real finger in " +
             "the headset (replay lab: index tip 1.3 cm from the solve, 0.2 cm without it).")]
    [SerializeField] private bool _naturalFingerShape = false;
    [Tooltip("Index base (x) and middle (y) joint flexion (degrees) read when the real finger is straight; subtracted and the range rescaled.")]
    [SerializeField] private Vector2 _indexStraightBias = new Vector2(10f, 15f);
    [Tooltip("The index distal joint follows the middle joint within this ratio (real fingers: distal ~2/3 of middle).")]
    [SerializeField] private Vector2 _indexDistalRatio = new Vector2(0.5f, 0.8f);
    [Tooltip("Maximum sideways spread of the index finger (degrees).")]
    [SerializeField] private float _indexSpreadLimit = 10f;

    // The 3 palm points: wrist (0), thumb base (1), index base (5) -- they decide the wrist position / rotation
    private static readonly int[] PalmPoints = { 0, 1, 5 };
    private const int N = HandImageFit.Points;

    private FingerUDPReceiver _receiver;
    private GloveLiveStreamer _streamer;
    private Transform _wrist;
    private FingerChainFitter _thumb, _index;
    private HandImageFit _fit;
    private Transform[] _between;      // bones between the wrist and the finger bases (e.g. the index metacarpal)
    private Quaternion[] _betweenRest;
    private Transform[] _restJoints = new Transform[0]; // every XRHand_* joint below the wrist, and its rest local position
    private Vector3[] _restJointPos = new Vector3[0];

    private readonly Vector2[] _uv = new Vector2[N];
    private readonly float[] _conf = new float[N];
    private readonly float[] _confIn = new float[N]; // = _conf, with points near / outside the image edge set to 0
    private readonly Vector3[] _dirs = new Vector3[N];
    private readonly float[] _w = new float[N];
    // Render check: the index angles last written to the bones, and how far the index bones really are from them just before
    // rendering (Application.onBeforeRender) -- something writing the bones after us would show here
    private readonly float[] _writtenA = new float[8];
    private bool _bonesWritten;
    private readonly Vector3[] _gapJoints = new Vector3[HandImageFit.Points];
    /// <summary>Largest distance (mm) between an index bone just before rendering and where the solver put that joint (latest frame).</summary>
    public float RenderGapMm { get; private set; }
    /// <summary>...and what changed since WriteBones: largest index bone rotation (degrees), largest rotation of the bones between the
    /// wrist and the finger bases away from their rest pose (degrees), hand size (ratio - 1).</summary>
    public float RenderGapIndexDeg { get; private set; }
    public float RenderGapBetweenDeg { get; private set; }
    public float RenderGapScale { get; private set; }
    private readonly Quaternion[] _writtenIndexRot = new Quaternion[3];
    private float _writtenScale = 1f;
    // Knuckles (model points 9, 13, 17) for the next solve (SetKnuckles)
    private bool _hasKnuckles;
    private readonly Vector2[] _kuv = new Vector2[HandImageFit.Knuckles];
    private readonly float[] _kconf = new float[HandImageFit.Knuckles], _kw = new float[HandImageFit.Knuckles];
    private readonly Vector3[] _kdirs = new Vector3[HandImageFit.Knuckles];
    // Second camera for the next solve (SetSecondView)
    private bool _hasView2;
    private Pose _cam2;
    private readonly Vector2[] _uv2 = new Vector2[N];
    private readonly float[] _conf2 = new float[N], _w2 = new float[N];
    private readonly Vector3[] _dirs2 = new Vector3[N];
    // Copy of the latest solve for GloveFitDebugView
    private bool _dbgValid;
    private Pose _dbgCam;
    private readonly Vector3[] _dbgDirs = new Vector3[N];
    private readonly float[] _dbgW = new float[N];
    private readonly Vector3[] _dbgJoints = new Vector3[N];

    private int _lastFid;
    private float _lastSeenTime = -999f;     // last image where the palm points were seen
    private float _lastGoodTime = -999f;     // last result good enough to start the next solve from
    private float _depthPrior;
    private bool _hasDepthPrior;
    private float _lastDepthTime;

    private Vector3 _dispP;
    private Quaternion _dispR = Quaternion.identity;
    private readonly float[] _dispA = new float[8];
    private bool _hasDisp;
    private int _dispFrame = -1;
    private float _weight;
    private int _seenSwitches;
    private float _slowUntil;
    private bool _thumbFrozen, _indexFrozen; // finger out of frame -> keep its previous pose
    private float _forceGate = 1f;

    // One Euro filtered result (updated after every solve)
    private Vector3 _fP;
    private Quaternion _fR = Quaternion.identity;
    private readonly float[] _fA = new float[8];
    private float _fSpeedP, _fSpeedR;
    private readonly float[] _fSpeedA = new float[8];
    private Vector3 _fVel, _fAngVel;           // m/s, axis*degrees/s
    private readonly float[] _fVelA = new float[8];
    private float _fTime = -999f;              // latest filter step (reference for display extrapolation)
    private float _fTimeP, _fTimeR, _fTimeT, _fTimeI; // latest ACCEPTED position / rotation / thumb / index result
    private float _rotTrustedTime;             // latest rotation result accepted on its own trust (not by _staleFollow)
    private Quaternion _rawPrevR = Quaternion.identity; // previous raw result (to confirm jumps)
    private float _rawPrevTime = -999f;
    private Vector3 _rawAngVel;                         // angular velocity between the last two raw results (degrees/s, world axis)
    private bool _hasFilter;

    private float _initWristScale = 1f;
    private Oculus.Interaction.HandVisual _gloveVisual, _leftVisual;
    private float _leftScale;
    private Vector3 _scaleBase = Vector3.one, _scaleSet = new Vector3(-1f, -1f, -1f); // hand root size before / after _gloveScale
    private readonly float[] _shownA = new float[8];

    // --- Diagnostics (written to glove_diag by FingerUDPReceiver) ---
    public float Weight => _weight;
    public int SolvedFrameId { get; private set; }
    public float ImageError => _fit != null ? _fit.ImageError : 0f;
    public float Cost => _fit != null ? _fit.LastCost : 0f;
    public float Depth => _fit != null ? _fit.Depth : 0f;
    /// <summary>The Quest's hand depth (camera view axis, m), smoothed; 0 = none yet.</summary>
    public float DepthPrior => _hasDepthPrior ? _depthPrior : 0f;
    public float Pinch => _fit != null ? _fit.Pinch : 0f;
    public int Switches => _fit != null ? _fit.Switches : 0;
    public int Reseeds => _fit != null ? _fit.Reseeds : 0;
    public float SolveMs { get; private set; }
    /// <summary>Trust of the latest solve (0..1) for the wrist rotation.</summary>
    public float LastTrust { get; private set; } = 1f;
    public bool Ready => _fit != null;
    /// <summary>Kept for the glove_diag column (the hexagon plate is no longer used): always -1.</summary>
    public int DorsalUsed => -1;
    /// <summary>Current glove hand size (1 = the reference skeleton, palm about 9.9 cm).</summary>
    public float HandScale => _wrist != null ? _wrist.lossyScale.x : 0f;
    /// <summary>Age of the solved image (ms): from capture until Python's result reached Unity.</summary>
    public float FrameAgeMs { get; private set; }
    /// <summary>Like FrameAgeMs but from the camera EXPOSURE (earlier still, includes camera processing). 0 = unknown.</summary>
    public float ExposureAgeMs { get; private set; }
    /// <summary>RAW wrist solved from the latest image and that image's exposure time (Time.realtimeSinceStartup, 0 = unknown):
    /// "the real hand at capture time", to measure virtual vs real hand latency.</summary>
    public Vector3 RawWrist { get; private set; }
    public Quaternion RawRotation => _fit != null ? _fit.Rotation : Quaternion.identity;
    /// <summary>Raw solved angle k of the latest image (0-3 thumb, 4-7 index, degrees), before filtering.</summary>
    public float RawAngle(int k) => _fit != null ? _fit.Angles[k] : 0f;
    /// <summary>Hand size the solver uses (HandImageFit.Scale).</summary>
    public float RawScale => _fit != null ? _fit.Scale : 1f;
    /// <summary>The 9 virtual joints of the latest raw solve (no point offsets); false = nothing solved yet.</summary>
    public bool TryGetRawJoints(Vector3[] joints)
    {
        if (_fit == null || !_fit.HasSolution) return false;
        _fit.GetJointPoints(joints);
        return true;
    }
    public float RawExposureTime { get; private set; }
    /// <summary>Image frame of the latest raw solve.</summary>
    public int RawFrameId { get; private set; }
    /// <summary>How far the shown wrist is from the latest solve (mm / degrees): the lag added by the filters.</summary>
    public float LagMm { get; private set; }
    public float LagDeg { get; private set; }
    /// <summary>Trust of the latest solve: wrist rotation, thumb, index (0..1, before the accept / reject threshold).</summary>
    public float WristTrust { get; private set; }
    public float ThumbTrust { get; private set; }
    public float IndexTrust { get; private set; }
    /// <summary>Number of points (of 9) near / outside the image edge in the latest image.</summary>
    public int EdgePoints { get; private set; }
    /// <summary>0 = hand fully in frame, 1 = partly out (points dropped / a finger frozen), 2 = hand not seen (pose held / back to Quest).</summary>
    public int State { get; private set; } = 2;
    /// <summary>Force gate for GloveForceEsp32Output: = Weight, goes to 0 while the thumb or index pose is held (_cutForceWhenFingerFrozen).</summary>
    public float ForceWeight => _weight * _forceGate;
    public bool ThumbFrozen => _thumbFrozen;
    public bool IndexFrozen => _indexFrozen;

    /// <summary>Latest solve, for GloveFitDebugView: camera pose at capture, ray direction + weight of each of the
    /// 9 points (weight 0 = not used) and the raw solved joints (before filtering). Arrays must hold HandImageFit.Points.
    /// false = nothing solved yet.</summary>
    public bool TryGetDebugView(out Pose camera, Vector3[] dirs, float[] weights, Vector3[] joints)
    {
        camera = _dbgCam;
        if (!_dbgValid) return false;
        System.Array.Copy(_dbgDirs, dirs, N);
        System.Array.Copy(_dbgW, weights, N);
        System.Array.Copy(_dbgJoints, joints, N);
        return true;
    }

    private void OnEnable() => Application.onBeforeRender += CheckRenderGap;
    private void OnDisable() => Application.onBeforeRender -= CheckRenderGap;

    /// <summary>Measures RenderGapMm now (also called by GloveReplayLab).</summary>
    public void CheckRenderGap()
    {
        if (_fit == null || !_bonesWritten || _wrist == null || _index == null) { RenderGapMm = 0f; return; }
        _fit.ComputeJoints(_wrist.position, _wrist.rotation, _writtenA, _gapJoints);
        Transform b = _index.RootBone;
        float gap = 0f;
        for (int j = 5; j < 9 && b != null; j++)
        {
            gap = Mathf.Max(gap, Vector3.Distance(b.position, _gapJoints[j]));
            b = b.childCount > 0 ? b.GetChild(0) : null;
        }
        RenderGapMm = gap * 1000f;
        float di = 0f, db = 0f;
        b = _index.RootBone;
        for (int j = 0; j < 3 && b != null; j++)
        {
            di = Mathf.Max(di, Quaternion.Angle(b.localRotation, _writtenIndexRot[j]));
            b = b.childCount > 0 ? b.GetChild(0) : null;
        }
        if (_between != null)
            for (int i = 0; i < _between.Length; i++) db = Mathf.Max(db, Quaternion.Angle(_between[i].localRotation, _betweenRest[i]));
        RenderGapIndexDeg = di;
        RenderGapBetweenDeg = db;
        RenderGapScale = _wrist.lossyScale.x / Mathf.Max(_writtenScale, 1e-6f) - 1f;
    }

    /// <summary>Called by FingerUDPReceiver right after creating the five finger kinematics (rig in its rest pose).</summary>
    public void Initialize(FingerUDPReceiver receiver, Transform wrist, FingerChainFitter thumb, FingerChainFitter index,
                           Transform[] knuckles = null)
    {
        _receiver = receiver;
        _wrist = wrist;
        _thumb = thumb;
        _index = index;
        if (wrist == null || thumb == null || index == null) return;
        _fit = new HandImageFit(wrist, thumb, index);
        _fit.SetKnuckleJoints(wrist, knuckles);
        // TEMPORARILY OFF (30/10): FingerChainFitter.SetLengthScale does not exist yet (unfinished change) -> build failed.
        // thumb.SetLengthScale(_thumbLength);
        // index.SetLengthScale(_indexLength);
        HandImageFit.WristPointLocal = new Vector3(0f, 0f, -_wristPointDown);
        _initWristScale = Mathf.Max(wrist.lossyScale.x, 1e-4f);
        _gloveVisual = receiver.GetComponent<Oculus.Interaction.HandVisual>();

        var list = new System.Collections.Generic.List<Transform>();
        foreach (Transform root in new[] { thumb.RootBone, index.RootBone })
            for (Transform t = root.parent; t != null && t != wrist; t = t.parent)
                if (!list.Contains(t)) list.Add(t);
        _between = list.ToArray();
        _betweenRest = new Quaternion[_between.Length];
        for (int i = 0; i < _between.Length; i++) _betweenRest[i] = _between[i].localRotation;

        // Rest POSITION of every hand joint below the wrist (rig in its rest pose here, before HandVisual's first update)
        var joints = new System.Collections.Generic.List<Transform>();
        foreach (Transform t in wrist.GetComponentsInChildren<Transform>(true))
            if (t != wrist && t.name.StartsWith("XRHand_")) joints.Add(t);
        _restJoints = joints.ToArray();
        _restJointPos = new Vector3[_restJoints.Length];
        for (int i = 0; i < _restJoints.Length; i++) _restJointPos[i] = _restJoints[i].localPosition;
    }

    /// <summary>Builds the virtual hand for this frame (questPos/Rot = the wrist HandVisual has just written).
    /// holdLonger = holding an object while data is lost -> keep the last pose longer (so the object is not dropped).</summary>
    public void Drive(Vector3 questPos, Quaternion questRot, bool dataValid, bool holdLonger = false)
    {
        if (_fit == null) return;
        ApplyHandScale();
        if (dataValid) TrySolveNewFrame();
        if (Time.frameCount != _dispFrame)
        {
            _dispFrame = Time.frameCount;
            StepDisplay(Time.time, Time.deltaTime, holdLonger);
        }
        WriteBones(questPos, questRot);
    }

    /// <summary>One rendered frame: blend weight, hand state and the display smoothing towards the filtered result.
    /// Also called by GloveReplayLab (Editor) to replay recordings with the headset's code.</summary>
    public void StepDisplay(float now, float dt, bool holdLonger = false)
    {
        bool fresh = _hasFilter && (now - _lastSeenTime <= _holdSeconds || holdLonger);
        _weight = Mathf.MoveTowards(_weight, fresh ? 1f : 0f, dt / Mathf.Max(_blendSeconds, 1e-3f));
        bool frozen = _thumbFrozen || _indexFrozen;
        _forceGate = _cutForceWhenFingerFrozen && frozen ? 0f : 1f; // GloveForceEsp32Output smooths the gate itself
        State = !fresh ? 2 : (EdgePoints > 0 || frozen ? 1 : 0);
        if (_hasFilter)
        {
            if (!_hasDisp || _weight <= 0.001f)
            {
                _dispP = _fP;
                _dispR = _fR;
                System.Array.Copy(_fA, _dispA, 8);
                _hasDisp = true;
            }
            else
            {
                // The solver just switched interpretation (a clearly different hand pose): glide a bit slower
                if (_fit.Switches != _seenSwitches)
                {
                    _seenSwitches = _fit.Switches;
                    _slowUntil = now + _switchSeconds;
                }
                float tau = now < _slowUntil ? _switchSeconds * 0.5f : _displaySeconds;
                float a = 1f - Mathf.Exp(-dt / Mathf.Max(tau, 1e-3f));
                // Target = the filtered result, pushed on along the velocity since the last solve
                float ahead = Mathf.Clamp(now - _fTime, 0f, _maxExtrapolateSeconds);
                float aheadR = _extrapolateRotation * ahead;
                Vector3 tgtP = _fP + _extrapolate * ahead * _fVel;
                Quaternion tgtR = Quaternion.AngleAxis(_fAngVel.magnitude * aheadR, _fAngVel.normalized) * _fR;
                _dispP = Vector3.Lerp(_dispP, tgtP, a);
                _dispR = Quaternion.Slerp(_dispR, tgtR, a);
                for (int k = 0; k < 8; k++) _dispA[k] = Mathf.Lerp(_dispA[k], _fA[k] + aheadR * _fVelA[k], a);
            }
            LagMm = Vector3.Distance(_dispP, _fit.Position) * 1000f;
            LagDeg = Quaternion.Angle(_dispR, _fit.Rotation);
        }
    }

    /// <summary>The wrist pose being shown (before the blend with the Quest hand); false = nothing shown yet.</summary>
    public bool TryGetShownWrist(out Vector3 position, out Quaternion rotation)
    {
        position = _dispP;
        rotation = _dispR;
        return _hasDisp;
    }

    /// <summary>Writes the shown pose to the bones (on every call -- HandVisual may have just overwritten them).
    /// Wrist: blend image hand &lt;-&gt; Quest hand (questPos/Rot) by Weight. Fingers: always the image pose (the last good pose when
    /// the hand is lost); the Quest cannot see gloved fingers.</summary>
    public void WriteBones(Vector3 questPos, Quaternion questRot)
    {
        float w = _hasDisp ? _weight : 0f;
        float wf = _hasDisp ? 1f : 0f;
        if (wf > 0f)
        {
            for (int i = 0; i < _between.Length; i++)
                _between[i].localRotation = _betweenRest[i];
            // HandVisual writes every joint's local POSITION from the Quest's own (gloved) hand tracking, which we only override in
            // rotation: on the headset the index bones sat 23-26 mm from where the solver put them (glove_diag 30/10 17:12, rgap_mm;
            // replay lab without HandVisual: 0.0), the finger looked cut short and off to the side. Back to the rig's rest positions.
            for (int i = 0; i < _restJoints.Length; i++)
                _restJoints[i].localPosition = _restJointPos[i];
        }
        if (w > 0f)
            _wrist.SetPositionAndRotation(Vector3.Lerp(questPos, _dispP, w), Quaternion.Slerp(questRot, _dispR, w));
        float[] shown = _naturalFingerShape ? NaturalShape(_dispA) : _dispA;
        System.Array.Copy(shown, _writtenA, 8);
        _bonesWritten = wf > 0f;
        _thumb.SetAngles(shown[0], shown[1], shown[2], shown[3]);
        _index.SetAngles(shown[4], shown[5], shown[6], shown[7]);
        _thumb.Apply(wf);
        _index.Apply(wf);
        // What was just written, to see later what changed before rendering (CheckRenderGap)
        Transform ib = _index.RootBone;
        for (int j = 0; j < 3; j++) { _writtenIndexRot[j] = ib != null ? ib.localRotation : Quaternion.identity; ib = ib != null && ib.childCount > 0 ? ib.GetChild(0) : null; }
        _writtenScale = _wrist.lossyScale.x;
    }

    /// <summary>Display-only reshaping of the 8 finger angles (see _naturalFingerShape): index straight when the real finger is straight,
    /// distal joint coupled to the middle joint, limited sideways spread; thumb joints never bend backwards.</summary>
    private float[] NaturalShape(float[] src)
    {
        System.Array.Copy(src, _shownA, 8);
        // Thumb: [0] spread, [1] base, [2] middle (MCP), [3] distal (IP) -- no backward bending at MCP / IP
        _shownA[2] = Mathf.Max(_shownA[2], 0f);
        _shownA[3] = Mathf.Max(_shownA[3], 0f);
        // Index: [4] spread, [5] base (MCP), [6] middle (PIP), [7] distal (DIP)
        _shownA[4] = Mathf.Clamp(_shownA[4], -_indexSpreadLimit, _indexSpreadLimit);
        _shownA[5] = Unbias(_shownA[5], _indexStraightBias.x, _index.MaxAngle(1));
        _shownA[6] = Unbias(_shownA[6], _indexStraightBias.y, _index.MaxAngle(2));
        _shownA[7] = Mathf.Clamp(_shownA[7], _indexDistalRatio.x * _shownA[6], _indexDistalRatio.y * _shownA[6]);
        return _shownA;
    }

    /// <summary>Removes a constant flexion bias: bias -> 0, max stays max, values below the bias become 0 (straight).</summary>
    private static float Unbias(float angle, float bias, float max)
    {
        if (bias <= 0f || max <= bias) return angle;
        return Mathf.Max(0f, angle - bias) * max / (max - bias);
    }

    /// <summary>Glove hand size from the bare left hand times _gloveScale, then tells the solver the current size (HandVisual resets the
    /// hand root scale every frame; this runs AFTER HandVisual, so it can overwrite it). Also called by GloveReplayLab (Editor).</summary>
    public void ApplyHandScale()
    {
        Transform hand = _gloveVisual != null ? _gloveVisual.Root : null;
        if (hand != null && hand.localScale != _scaleSet) _scaleBase = hand.localScale; // someone else (HandVisual) set a new size
        if (_useLeftHandScale && hand != null)
        {
            if (_leftVisual == null && Time.frameCount % 60 == 0)
            {
                foreach (var hv in FindObjectsByType<Oculus.Interaction.HandVisual>(FindObjectsInactive.Exclude))
                    if (hv != _gloveVisual && hv.Hand != null && hv.Hand.Handedness == Oculus.Interaction.Input.Handedness.Left)
                    {
                        _leftVisual = hv;
                        break;
                    }
            }
            var left = _leftVisual != null ? _leftVisual.Hand : null;
            if (left != null && left.IsTrackedDataValid && left.Scale > 0.6f && left.Scale < 1.4f) _leftScale = left.Scale;
            if (_leftScale > 0f)
            {
                float parent = hand.parent != null ? Mathf.Max(hand.parent.lossyScale.x, 1e-4f) : 1f;
                _scaleBase = Vector3.one * (_leftScale / parent);
            }
        }
        if (hand != null)
        {
            hand.localScale = _scaleBase * _gloveScale;
            _scaleSet = hand.localScale;
        }
        _fit.SetScale(_wrist.lossyScale.x / _initWristScale);
    }

    private bool InFrame(Vector2 uv) =>
        uv.x >= _edgeMargin && uv.x <= 1f - _edgeMargin && uv.y >= _edgeMargin && uv.y <= 1f - _edgeMargin;

    private readonly Vector2[] _kuvIn = new Vector2[HandImageFit.Knuckles];
    private readonly float[] _kconfIn = new float[HandImageFit.Knuckles];
    private readonly Vector3[] _kdirsIn = new Vector3[HandImageFit.Knuckles];

    private void TrySolveNewFrame()
    {
        if (!_receiver.TryGetPixelPoints(out int fid, _uv, _conf, out _, out _, _kuvIn, _kconfIn) || fid == _lastFid) return;
        _lastFid = fid;
        if (_streamer == null) _streamer = FindAnyObjectByType<GloveLiveStreamer>();
        if (_streamer == null || !_streamer.TryGetFramePose(fid, out Pose cam)) return;
        if (_streamer.TryGetFrameAge(fid, out float age)) FrameAgeMs = age * 1000f;
        for (int i = 0; i < N; i++)
        {
            if (!_streamer.TryGetFrameRay(fid, _uv[i], out Ray ray)) return;
            _dirs[i] = ray.direction;
        }
        bool knuckles = false;
        for (int k = 0; k < HandImageFit.Knuckles; k++)
        {
            _kdirsIn[k] = Vector3.forward;
            if (_kconfIn[k] <= 0f) continue;
            if (!_streamer.TryGetFrameRay(fid, _kuvIn[k], out Ray ray)) { _kconfIn[k] = 0f; continue; }
            _kdirsIn[k] = ray.direction;
            knuckles = true;
        }
        if (knuckles) SetKnuckles(_kuvIn, _kconfIn, _kdirsIn);
        bool hasHint = _streamer.TryGetFrameHint(fid, out Vector3 center, out _);
        if (!SolveImage(Time.time, fid, cam, _uv, _conf, _dirs, hasHint, center)) return;
        // "Real hand at capture time" stamp: raw result + exposure time
        if (_streamer.TryGetFrameExposureTime(fid, out float exposure))
        {
            RawExposureTime = exposure;
            ExposureAgeMs = (Time.realtimeSinceStartup - exposure) * 1000f;
        }
        else RawExposureTime = ExposureAgeMs = 0f;
    }

    /// <summary>Solves one image result and runs the filter step. uv = the 9 points (image fractions, top-left origin), conf = their
    /// confidence, dirs = ray directions from the camera at capture (cam), hintCenter = the Quest's hand centre at capture (depth hint).
    /// false = not solved. Also called by GloveReplayLab (Editor) to replay recordings with the headset's code.</summary>
    /// <summary>The same 9 points seen by the second passthrough camera, used by the NEXT SolveImage only: two views measure each
    /// joint's depth instead of guessing it (cam = that camera's pose at capture time, dirs = world rays).</summary>
    public void SetSecondView(Pose cam, Vector2[] uv, float[] conf, Vector3[] dirs)
    {
        _cam2 = cam;
        System.Array.Copy(uv, _uv2, N);
        System.Array.Copy(conf, _conf2, N);
        System.Array.Copy(dirs, _dirs2, N);
        _hasView2 = true;
    }

    /// <summary>The model's middle / ring / little finger base points (9, 13, 17) of the same image, used by the NEXT SolveImage only
    /// (uv = image fractions, conf = confidence, dirs = world rays from the camera at capture).</summary>
    public void SetKnuckles(Vector2[] uv, float[] conf, Vector3[] dirs)
    {
        System.Array.Copy(uv, _kuv, HandImageFit.Knuckles);
        System.Array.Copy(conf, _kconf, HandImageFit.Knuckles);
        System.Array.Copy(dirs, _kdirs, HandImageFit.Knuckles);
        _hasKnuckles = true;
    }

    public bool SolveImage(float now, int fid, Pose cam, Vector2[] uv, float[] conf, Vector3[] dirs, bool hasHint, Vector3 hintCenter)
    {
        if (_fit == null) return false;
        if (uv != _uv) System.Array.Copy(uv, _uv, N);
        if (conf != _conf) System.Array.Copy(conf, _conf, N);
        if (dirs != _dirs) System.Array.Copy(dirs, _dirs, N);

        // Points near / outside the image edge: the model guesses instead of seeing -> weight 0, treated as confidence 0
        int inFrame = 0, palmIn = 0, thumbIn = 0, indexIn = 0, edge = 0;
        for (int i = 0; i < N; i++)
        {
            bool inside = !_rejectEdgePoints || InFrame(_uv[i]);
            if (inside)
            {
                inFrame++;
                if (i == 0 || i == 1 || i == 5) palmIn++;
                if (i >= 1 && i <= 4) thumbIn++;
                else if (i >= 5) indexIn++;
            }
            else edge++;
            _confIn[i] = inside ? _conf[i] : 0f;
            float c = inside && _conf[i] >= _minConfidence ? 1f : 0f;
            _w[i] = i == 1 ? c * _thumbBaseWeight : c;
        }
        EdgePoints = edge;
        float meanConf = 0f;
        for (int i = 0; i < N; i++) meanConf += _conf[i];
        meanConf /= N;
        if (meanConf < _minMeanConfidence || (_rejectEdgePoints && (inFrame < _minInFramePoints || palmIn == 0)))
        {
            // Too few really visible points -> no solve (it would give a twisted hand); does not count as "hand seen"
            WristTrust = ThumbTrust = IndexTrust = 0f;
            return false;
        }
        _thumbFrozen = _rejectEdgePoints && thumbIn < _minFingerPoints;
        _indexFrozen = _rejectEdgePoints && indexIn < _minFingerPoints;

        // Hand depth from the Quest AT CAPTURE TIME (hand centre = middle finger base), averaged over time
        if (hasHint)
        {
            float z = Vector3.Dot(hintCenter - cam.position, cam.rotation * Vector3.forward);
            if (z > 0.08f && z < 1.2f)
            {
                float since = now - _lastDepthTime;
                _depthPrior = !_hasDepthPrior || since > 2f ? z
                    : Mathf.Lerp(_depthPrior, z, 1f - Mathf.Exp(-since / Mathf.Max(_depthPriorSeconds, 1e-3f)));
                _hasDepthPrior = true;
                _lastDepthTime = now;
            }
        }

        // Start from the previous answer only while it is still good (a bad starting point easily gets stuck in a wrong pose;
        // HandImageFit also tries every interpretation again when the warm answer fits badly)
        bool warm = now - _lastGoodTime <= _warmSeconds;
        if (_hasKnuckles)
        {
            for (int k = 0; k < HandImageFit.Knuckles; k++)
                _kw[k] = (!_rejectEdgePoints || InFrame(_kuv[k])) && _kconf[k] >= _minConfidence ? 1f : 0f;
            _fit.SetKnuckleView(_kdirs, _kw);
            _hasKnuckles = false;
        }
        if (_hasView2)
        {
            for (int i = 0; i < N; i++)
                _w2[i] = (!_rejectEdgePoints || InFrame(_uv2[i])) && _conf2[i] >= _minConfidence ? (i == 1 ? _thumbBaseWeight : 1f) : 0f;
            _fit.SetSecondView(_cam2.position, _cam2.rotation, _dirs2, _w2);
            _hasView2 = false;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool ok = _fit.Solve(cam.position, cam.rotation, _dirs, _w, _hasDepthPrior ? _depthPrior : 0f, warm);
        SolveMs = (float)sw.Elapsed.TotalMilliseconds;
        if (!ok) return false;
        _dbgValid = true;
        _dbgCam = cam;
        System.Array.Copy(_dirs, _dbgDirs, N);
        System.Array.Copy(_w, _dbgW, N);
        _fit.GetModelPoints(_dbgJoints);
        RawWrist = _fit.Position;
        RawFrameId = fid;

        bool seen = PalmConfTrust() >= _seenTrust;
        if (!_hasFilter || now - _lastSeenTime > _holdSeconds)
        {
            // Restart (first time / after losing the hand): needs the palm seen, then starts from this result
            LastTrust = WristTrust = ErrTrust() * Mathf.Max(PalmConfTrust(), 0.3f);
            if (!seen) return true;
            ResetFilter(now, _hasFilter);
        }
        else FilterStep(now);
        if (seen) _lastSeenTime = now;
        if (LastTrust >= _rejectBelow) _lastGoodTime = now;
        SolvedFrameId = fid;
        return true;
    }

    /// <summary>Trust from the image error (1 = good, 0 = too bad).</summary>
    private float ErrTrust() => Mathf.Clamp01((_errBad - _fit.ImageError) / Mathf.Max(_errBad - _errGood, 1e-4f));

    /// <summary>Restarts the filter from the current solve (first time, or after losing the hand for long).</summary>
    /// <param name="keepUnsureFingers">Hand lost for long and seen again: fingers the model is unsure about KEEP their previous pose
    /// (blended by confidence) instead of jumping to the result.</param>
    private void ResetFilter(float now, bool keepUnsureFingers = false)
    {
        _fP = _fit.Position;
        _fR = _fit.Rotation;
        if (keepUnsureFingers)
        {
            float wT = _thumbFrozen ? 0f : ConfTrust(1, 4), wI = _indexFrozen ? 0f : ConfTrust(5, 8);
            for (int k = 0; k < 8; k++) _fA[k] = Mathf.Lerp(_fA[k], _fit.Angles[k], k < 4 ? wT : wI);
        }
        else System.Array.Copy(_fit.Angles, _fA, 8);
        _rawPrevR = _fit.Rotation;
        _rawPrevTime = now;
        _rawAngVel = Vector3.zero;
        _fSpeedP = _fSpeedR = 0f;
        System.Array.Clear(_fSpeedA, 0, 8);
        _fVel = _fAngVel = Vector3.zero;
        System.Array.Clear(_fVelA, 0, 8);
        _fTime = _fTimeP = _fTimeR = _fTimeT = _fTimeI = _rotTrustedTime = now;
        _hasFilter = true;
    }

    /// <summary>One One Euro filter step for a new solve. Position, rotation, thumb and index are judged separately: trust below
    /// _rejectBelow (or an unconfirmed rotation jump, or a frozen finger) -> that part waits; accepted -> followed at the One Euro
    /// rate times max(trust, _acceptFloor). The wrist POSITION always follows at least at _positionFloor while the palm is seen.
    /// Each part's time step counts from its latest accepted result, so the first good result after a few dropped ones pulls it
    /// to the right place at once.</summary>
    private void FilterStep(float now)
    {
        Vector3 p = _fit.Position;
        Quaternion r = _fit.Rotation;
        float err = ErrTrust(), palm = PalmConfTrust();
        // Confirmed = close to the previous raw result, or to where the previous two raw results were turning (a fast wrist turn moves
        // more than _confirmDegrees between images: recording 30/10 16:03, every result of a turn counted as a jump and the virtual
        // hand stood still until the turn ended)
        float dtRaw = now - _rawPrevTime;
        Quaternion ahead = Quaternion.AngleAxis(_rawAngVel.magnitude * dtRaw, _rawAngVel.normalized) * _rawPrevR;
        bool confirmed = dtRaw < _confirmSeconds &&
                         (Quaternion.Angle(r, _rawPrevR) < _confirmDegrees || Quaternion.Angle(r, ahead) < _confirmDegrees);
        bool jump = Quaternion.Angle(r, _fR) > _jumpDegrees && !confirmed;
        _rawAngVel = dtRaw > 1e-3f && dtRaw < _confirmSeconds ? AngularVelocity(_rawPrevR, r, dtRaw) : Vector3.zero;
        _rawPrevR = r;
        _rawPrevTime = now;

        float rotTrust = err * Mathf.Max(palm, 0.3f);
        float tT = _thumbFrozen ? 0f : err * ConfTrust(1, 4);
        float tI = _indexFrozen ? 0f : err * ConfTrust(5, 8);
        LastTrust = WristTrust = rotTrust;
        ThumbTrust = tT;
        IndexTrust = tI;

        float aP = palm >= _seenTrust ? Mathf.Max(Accept(rotTrust), _positionFloor) : 0f;
        if (aP > 0f)
        {
            float te = Mathf.Max(now - _fTimeP, 1e-3f);
            _fSpeedP = Mathf.Lerp(_fSpeedP, Vector3.Distance(p, _fP) / te, OneEuroAlpha(te, _speedCutoff));
            Vector3 newP = Vector3.Lerp(_fP, p, OneEuroAlpha(te, _minCutoff + _betaPosition * _fSpeedP) * aP);
            _fVel = Vector3.Lerp(_fVel, (newP - _fP) / te, 0.5f);
            _fP = newP;
            _fTimeP = now;
        }
        else _fVel = Vector3.zero; // do not extrapolate along an old velocity while results are being dropped

        float aR = jump ? 0f : Accept(rotTrust);
        if (aR > 0f) _rotTrustedTime = now;
        // Rotation held for long although consecutive results agree: follow them slowly instead of freezing
        else if (confirmed && palm >= _seenTrust && now - _rotTrustedTime > _maxRotationWait) aR = _staleFollow;
        if (aR > 0f)
        {
            float te = Mathf.Max(now - _fTimeR, 1e-3f);
            _fSpeedR = Mathf.Lerp(_fSpeedR, Quaternion.Angle(r, _fR) / te, OneEuroAlpha(te, _speedCutoff));
            Quaternion newR = Quaternion.Slerp(_fR, r, OneEuroAlpha(te, _minCutoff + _betaRotation * _fSpeedR) * aR);
            _fAngVel = Vector3.Lerp(_fAngVel, AngularVelocity(_fR, newR, te), 0.5f);
            _fR = newR;
            _fTimeR = now;
        }
        else _fAngVel = Vector3.zero;

        FilterFinger(0, Accept(tT), ref _fTimeT, now);
        FilterFinger(4, Accept(tI), ref _fTimeI, now);
        _fTime = now;
    }

    /// <summary>The 4 angles of one finger (starting at first) under FilterStep's accept / reject rule.</summary>
    private void FilterFinger(int first, float accept, ref float lastTime, float now)
    {
        if (accept <= 0f)
        {
            for (int k = first; k < first + 4; k++) _fVelA[k] = 0f;
            return;
        }
        float te = Mathf.Max(now - lastTime, 1e-3f);
        float ad = OneEuroAlpha(te, _speedCutoff);
        for (int k = first; k < first + 4; k++)
        {
            float x = _fit.Angles[k];
            _fSpeedA[k] = Mathf.Lerp(_fSpeedA[k], Mathf.Abs(x - _fA[k]) / te, ad);
            float newA = Mathf.Lerp(_fA[k], x, OneEuroAlpha(te, _minCutoff + _betaAngle * _fSpeedA[k]) * accept);
            _fVelA[k] = Mathf.Lerp(_fVelA[k], (newA - _fA[k]) / te, 0.5f);
            _fA[k] = newA;
        }
        lastTime = now;
    }

    /// <summary>Trust -> follow factor: below _rejectBelow = 0 (dropped), otherwise at least _acceptFloor.</summary>
    private float Accept(float trust) => trust >= _rejectBelow ? Mathf.Max(trust, _acceptFloor) : 0f;

    private static Vector3 AngularVelocity(Quaternion from, Quaternion to, float te)
    {
        (to * Quaternion.Inverse(from)).ToAngleAxis(out float dAng, out Vector3 axis);
        if (dAng > 180f) dAng -= 360f;
        return float.IsFinite(axis.x) ? axis * (dAng / te) : Vector3.zero;
    }

    /// <summary>Trust (0..1) from the mean confidence of image points [from..to] of the latest solve (Python model).
    /// Points near / outside the image edge count as 0 (_rejectEdgePoints).</summary>
    private float ConfTrust(int from, int to)
    {
        float sum = 0f;
        for (int i = from; i <= to; i++) sum += _confIn[i];
        return ConfToTrust(sum / (to - from + 1));
    }

    /// <summary>Confidence of the 3 palm points (wrist, thumb base, index base), which decide the wrist.</summary>
    private float PalmConfTrust()
    {
        float sum = 0f;
        foreach (int i in PalmPoints) sum += _confIn[i];
        return ConfToTrust(sum / PalmPoints.Length);
    }

    private float ConfToTrust(float meanConf) =>
        Mathf.Clamp01((meanConf - _confLow) / Mathf.Max(_confHigh - _confLow, 1e-4f));

    private static float OneEuroAlpha(float te, float cutoff)
    {
        float r = 2f * Mathf.PI * cutoff * te;
        return r / (r + 1f);
    }
}
