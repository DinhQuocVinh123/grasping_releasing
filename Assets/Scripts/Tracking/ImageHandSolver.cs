using UnityEngine;

/// <summary>
/// Builds the virtual glove hand FROM THE CAMERA IMAGE (see HandImageFit): for each image result from Python, the
/// 9 points become 9 rays from the camera at capture time -> solve once -> between two images the hand only glides
/// smoothly towards the new result (it is NOT re-solved against the jittery Quest wrist). When Python loses the hand,
/// the last pose is kept for _holdSeconds, then the hand blends back to the Quest hand.
///
/// FingerUDPReceiver calls Drive() every time HandVisual has written the joints. Disabling this component gives the
/// plain Quest hand (only the middle, ring and little fingers stay curled).
///
/// Smoothing, per image result: a One Euro filter (strong when the hand is still, light when it moves), then two
/// display smoothing stages per rendered frame, plus extrapolation along the filtered velocity to make up for their lag.
/// _betaPosition 200 and _displaySeconds 0.015 were chosen with tools/analysis/filter_sim.py on recorded logs: lower latency
/// with no visible jitter.
/// </summary>
public sealed class ImageHandSolver : MonoBehaviour
{
    [Tooltip("Time constant (s) of EACH of the two chained display smoothing stages, from the filtered result to the rendered frame, so the hand " +
             "moves EVENLY between images (~18 images/s) and hides what the One Euro filter lets through. 0.015 s: about 18 ms less latency " +
             "than 0.02 with jitter while moving 0.45 -> 0.69 mm (tools/analysis/filter_sim.py); 0.01 saves 26 ms but jitters 2.5x more.")]
    [SerializeField] private float _displaySeconds = 0.015f;
    [Header("One Euro filter (per solve): hand still -> strong smoothing, hand moving -> follows quickly")]
    [Tooltip("Cutoff frequency with the hand still (Hz). Lower = smoother but more lag.")]
    [SerializeField] private float _minCutoff = 0.7f;
    [Tooltip("Extra cutoff per unit of wrist speed (Hz per m/s). 200 follows faster when the hand starts moving " +
             "(simulated still-hand jitter 1.07 -> 1.15 mm vs 80); minCutoff barely affects latency.")]
    [SerializeField] private float _betaPosition = 200f;
    [Tooltip("Extra cutoff per unit of wrist rotation speed (Hz per degree/s).")]
    [SerializeField] private float _betaRotation = 0.1f;
    [Tooltip("Extra cutoff per unit of finger joint speed (Hz per degree/s), per joint.")]
    [SerializeField] private float _betaAngle = 0.05f;
    [Tooltip("Cutoff used when estimating speed (Hz).")]
    [SerializeField] private float _speedCutoff = 1f;
    [Tooltip("Between two images, push the wrist forward along its velocity by this fraction (0 = wait for the next image). " +
             "1 = move on at the current velocity -> makes up for the lag of the two display smoothing stages.")]
    [SerializeField] private float _extrapolate = 1f;
    [Tooltip("Same as above, for the wrist rotation and finger angles (1 makes the still hand's rotation jitter clearly worse).")]
    [SerializeField] private float _extrapolateRotation = 0.5f;
    [Tooltip("Extrapolate at most this long after the last solve (images arrive about every 0.056 s).")]
    [SerializeField] private float _maxExtrapolateSeconds = 0.06f;
    [Header("Trust of each result (no jumps when the two fingertips nearly touch)")]
    [Tooltip("Image error (in palm lengths) below this = fully trusted; above _errBad = almost ignored. Measured: free hand mean 0.042, " +
             "fingertips about to touch 0.147, where the wrist jumped 26-78 degrees and the thumb 10-18 degrees per result and the One Euro filter took it for fast motion.")]
    [SerializeField] private float _errGood = 0.07f;
    [SerializeField] private float _errBad = 0.20f;
    [Tooltip("Wrist rotation jumping more than this (degrees) from the filtered result, NOT confirmed by the previous result -> trusted only 20% " +
             "(with _trustRejectsNotSlows: the result is dropped).")]
    [SerializeField] private float _jumpDegrees = 15f;
    [Tooltip("Two consecutive results within this (degrees, less than 0.2 s apart) = a real motion is confirmed (e.g. a fast wrist turn).")]
    [SerializeField] private float _confirmDegrees = 10f;
    [Tooltip("Mean confidence of a finger's points (Python model), from low to high: below = keep the finger's pose, above = fully trusted. " +
             "With the fingertips about to touch, thumb points score only 0.2-0.35 (clearly visible: 0.6-1.2).")]
    [SerializeField] private float _confLow = 0.25f;
    [SerializeField] private float _confHigh = 0.6f;
    [Tooltip("Minimum trust of a result (so the hand never fully stops while the model stays unsure). Old mode only.")]
    [SerializeField] private float _minTrust = 0.03f;
    [Tooltip("Results trusted below this do NOT count as 'hand seen' (hand at the frame edge / occluded -> the model guesses wrong; " +
             "seen as a fit of 0.03 for long periods while the weight stayed 1). Only bad results -> keep the last good pose for _holdSeconds, then the Quest hand, " +
             "and GloveForceEsp32Output's force gate goes to 0. After losing the hand, the FIRST result must reach this to be used. 0 = old behaviour.")]
    [SerializeField] private float _minGoodTrust = 0.1f;

