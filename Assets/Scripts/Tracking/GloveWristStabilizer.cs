using Oculus.Interaction.Input;
using UnityEngine;

/// <summary>
/// Stabilises the WRIST of the glove hand as tracked by the Quest. It sits inside Meta's hand DATA CHAIN
/// (like the Interaction SDK's HandFilter / LastKnownGoodHand), so everything that reads this hand (HandVisual,
/// the squeezable objects, the hand-position hint sent to Python) gets the same stabilised pose.
///
/// Why: with the glove on (black straps and a hexagon plate on the back of the hand) the Quest recognises the
/// hand poorly: the wrist stays still for a few frames, then JUMPS 5-12 cm or turns 20-60 degrees at once
/// (measured: jumps over 3 cm about 190 times per minute, versus about 10 with the earlier glove).
///
/// How:
///   1. A jump FURTHER than a real hand could move in that time (maximum speed _maxSpeed), or low Quest
///      confidence -> suspect: KEEP the old pose instead of following it.
///   2. Suspect, but the Quest reports the same new place for _confirmFrames frames in a row -> the real hand
///      is there (e.g. a fast swing) -> accept it, and GLIDE there over _reacquireSeconds instead of jumping.
///   3. Small jitter: One Euro filter (Casiez 2012; Meta's implementation).
///   4. Short tracking loss (&lt; _holdSeconds): keep the last hand pose.
///
/// Setup: put it on a GameObject; _iModifyDataFromSourceMono = the right hand's SyntheticHandData,
/// Update Mode = After Previous Step, Update After = SyntheticHandData.
/// The glove hand's HandVisual points at this component instead of SyntheticHandData.
/// </summary>
public class GloveWristStabilizer : Hand
{
    [Header("Jump rejection")]
    [Tooltip("Maximum speed of the real hand (m/s). A wrist jump further than this allows is treated as a tracking error.")]
    [SerializeField] private float _maxSpeed = 2.5f;
    [Tooltip("Added to the jump threshold (m), so normal jitter is not treated as a jump.")]
    [SerializeField] private float _jumpMargin = 0.015f;
    [Tooltip("Maximum wrist rotation speed (degrees/s).")]
    [SerializeField] private float _maxAngularSpeed = 400f;
    [SerializeField] private float _rotationMargin = 6f;
    [Tooltip("A new position must repeat for this many frames in a row (within _confirmRadius) to be accepted.")]
    [SerializeField] private int _confirmFrames = 3;
    [SerializeField] private float _confirmRadius = 0.02f;
    [SerializeField] private float _confirmAngle = 15f;
    [Tooltip("When a new position is accepted after holding: glide to it over this time (s) instead of jumping.")]
    [SerializeField] private float _reacquireSeconds = 0.15f;
    [Tooltip("If the Quest loses the hand for less than this (s), keep the last hand pose.")]
    [SerializeField] private float _holdSeconds = 0.5f;

    [Header("Jitter filter (One Euro)")]
    [SerializeField] private float _minCutoff = 1.5f;
    [SerializeField] private float _beta = 0.5f;

    /// <summary>Number of frames HELD (Quest data ignored), for diagnostics.</summary>
    public int HeldFrames { get; private set; }
    public int Reacquires { get; private set; }

    /// <summary>RAW Quest wrist (before any rejection / filtering here), world space, from the latest Quest update.
    /// Used to measure how late and how jittery the Quest sees the gloved hand (glove_diag columns qrx..).
    /// RawValid = the Quest was tracking the hand in that frame.</summary>
    public Pose RawWorldRoot { get; private set; }
    public bool RawValid { get; private set; }
    public bool RawHighConfidence { get; private set; }
    /// <summary>Time.realtimeSinceStartup when the latest raw data arrived.</summary>
    public float RawTime { get; private set; }

    private readonly HandDataAsset _last = new HandDataAsset();
    private IOneEuroFilter<Vector3> _posFilter;
    private IOneEuroFilter<Quaternion> _rotFilter;
    private bool _has, _hasCandidate;
    private Pose _accepted;          // last trusted Quest pose
    private float _sinceAccepted;    // seconds since the last trusted pose
    private Pose _candidate;
    private int _candidateCount;
    private Vector3 _glidePos;       // remaining offset after accepting a new pose (decays to 0)
    private Quaternion _glideRot = Quaternion.identity;
    private Pose _out;
    private float _lostTime;

    protected virtual void Awake()
    {
        _posFilter = OneEuroFilter.CreateVector3();
        _rotFilter = OneEuroFilter.CreateQuaternion();
    }