    [Header("Latency: drop bad results, follow accepted results QUICKLY")]
    [Tooltip("ON: trust is only used to REJECT results (below _rejectBelow = ignored); accepted results are followed at least at " +
             "_acceptFloor. The wrist only looks at the image error + the 3 palm points (wrist, thumb base, index base), so an occluded thumb " +
             "does not slow it down.\n\nOFF (old mode): follow speed MULTIPLIED by trust -> with the fingertips about to touch (trust ~0.1) the virtual hand " +
             "lagged behind for about 2 s.")]
    [SerializeField] private bool _trustRejectsNotSlows = true;
    [Tooltip("Results (wrist or each finger) trusted below this are dropped, no update. Same as _minGoodTrust: below it the hand counts as not seen.")]
    [SerializeField] private float _rejectBelow = 0.1f;
    [Tooltip("Accepted results are followed at least this fast compared with a fully trusted result (1 = always like the best result).")]
    [Range(0.1f, 1f)]
    [SerializeField] private float _acceptFloor = 0.6f;

    [Header("Latency compensation from Quest motion (EXPERIMENTAL, off by default)")]
    [Tooltip("ON: wrist position = image result + how far the Quest hand has moved since that image was CAPTURED -> compensates the Wi-Fi + Python delay. " +
             "The Quest's depth for the gloved hand jitters 4-10 cm, so only turn this on once glove_diag shows the Quest motion is smooth enough.")]
    [SerializeField] private bool _useQuestMotion = false;
    [Tooltip("Only use the part of the Quest motion PERPENDICULAR to the camera ray (drop near/far, where the Quest jitters most).")]
    [SerializeField] private bool _questMotionLateralOnly = true;
    [Tooltip("Maximum compensation (m), to block wrong Quest jumps.")]
    [SerializeField] private float _questMotionMaxMeters = 0.15f;

    [Header("Hand leaving the camera frame")]
    [Tooltip("ON: points from Python in the image edge band or outside the image (the model GUESSES them) get weight 0 in the solve. " +
             "A finger with fewer than _minFingerPoints points in frame KEEPS its last good pose. The whole hand with fewer than _minInFramePoints " +
             "points (or none of the 3 palm points) -> no solve, the hand counts as not seen.")]
    [SerializeField] private bool _rejectEdgePoints = true;
    [Tooltip("Image edge band (fraction of width / height) treated as 'too close to the edge, not trusted'.")]
    [Range(0f, 0.15f)]
    [SerializeField] private float _edgeMargin = 0.03f;
    [Tooltip("The hand needs at least this many points (of 9) in frame to be solved.")]
    [Range(3, 9)]
    [SerializeField] private int _minInFramePoints = 5;
    [Tooltip("Each finger (4 points) needs at least this many points in frame, otherwise it keeps its previous pose.")]
    [Range(1, 4)]
    [SerializeField] private int _minFingerPoints = 2;
    [Tooltip("ON: when the hand is lost for long, only the WRIST goes back to the Quest; thumb and index KEEP their last good pose. OFF (old): fingers also go " +
             "back to the Quest hand, which cannot see gloved fingers, so the virtual fingers got twisted.")]
    [SerializeField] private bool _keepFingersWhenLost = true;
    [Tooltip("ON: while the thumb or index pose is HELD (out of frame) -> ForceWeight goes to 0 and GloveForceEsp32Output's force gate fades " +
             "the force out (no pushing on a finger that is frozen).")]
    [SerializeField] private bool _cutForceWhenFingerFrozen = true;

    [Tooltip("When the solver switches to a different interpretation (e.g. escaping a wrongly bent finger pose), glide to the new pose over this time (s).")]
    [SerializeField] private float _switchSeconds = 0.3f;
    [Tooltip("Python lost the hand: keep the last pose this long (s) before going back to the Quest hand.")]
    [SerializeField] private float _holdSeconds = 1.0f;
    [Tooltip("Blend time between the image hand and the Quest hand (s).")]
    [SerializeField] private float _blendSeconds = 0.3f;
    [Tooltip("The Quest depth (jitters 4-10 cm with the glove) is averaged over this many seconds before being used as a hint.")]
    [SerializeField] private float _depthPriorSeconds = 0.6f;
    [Tooltip("The Quest wrist rotation is smoothed over this many seconds before being used as a hint (only to choose palm up / down).")]
    [SerializeField] private float _rotPriorSeconds = 0.3f;
    [Tooltip("If the previous answer is older than this (s), solve from scratch instead of starting from it.")]
    [SerializeField] private float _warmSeconds = 0.5f;
    [Tooltip("Points with a confidence below this are ignored.")]
    [SerializeField] private float _minConfidence = 0.2f;
    [Tooltip("Weight of point 1 (thumb base near the wrist; no tape marker there, so the model places it less reliably).")]
    [SerializeField] private float _thumbBaseWeight = 0.5f;
    [Tooltip("Use the Quest wrist rotation as a (weak) hint. OFF by default: with the glove the Quest often guesses the palm BACKWARDS " +
             "(back of the hand towards the camera while the Quest reports the palm facing the eyes) -> pulls the virtual hand into a sideways tilt.")]
    [SerializeField] private bool _useQuestRotation = false;
    [Tooltip("Use the hexagon plate on the back of the hand (found by colour in Python) to tell how far the hand is turned over. OFF: " +
             "re-running 5 recordings with it on made the image error worse (e.g. 0.056 -> 0.066) and doubled finger jitter; the point Python finds " +
             "is on average 0.42 palm lengths from the model position (it often picks up wood / cardboard / the mouse) -> wrong wrist, and the index " +
             "finger bends to compensate, worst with the arm stretched out.")]
    [SerializeField] private bool _useDorsalTiles = false;
    [Tooltip("Glove hand size = the size the Quest measures for the BARE LEFT HAND (Hand.Scale); both hands are the same size, and the Quest's size " +
             "estimate for the gloved hand is unreliable. Until the left hand has been seen, keep the size the Quest set.")]
    [SerializeField] private bool _useLeftHandScale = true;

    // The 3 palm points: wrist (0), thumb base (1), index base (5) -- they decide the wrist position / rotation
    private static readonly int[] PalmPoints = { 0, 1, 5 };

    private FingerUDPReceiver _receiver;
    private GloveLiveStreamer _streamer;
    private Transform _wrist;
    private FingerChainFitter _thumb, _index;
    private HandImageFit _fit;
    private Transform[] _between;      // bones between the wrist and the finger bases (e.g. the index metacarpal)
    private Quaternion[] _betweenRest;

    private readonly Vector2[] _uv = new Vector2[HandImageFit.Points];
    private readonly float[] _conf = new float[HandImageFit.Points];
    private readonly float[] _confIn = new float[HandImageFit.Points]; // = _conf, with points near / outside the image edge set to 0
    private readonly Vector3[] _dirs = new Vector3[HandImageFit.Points];
    private readonly float[] _w = new float[HandImageFit.Points];

    private int _lastFid;
    private float _lastGoodTime = -999f;     // last result that reached _minGoodTrust
    private float _depthPrior;
    private bool _hasDepthPrior;
    private float _lastDepthTime;
    private Quaternion _rotPrior;
    private bool _hasRotPrior;
    private float _lastRotTime;

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
    private float _fTimeW, _fTimeT, _fTimeI;    // latest ACCEPTED wrist / thumb / index result (new mode)
    private Quaternion _rawPrevR = Quaternion.identity; // previous raw result (to confirm jumps)
    private float _rawPrevTime = -999f;
    // Middle smoothing stage (display = 2 chained stages)
    private Vector3 _midP;
    private Quaternion _midR = Quaternion.identity;
    private readonly float[] _midA = new float[8];
    private bool _hasFilter;
    // Quest hand centre + camera AT CAPTURE TIME of the solved image (latency compensation from Quest motion)
    private Vector3 _solveHintCenter, _solveCamPos;
    private bool _hasSolveHint;