    protected override void Apply(HandDataAsset data)
    {
        base.Apply(data);
        float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        RecordRaw(data);

        // Quest lost the hand: keep the last pose for a moment (avoids the virtual hand flickering)
        if (!data.IsDataValid || !data.IsTracked)
        {
            _lostTime += dt;
            if (_has && data.IsConnected && _lostTime <= _holdSeconds)
            {
                data.CopyPosesFrom(_last);
                data.IsDataValid = data.IsTracked = data.IsHighConfidence = true;
                data.RootPoseOrigin = PoseOrigin.SyntheticPose;
                HeldFrames++;
            }
            else _has = false;
            return;
        }
        _lostTime = 0f;

        Pose raw = data.Root;
        if (!_has)
        {
            _has = true;
            _accepted = _out = raw;
            _sinceAccepted = 0f;
            _hasCandidate = false;
            _glidePos = Vector3.zero;
            _glideRot = Quaternion.identity;
            _posFilter.Reset();
            _rotFilter.Reset();
            _posFilter.Step(raw.position, dt);
            _rotFilter.Step(raw.rotation, dt);
            _last.CopyFrom(data);
            return;
        }

        // 1. Can this data be trusted?
        _sinceAccepted += dt;
        float jump = Vector3.Distance(raw.position, _accepted.position);
        float turn = Quaternion.Angle(raw.rotation, _accepted.rotation);
        bool suspect = !data.IsHighConfidence ||
                       jump > _maxSpeed * _sinceAccepted + _jumpMargin ||
                       turn > _maxAngularSpeed * _sinceAccepted + _rotationMargin;

        bool accept = !suspect;
        if (suspect && data.IsHighConfidence)
        {
            // 2. The Quest keeps reporting the same new place -> the real hand is there
            if (_hasCandidate && Vector3.Distance(raw.position, _candidate.position) < _confirmRadius &&
                Quaternion.Angle(raw.rotation, _candidate.rotation) < _confirmAngle)
                _candidateCount++;
            else
            {
                _candidate = raw;
                _candidateCount = 1;
                _hasCandidate = true;
            }
            if (_candidateCount >= _confirmFrames)
            {
                accept = true;
                Reacquires++;
            }
        }

        Pose target;
        if (accept)
        {
            bool reacquire = suspect;
            _accepted = raw;
            _sinceAccepted = 0f;
            _hasCandidate = false;
            target = raw;
            if (reacquire)
            {
                // Start gliding: keep the pose currently shown, the offset decays to 0
                Vector3 f = _posFilter.Step(raw.position, dt);
                Quaternion fr = _rotFilter.Step(raw.rotation, dt);
                _glidePos = _out.position - f;
                _glideRot = _out.rotation * Quaternion.Inverse(fr);
                Output(data, f, fr, dt, alreadyFiltered: true);
                return;
            }
        }
        else
        {
            target = _accepted; // hold the old pose
            HeldFrames++;
        }

        Output(data, target.position, target.rotation, dt, alreadyFiltered: false);
    }

    /// <summary>Stores the raw Quest wrist (diagnostics only, the data is not changed). data.Root is in tracking space ->
    /// converted to world with this frame's own TrackingToWorldTransformer (like Hand.GetRootPose).</summary>
    private void RecordRaw(HandDataAsset data)
    {
        RawValid = data.IsDataValid && data.IsTracked;
        if (!RawValid) return;
        var toWorld = data.Config != null ? data.Config.TrackingToWorldTransformer : null;
        RawWorldRoot = toWorld != null ? toWorld.ToWorldPose(data.Root) : data.Root;
        RawHighConfidence = data.IsHighConfidence;
        RawTime = Time.realtimeSinceStartup;
    }

    private void Output(HandDataAsset data, Vector3 pos, Quaternion rot, float dt, bool alreadyFiltered)
    {
        _posFilter.SetProperties(new OneEuroFilterPropertyBlock(_minCutoff, _beta));
        _rotFilter.SetProperties(new OneEuroFilterPropertyBlock(_minCutoff, _beta));
        Vector3 fp = alreadyFiltered ? pos : _posFilter.Step(pos, dt);
        Quaternion fr = alreadyFiltered ? rot : _rotFilter.Step(rot, dt);

        float decay = Mathf.Exp(-dt / Mathf.Max(_reacquireSeconds, 1e-3f));
        _glidePos *= decay;
        _glideRot = Quaternion.Slerp(Quaternion.identity, _glideRot, decay);

        _out = new Pose(fp + _glidePos, _glideRot * fr);
        data.Root = _out;
        data.RootPoseOrigin = PoseOrigin.FilteredTrackedPose;
        _last.CopyFrom(data);
    }
}