    // --- Diagnostics (written to glove_diag by FingerUDPReceiver) ---
    public float Weight => _weight;
    public int SolvedFrameId { get; private set; }
    public float ImageError => _fit != null ? _fit.ImageError : 0f;
    public float Cost => _fit != null ? _fit.LastCost : 0f;
    public float Depth => _fit != null ? _fit.Depth : 0f;
    public float DepthPrior => _hasDepthPrior ? _depthPrior : 0f;
    public float Pinch => _fit != null ? _fit.Pinch : 0f;
    public int Switches => _fit != null ? _fit.Switches : 0;
    public float SolveMs { get; private set; }
    /// <summary>Trust of the latest solve (0..1) for the wrist position / rotation.</summary>
    public float LastTrust { get; private set; } = 1f;
    public bool Ready => _fit != null;
    public int DorsalUsed => _fit != null ? _fit.DorsalUsed : -1;
    /// <summary>Current glove hand size (1 = the reference skeleton, palm about 9.9 cm).</summary>
    public float HandScale => _wrist != null ? _wrist.lossyScale.x : 0f;
    /// <summary>Age of the solved image (ms): from capture until Python's result reached Unity.</summary>
    public float FrameAgeMs { get; private set; }
    /// <summary>Like FrameAgeMs but from the camera EXPOSURE (earlier still, includes camera processing). 0 = unknown.</summary>
    public float ExposureAgeMs { get; private set; }
    /// <summary>RAW wrist solved from the latest image (not filtered, not extrapolated) and that image's exposure time
    /// (Time.realtimeSinceStartup, 0 = unknown): "the real hand at capture time", to measure virtual vs real hand latency.</summary>
    public Vector3 RawWrist { get; private set; }
    public float RawExposureTime { get; private set; }
    /// <summary>How far the shown wrist is from the latest solve (mm / degrees): the lag added by the filters.</summary>
    public float LagMm { get; private set; }
    public float LagDeg { get; private set; }
    /// <summary>Trust of the latest solve: wrist, thumb, index (0..1, before the accept / reject thresholds).</summary>
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

    private float _initWristScale = 1f;
    private Oculus.Interaction.HandVisual _gloveVisual, _leftVisual;
    private float _leftScale;

    /// <summary>Called by FingerUDPReceiver right after creating the five finger kinematics (rig in its rest pose).</summary>
    /// <param name="fitters">5 fingers: thumb, index (built), middle / ring / little (used to place the hexagon plate when curled).</param>
    public void Initialize(FingerUDPReceiver receiver, Transform wrist, FingerChainFitter[] fitters, Vector3 palmTarget, Vector3 closedAngles)
    {
        _receiver = receiver;
        _wrist = wrist;
        _thumb = fitters[0];
        _index = fitters[1];
        if (wrist == null || _thumb == null || _index == null) return;
        FingerChainFitter thumb = _thumb, index = _index;
        _fit = new HandImageFit(wrist, fitters, palmTarget, closedAngles);
        _initWristScale = Mathf.Max(wrist.lossyScale.x, 1e-4f);
        _gloveVisual = receiver.GetComponent<Oculus.Interaction.HandVisual>();

        var list = new System.Collections.Generic.List<Transform>();
        foreach (Transform root in new[] { thumb.RootBone, index.RootBone })
            for (Transform t = root.parent; t != null && t != wrist; t = t.parent)
                if (!list.Contains(t)) list.Add(t);
        _between = list.ToArray();
        _betweenRest = new Quaternion[_between.Length];
        for (int i = 0; i < _between.Length; i++) _betweenRest[i] = _between[i].localRotation;
    }

    /// <summary>Builds the virtual hand for this frame. questUpdated = HandVisual has just written a new wrist (questPos/Rot).
    /// holdLonger = holding an object while data is lost -> keep the last pose longer (so the object is not dropped).</summary>
    public void Drive(bool questUpdated, Vector3 questPos, Quaternion questRot, bool dataValid, bool holdLonger = false)
    {
        if (_fit == null) return;
        ApplyHandScale();
        if (questUpdated) UpdateRotPrior(questRot);
        if (dataValid) TrySolveNewFrame();

        if (Time.frameCount != _dispFrame)
        {
            _dispFrame = Time.frameCount;
            bool fresh = _fit.HasSolution && (Time.time - _lastGoodTime <= _holdSeconds || holdLonger);
            _weight = Mathf.MoveTowards(_weight, fresh ? 1f : 0f, Time.deltaTime / Mathf.Max(_blendSeconds, 1e-3f));
            bool frozen = _thumbFrozen || _indexFrozen;
            _forceGate = _cutForceWhenFingerFrozen && frozen ? 0f : 1f; // GloveForceEsp32Output smooths the gate itself
            State = !fresh ? 2 : (EdgePoints > 0 || frozen ? 1 : 0);
            if (_fit.HasSolution)
            {
                if (!_hasFilter) ResetFilter(Time.time);
                if (!_hasDisp || _weight <= 0.001f)
                {
                    _dispP = _midP = _fP;
                    _dispR = _midR = _fR;
                    System.Array.Copy(_fA, _dispA, 8);
                    System.Array.Copy(_fA, _midA, 8);
                    _hasDisp = true;
                }
                else
                {
                    // The solver just switched interpretation (a clearly different hand pose): glide a bit slower
                    if (_fit.Switches != _seenSwitches)
                    {
                        _seenSwitches = _fit.Switches;
                        _slowUntil = Time.time + _switchSeconds;
                    }
                    float tau = Time.time < _slowUntil ? _switchSeconds * 0.25f : _displaySeconds;
                    float a = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(tau, 1e-3f));
                    // Target = the filtered result, pushed on along the velocity since the last solve
                    float ahead = Mathf.Clamp(Time.time - _fTime, 0f, _maxExtrapolateSeconds);
                    float aheadR = _extrapolateRotation * ahead;
                    Vector3 tgtP = _fP + _extrapolate * ahead * _fVel;
                    if (_useQuestMotion && TryQuestMotion(out Vector3 questDelta)) tgtP = _fP + questDelta;
                    Quaternion tgtR = Quaternion.AngleAxis(_fAngVel.magnitude * aheadR, _fAngVel.normalized) * _fR;
                    // Two chained smoothing stages: continuous display velocity (no stepping at the image rate)
                    _midP = Vector3.Lerp(_midP, tgtP, a);
                    _dispP = Vector3.Lerp(_dispP, _midP, a);
                    _midR = Quaternion.Slerp(_midR, tgtR, a);
                    _dispR = Quaternion.Slerp(_dispR, _midR, a);
                    for (int k = 0; k < 8; k++)
                    {
                        _midA[k] = Mathf.Lerp(_midA[k], _fA[k] + aheadR * _fVelA[k], a);
                        _dispA[k] = Mathf.Lerp(_dispA[k], _midA[k], a);
                    }
                }
                LagMm = Vector3.Distance(_dispP, _fit.Position) * 1000f;
                LagDeg = Quaternion.Angle(_dispR, _fit.Rotation);
            }
        }

        // Write to the bones (on every call -- HandVisual may have just overwritten them). Wrist: blend image hand <-> Quest hand by _weight.
        // Fingers: with _keepFingersWhenLost always the image pose (the last good pose when the hand is lost), since the Quest cannot see gloved fingers.
        float w = _hasDisp ? _weight : 0f;
        float wf = _keepFingersWhenLost ? (_hasDisp ? 1f : 0f) : w;
        if (wf > 0f)
            for (int i = 0; i < _between.Length; i++)
                _between[i].localRotation = Quaternion.Slerp(_between[i].localRotation, _betweenRest[i], wf);
        if (w > 0f)
            _wrist.SetPositionAndRotation(Vector3.Lerp(questPos, _dispP, w), Quaternion.Slerp(questRot, _dispR, w));
        _thumb.SetAngles(_dispA[0], _dispA[1], _dispA[2], _dispA[3]);
        _index.SetAngles(_dispA[4], _dispA[5], _dispA[6], _dispA[7]);
        _thumb.Apply(wf);
        _index.Apply(wf);
    }

    /// <summary>How far the Quest hand has moved since the solved image was captured (hand centre = middle finger base, the same point as the hint).</summary>
    private bool TryQuestMotion(out Vector3 delta)
    {
        delta = Vector3.zero;
        if (!_hasSolveHint || _receiver == null || !_receiver.TryGetRawHandPose(out Vector3 center, out _)) return false;
        delta = center - _solveHintCenter;
        if (_questMotionLateralOnly)
        {
            Vector3 ray = _solveHintCenter - _solveCamPos;
            if (ray.sqrMagnitude > 1e-6f) delta = Vector3.ProjectOnPlane(delta, ray.normalized);
        }
        delta = Vector3.ClampMagnitude(delta, _questMotionMaxMeters);
        return true;
    }

    /// <summary>Glove hand size from the bare left hand, then tells the solver the current size (HandVisual resets the
    /// hand root scale every frame; this runs AFTER HandVisual, so it can overwrite it).</summary>
    private void ApplyHandScale()
    {
        if (_useLeftHandScale && _gloveVisual != null && _gloveVisual.Root != null)
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
                Transform root = _gloveVisual.Root;
                float parent = root.parent != null ? Mathf.Max(root.parent.lossyScale.x, 1e-4f) : 1f;
                root.localScale = Vector3.one * (_leftScale / parent);
            }
        }
        _fit.SetScale(_wrist.lossyScale.x / _initWristScale);
    }

    private void UpdateRotPrior(Quaternion questRot)
    {
        if (!_hasRotPrior || Time.time - _lastRotTime > 1f)
            _rotPrior = questRot;
        else
            _rotPrior = Quaternion.Slerp(_rotPrior, questRot,
                1f - Mathf.Exp(-(Time.time - _lastRotTime) / Mathf.Max(_rotPriorSeconds, 1e-3f)));
        _hasRotPrior = true;
        _lastRotTime = Time.time;
    }

    private bool InFrame(Vector2 uv) =>
        uv.x >= _edgeMargin && uv.x <= 1f - _edgeMargin && uv.y >= _edgeMargin && uv.y <= 1f - _edgeMargin;

    private void TrySolveNewFrame()
    {
        if (!_receiver.TryGetPixelPoints(out int fid, _uv, _conf, out int dorsal, out Vector2 dorsalUV) || fid == _lastFid) return;
        _lastFid = fid;
        if (_streamer == null) _streamer = FindAnyObjectByType<GloveLiveStreamer>();
        if (_streamer == null || !_streamer.TryGetFramePose(fid, out Pose cam)) return;
        if (_streamer.TryGetFrameAge(fid, out float age)) FrameAgeMs = age * 1000f;

        // Points near / outside the image edge: the model guesses instead of seeing -> weight 0, treated as confidence 0
        int inFrame = 0, palmIn = 0, thumbIn = 0, indexIn = 0, edge = 0;
        for (int i = 0; i < HandImageFit.Points; i++)
        {
            if (!_streamer.TryGetFrameRay(fid, _uv[i], out Ray ray)) return;
            _dirs[i] = ray.direction;
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
        if (_rejectEdgePoints && (inFrame < _minInFramePoints || palmIn == 0))
        {
            // Too few really visible points -> no solve (it would give a twisted hand); does not refresh "hand last seen"
            WristTrust = ThumbTrust = IndexTrust = 0f;
            return;
        }
        _thumbFrozen = _rejectEdgePoints && thumbIn < _minFingerPoints;
        _indexFrozen = _rejectEdgePoints && indexIn < _minFingerPoints;

        // Hand depth from the Quest AT CAPTURE TIME (hand centre = middle finger base), averaged over time
        _hasSolveHint = false;
        if (_streamer.TryGetFrameHint(fid, out Vector3 center, out _))
        {
            _solveHintCenter = center;
            _solveCamPos = cam.position;
            _hasSolveHint = true;
            float z = Vector3.Dot(center - cam.position, cam.rotation * Vector3.forward);
            if (z > 0.08f && z < 1.2f)
            {
                float since = Time.time - _lastDepthTime;
                _depthPrior = !_hasDepthPrior || since > 2f ? z
                    : Mathf.Lerp(_depthPrior, z, 1f - Mathf.Exp(-since / Mathf.Max(_depthPriorSeconds, 1e-3f)));
                _hasDepthPrior = true;
                _lastDepthTime = Time.time;
            }
        }

        // Hexagon plate on the back of the hand: 1 = seen (ray through its centre), 0 = searched but not seen, -1 = unknown
        Vector3 dorsalDir = Vector3.zero;
        if (!_useDorsalTiles) dorsal = -1;
        else if (dorsal == 1)
        {
            if (_streamer.TryGetFrameRay(fid, dorsalUV, out Ray dr)) dorsalDir = dr.direction;
            else dorsal = -1;
        }

        // Start from the previous answer only while it is still GOOD (a bad starting point easily gets stuck in a wrong pose)
        bool warm = Time.time - _lastGoodTime <= _warmSeconds;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool ok = _fit.Solve(cam.position, cam.rotation, _dirs, _w,
                             _hasDepthPrior ? _depthPrior : 0f, _useQuestRotation && _hasRotPrior ? _rotPrior : (Quaternion?)null,
                             warm, dorsal, dorsalDir);
        SolveMs = (float)sw.Elapsed.TotalMilliseconds;
        if (!ok) return;
        // "Real hand at capture time" stamp: raw result + exposure time (recorded even if the filter drops this result)
        RawWrist = _fit.Position;
        if (_streamer.TryGetFrameExposureTime(fid, out float exposure))
        {
            RawExposureTime = exposure;
            ExposureAgeMs = (Time.realtimeSinceStartup - exposure) * 1000f;
        }
        else RawExposureTime = ExposureAgeMs = 0f;
        if (!_hasFilter || Time.time - _lastGoodTime > _holdSeconds)
        {
            // Restart (first time / after losing the hand): only take a GOOD result, otherwise the hand keeps its current pose
            LastTrust = ResultTrust();
            WristTrust = LastTrust;
            if (LastTrust < _minGoodTrust) return;
            ResetFilter(Time.time, _hasFilter);
        }
        else if (_trustRejectsNotSlows) FilterStepGated(Time.time);
        else FilterStep(Time.time);
        if (LastTrust >= _minGoodTrust) _lastGoodTime = Time.time;
        SolvedFrameId = fid;
    }

    /// <summary>Trust from the image error (1 = good, 0 = too bad).</summary>
    private float ErrTrust() => Mathf.Clamp01((_errBad - _fit.ImageError) / Mathf.Max(_errBad - _errGood, 1e-4f));

    /// <summary>Trust of the result just solved, from the image error + point confidence (like FilterStep, without the rotation-jump rule).</summary>
    private float ResultTrust()
    {
        float w = ErrTrust();
        if (_trustRejectsNotSlows) return w * Mathf.Max(PalmConfTrust(), 0.3f);
        return Mathf.Max(w * Mathf.Max(ConfTrust(0, 8), 0.3f), _minTrust);
    }

    /// <summary>Restarts the filter from the current solve (first time, or after losing the hand for long).</summary>
    /// <param name="keepUnsureFingers">Hand lost for long and seen again: fingers the model is unsure about KEEP their previous pose (blended by
    /// confidence) instead of jumping to the result -- with the fingertips about to touch, results come rarely (more than 1 s apart) and the thumb jumped 20-80 degrees.</param>
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
        _fSpeedP = _fSpeedR = 0f;
        System.Array.Clear(_fSpeedA, 0, 8);
        _fVel = _fAngVel = Vector3.zero;
        System.Array.Clear(_fVelA, 0, 8);
        _fTime = _fTimeW = _fTimeT = _fTimeI = now;
        _hasFilter = true;
    }

    /// <summary>One One Euro filter step for a new solve: cutoff = _minCutoff + beta * (smoothed) speed,
    /// so a still hand is smoothed strongly (little jitter) and a moving hand is followed almost at once (little lag).
    /// OLD mode: the follow speed is multiplied by the trust (see _trustRejectsNotSlows).</summary>
    private void FilterStep(float now)
    {
        float te = Mathf.Max(now - _fTime, 1e-3f);
        float ad = OneEuroAlpha(te, _speedCutoff);
        Vector3 p = _fit.Position;
        Quaternion r = _fit.Rotation;

        // Trust of this result: image error, unconfirmed sudden rotation jump, model point confidence.
        float w = ErrTrust();
        bool confirmed = Quaternion.Angle(r, _rawPrevR) < _confirmDegrees && now - _rawPrevTime < 0.2f;
        if (Quaternion.Angle(r, _fR) > _jumpDegrees && !confirmed) w *= 0.2f;
        float wFingers = w;
        w *= Mathf.Max(ConfTrust(0, 8), 0.3f);
        float wT = _thumbFrozen ? 0f : Mathf.Max(wFingers * ConfTrust(1, 4), _minTrust);
        float wI = _indexFrozen ? 0f : Mathf.Max(wFingers * ConfTrust(5, 8), _minTrust);
        w = Mathf.Max(w, _minTrust);
        _rawPrevR = r;
        _rawPrevTime = now;
        LastTrust = WristTrust = w;
        ThumbTrust = wT;
        IndexTrust = wI;

        _fSpeedP = Mathf.Lerp(_fSpeedP, Vector3.Distance(p, _fP) / te, ad * w);
        _fSpeedR = Mathf.Lerp(_fSpeedR, Quaternion.Angle(r, _fR) / te, ad * w);
        Vector3 newP = Vector3.Lerp(_fP, p, OneEuroAlpha(te, _minCutoff + _betaPosition * _fSpeedP) * w);
        _fVel = Vector3.Lerp(_fVel, (newP - _fP) / te, 0.5f * w);
        _fP = newP;
        Quaternion newR = Quaternion.Slerp(_fR, r, OneEuroAlpha(te, _minCutoff + _betaRotation * _fSpeedR) * w);
        _fAngVel = Vector3.Lerp(_fAngVel, AngularVelocity(_fR, newR, te), 0.5f * w);
        _fR = newR;
        for (int k = 0; k < 8; k++)
        {
            float x = _fit.Angles[k], wk = k < 4 ? wT : wI;
            if (wk <= 0f) { _fVelA[k] = 0f; continue; } // finger pose is being held
            _fSpeedA[k] = Mathf.Lerp(_fSpeedA[k], Mathf.Abs(x - _fA[k]) / te, ad * wk);
            float newA = Mathf.Lerp(_fA[k], x, OneEuroAlpha(te, _minCutoff + _betaAngle * _fSpeedA[k]) * wk);
            _fVelA[k] = Mathf.Lerp(_fVelA[k], (newA - _fA[k]) / te, 0.5f * wk);
            _fA[k] = newA;
        }
        _fTime = _fTimeW = _fTimeT = _fTimeI = now;
    }

    /// <summary>One Euro filter, NEW mode: trust is only used to ACCEPT / REJECT a result. Wrist, thumb and index are judged separately:
    /// trust below _rejectBelow (or an unconfirmed wrist rotation jump, or a frozen finger) -> unchanged, velocity 0;
    /// accepted -> followed at the One Euro rate times max(trust, _acceptFloor). The time step te counts from that part's latest
    /// ACCEPTED result, so after a few rejected results the first good one pulls the hand to the right place at once.</summary>
    private void FilterStepGated(float now)
    {
        Vector3 p = _fit.Position;
        Quaternion r = _fit.Rotation;
        float err = ErrTrust();
        bool confirmed = Quaternion.Angle(r, _rawPrevR) < _confirmDegrees && now - _rawPrevTime < 0.2f;
        bool jump = Quaternion.Angle(r, _fR) > _jumpDegrees && !confirmed;
        _rawPrevR = r;
        _rawPrevTime = now;

        float wristTrust = err * Mathf.Max(PalmConfTrust(), 0.3f);
        float tT = _thumbFrozen ? 0f : err * ConfTrust(1, 4);
        float tI = _indexFrozen ? 0f : err * ConfTrust(5, 8);
        LastTrust = WristTrust = wristTrust; // "hand seen" is judged before the rotation-jump rule (as in the old mode)
        ThumbTrust = tT;
        IndexTrust = tI;

        float aW = jump ? 0f : Accept(wristTrust);
        if (aW > 0f)
        {
            float te = Mathf.Max(now - _fTimeW, 1e-3f);
            float ad = OneEuroAlpha(te, _speedCutoff);
            _fSpeedP = Mathf.Lerp(_fSpeedP, Vector3.Distance(p, _fP) / te, ad);
            _fSpeedR = Mathf.Lerp(_fSpeedR, Quaternion.Angle(r, _fR) / te, ad);
            Vector3 newP = Vector3.Lerp(_fP, p, OneEuroAlpha(te, _minCutoff + _betaPosition * _fSpeedP) * aW);
            _fVel = Vector3.Lerp(_fVel, (newP - _fP) / te, 0.5f);
            _fP = newP;
            Quaternion newR = Quaternion.Slerp(_fR, r, OneEuroAlpha(te, _minCutoff + _betaRotation * _fSpeedR) * aW);
            _fAngVel = Vector3.Lerp(_fAngVel, AngularVelocity(_fR, newR, te), 0.5f);
            _fR = newR;
            _fTimeW = now;
        }
        else _fVel = _fAngVel = Vector3.zero; // do not extrapolate along an old velocity while results are being dropped

        FilterFingerGated(0, Accept(tT), ref _fTimeT, now);
        FilterFingerGated(4, Accept(tI), ref _fTimeI, now);
        _fTime = now;
    }

    /// <summary>The 4 angles of one finger (starting at first) under FilterStepGated's accept / reject rule.</summary>
    private void FilterFingerGated(int first, float accept, ref float lastTime, float now)
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
