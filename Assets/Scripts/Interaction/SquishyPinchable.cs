using UnityEngine;

/// <summary>
/// A "squeezable" object: when the virtual thumb and index tips press into its surface, the vertices near the fingertips are
/// pushed in -> a dent; squeezed hard enough from two opposite sides, the object ATTACHES to the hand (follows the midpoint
/// of the two fingertips); when released it springs back to its original shape.
///
/// Works on any object with a MeshFilter (sphere, box, toy...). The mesh needs enough vertices -- Unity's default sphere
/// (515 vertices) is fine, the default cube (24 vertices) dents very coarsely.
///
/// Several hands can interact with one object (_hands list): any hand can dent it; a hand that pinches correctly holds it
/// (only one hand holds at a time, the other can still dent it; when the holding hand lets go the other can catch it).
///
/// Assumes a UNIFORM scale on all three axes (x = y = z) to convert local units <-> metres.
///
/// Nothing extra is needed from FingerUDPReceiver: this script only reads the two virtual fingertips AFTER FingerUDPReceiver has
/// posed the bones this frame (higher DefaultExecutionOrder -> runs after the receiver's LateUpdate).
/// </summary>
[DefaultExecutionOrder(100)]
[RequireComponent(typeof(MeshFilter))]
public class SquishyPinchable : MonoBehaviour
{
    /// <summary>One hand: thumb tip + index tip.</summary>
    [System.Serializable]
    public struct Hand
    {
        [Tooltip("Bare hand: LeftThumbTip (updated by IsdkFingertipProxy). Glove hand: XRHand_ThumbTip.")]
        public Transform thumbTip;
        public Transform indexTip;
        [Tooltip("Optional: this hand's wrist. While held, the object follows the WRIST (tracked by the Quest 72 times/s, very smooth) instead of " +
                 "the midpoint of the two fingertips (glove hand: only ~8 updates/s from the camera, jumping a few mm each time). Empty = follow the fingertips.")]
        public Transform wrist;
    }

    [Header("Hands that can interact")]
    [Tooltip("Every hand in the list can dent and hold the object. Empty = find XRHand_ThumbTip / XRHand_IndexTip in the hand with FingerUDPReceiver.")]
    [SerializeField] private Hand[] _hands;

    // Old layout (one hand per object) -- kept to read old scenes, migrated to _hands automatically.
    [SerializeField, HideInInspector] private Transform _thumbTip;
    [SerializeField, HideInInspector] private Transform _indexTip;

    [Header("Head-attached hand (glove hand) -- measured in the view plane")]
    [Tooltip("Only applies to a HEAD-ATTACHED hand (child of _viewReference, e.g. the glove hand StaticHandModel_Right). " +
             "A bare hand tracked by the Quest has a real 3D position and is always measured in 3D, unaffected.\n\n" +
             "ON: ignore depth when testing whether a fingertip touches the object -- pull both fingertips to the object's DEPTH first.\n\n" +
             "Reason: camera data is 2D only, and with Preserve Rest Depth off each virtual finger lies flat at its base's depth -- the thumb base " +
             "is ~5 cm closer to the eyes than the index base. Seen from the eyes the two tips touch, but in 3D they are still ~5 cm apart in depth, " +
             "so with 3D distances the object never sees itself pinched.\n\n" +
             "OFF now: ImageHandSolver builds a 3D glove hand (with depth) -- with this on the ball would ignore depth and be touchable " +
             "with the hand near or far; the cube (PhysicsPinchGrabbable) always measures in 3D.")]
    [SerializeField] private bool _measureInViewPlane = false;
    [Tooltip("Transform standing for the viewer's eyes. Empty = the hand's parent (CenterEyeAnchor).")]
    [SerializeField] private Transform _viewReference;

    [Header("Compression")]
    [Tooltip("Fingertip radius (m). The hand model's 'fingertip' point is INSIDE the finger, the skin is this far from it -- " +
             "the object starts to dent when the SKIN touches the surface, and FingertipSurfaceConstraint keeps the skin exactly on the surface.")]
    [SerializeField] private float _fingerRadius = 0.008f;
    [Tooltip("MAXIMUM compression, as a fraction of the object's radius (0.25 = at most 1/4 of the radius). Small = stiff, large = soft.")]
    [Range(0.02f, 0.6f)]
    [SerializeField] private float _maxIndent = 0.25f;
    [Tooltip("Width of the dent, as a fraction of the radius. Small = a sharp dent like a pen tip, large = a wide round dent.")]
    [Range(0.1f, 1.5f)]
    [SerializeField] private float _contactSpread = 0.45f;
    [Tooltip("The object bulges at the sides when squeezed (like a real ball keeping its volume). 0 = no bulge.")]
    [Range(0f, 1f)]
    [SerializeField] private float _bulge = 0.35f;
    [Tooltip("Spring stiffness: how quickly the dent follows the fingertip, and how quickly it springs back when released.")]
    [SerializeField] private float _stiffness = 300f;
    [Tooltip("Spring damping. Low = wobbles a few times before settling, high = returns slowly without wobbling.")]
    [SerializeField] private float _damping = 18f;

    [Header("Two-finger squeeze (while held)")]
    [Tooltip("Held with two fingers: two dents SYMMETRIC about the ball centre (two opposing forces), the SAME depth, on one stable PINCH AXIS " +
             "(in the ball's frame, slowly following the hand) -- the virtual thumb + index sit exactly in the two dents (FingertipSurfaceConstraint). " +
             "Off = old behaviour: each dent follows the jittery real fingertip direction -> ball and fingers jumped around when lifted.")]
    [SerializeField] private bool _symmetricPinch = true;
    [Tooltip("How slowly the pinch axis follows the REAL thumb -> index direction (s). Large = more stable, small = follows the hand faster.")]
    [SerializeField] private float _pinchAxisSeconds = 0.3f;
    [Tooltip("How slowly the depth of the two dents follows the squeeze (gap between the real fingertips) (s), to filter glove-hand jitter.")]
    [SerializeField] private float _pinchDepthSeconds = 0.08f;

    [Header("Attaching to the hand")]
    [Tooltip("Grab starts when BOTH fingertips are within this distance (m) of the surface and on opposite sides. Larger = easier to grab, less precision needed.")]
    [SerializeField] private float _grabMargin = 0.015f;
    [Tooltip("Release when the fingertips open wider than the object's diameter plus this (m). Always applies, whichever release rule is chosen.")]
    [SerializeField] private float _releaseMargin = 0.01f;

    public enum ReleaseRule
    {
        WhenFingersOpen,     // release as soon as the fingers start to open (recommended)
        WhenWiderThanObject, // only release when the fingers open wider than the whole object (old behaviour)
    }

    [Tooltip("WhenFingersOpen: release when the fingers OPEN by _releaseOpening more than at the TIGHTEST squeeze -- after Prachyabrued & Borst (IJHCS 2012). " +
             "The real fingers are deep inside the object (nothing stops them), so waiting until they open wider than the object means opening the hand very far; the object 'never lets go'.\n\n" +
             "WhenWiderThanObject: the old rule, kept for comparison.")]
    [SerializeField] private ReleaseRule _releaseRule = ReleaseRule.WhenFingersOpen;
    [Tooltip("With WhenFingersOpen: how far (m) the fingers must open beyond the tightest squeeze to release. Small = releases easily, large = harder to drop but fewer accidental releases from a trembling hand.")]
    [SerializeField] private float _releaseOpening = 0.01f;
    [Tooltip("The release condition must hold CONTINUOUSLY for this long (s). Avoids accidental releases when the finger position jitters / jumps for 1-2 frames " +
             "(the glove hand gets only ~8 points per second from the camera, each update jumping a few mm). 0 = release immediately.")]
    [SerializeField] private float _releaseDelay = 0.12f;
    [Tooltip("While held, how quickly the object slides to the midpoint of the two fingertips.")]
    [SerializeField] private float _followSpeed = 12f;
    [Tooltip("Hand with a 'wrist': the object is fixed to the wrist from the moment of grabbing (like holding a real object), and only slowly slides back to the middle of the two fingertips at this rate " +
             "(1/s) -- correcting offsets gradually without shaking with every fingertip jump.")]
    [SerializeField] private float _recenterSpeed = 2f;
    [Tooltip("If the holding hand is LOST (the Quest drops the hand) for less than this (s), the object stays held -- when the hand reappears it keeps holding. " +
             "Longer losses release it.")]
    [SerializeField] private float _lostGraceSeconds = 0.35f;
    [Tooltip("While held: place the virtual thumb + index tips ON the object's surface (even when the real fingers are away from it), " +
             "so it looks like a real pinch. Done by FingertipSurfaceConstraint.")]
    [SerializeField] private bool _snapHeldFingertips = true;
    [Tooltip("Fingertips further than this (m) from the surface are no longer pulled onto it.")]
    [SerializeField] private float _snapMaxGap = 0.03f;

    [Header("On release")]
    [Tooltip("Where the object returns when no longer held. Empty = it stays where it was released.")]
    [SerializeField] private Transform _home;
    [SerializeField] private float _returnSpeed = 6f;

    /// <summary>All enabled squeezable objects in the scene, so FingertipSurfaceConstraint knows which objects to check.</summary>
    public static readonly System.Collections.Generic.List<SquishyPinchable> Active =
        new System.Collections.Generic.List<SquishyPinchable>();

    /// <summary>Whether the object is held (attached to a hand).</summary>
    public bool IsHeld => _heldBy >= 0;

    /// <summary>Current compression 0..1 (1 = maximum compression).</summary>
    public float Squish01 { get; private set; }

    /// <summary>How far (m) the REAL finger presses BEYOND the maximum compression -- the part of the real finger inside the object
    /// that the user cannot see (the virtual finger is held on the surface). 0 = not beyond.</summary>
    public float OvershootMeters { get; private set; }

    private Mesh _mesh;
    private Vector3[] _baseVertices;
    private Vector3[] _deformed;
    private Vector3 _center;  // object centre in local space
    private float _radius;    // mean radius in local space
    private float _maxRadius; // farthest vertex from the centre (local) -- quickly rejects far points when checking fingers

    // Meshes usually have "seams": one point stored as several identical vertices (for texture mapping). RecalculateNormals works
    // per vertex, so when the surface dents the two sides of a seam shade differently -> a visible crack. The groups of identical
    // vertices are stored to average their normals.
    private int[][] _seamGroups;
    private Vector3[] _normals;

    // Each fingertip is a "press point": press direction, current depth, spring velocity.
    // Hand h has two points: 2h (thumb) and 2h+1 (index).
    private Vector3[] _pressDir = new Vector3[0];
    private float[] _pressSurface = new float[0];
    private float[] _indent = new float[0];
    private float[] _indentVel = new float[0];
    private bool _meshDirty;

    // Symmetric pinch while held: pinch axis thumb -> index (local), filtered dent depth (local),
    // offset of the ball centre along the pinch axis from the middle of the real fingertips (m, + = towards the index; see SetPinchShift)
    private Vector3 _pinchAxis = Vector3.right;
    private float _pinchDepth;
    private float _pinchShift;

    private int _heldBy = -1; // hand holding the object (-1 = nobody)
    private Vector3 _grabAxis;
    private Quaternion _grabRotation;
    private float[] _tightestGap = new float[0]; // per hand: smallest fingertip gap since the grab started
    private float _releaseTimer;                 // how long the release condition has held (s)
    private float _lostTimer;                    // how long the holding hand has been lost (s)
    private Vector3 _grabLocalPos;               // object position in the wrist frame (when the hand has a wrist)
    private Quaternion _grabLocalRot;
    private bool[] _regrabBlocked = new bool[0];

    // Buffers reused every frame (no new arrays)
    private Vector3[] _points = new Vector3[0];
    private Vector3[] _rawPoints = new Vector3[0]; // real 3D fingertip positions (not projected into the view plane)
    private bool[] _handValid = new bool[0];

    private void Awake()
    {
        MigrateLegacyHand();
        if (_hands.Length == 0)
        {
            Transform thumb = FindInScene("XRHand_ThumbTip");
            Transform index = FindInScene("XRHand_IndexTip");
            if (thumb != null && index != null) _hands = new[] { new Hand { thumbTip = thumb, indexTip = index } };
        }
        // Glove hand (XRHand_*): the wrist is the fingertip's "...Wrist" ancestor -- filled in automatically if empty.
        for (int h = 0; h < _hands.Length; h++)
        {
            if (_hands[h].wrist != null || _hands[h].thumbTip == null) continue;
            Transform t = _hands[h].thumbTip.parent;
            while (t != null && !t.name.Contains("Wrist")) t = t.parent;
            _hands[h].wrist = t;
        }
        if (_viewReference == null)
        {
            var receiver = FindAnyObjectByType<FingerUDPReceiver>();
            if (receiver != null) _viewReference = receiver.transform.parent;
        }
        EnsureInitialized();
    }

    private void OnValidate() => MigrateLegacyHand();

    /// <summary>Old scenes only have one _thumbTip/_indexTip pair -> turned into the first hand.</summary>
    private void MigrateLegacyHand()
    {
        if (_hands == null) _hands = new Hand[0];
        if (_hands.Length == 0 && (_thumbTip != null || _indexTip != null))
        {
            _hands = new[] { new Hand { thumbTip = _thumbTip, indexTip = _indexTip } };
        }
        _thumbTip = null;
        _indexTip = null;
    }

    /// <summary>Allocates per-hand state (changing the number of hands in the Inspector at run time is fine).</summary>
    private void EnsureHandState()
    {
        if (_hands == null) _hands = new Hand[0];
        int hands = _hands.Length;
        if (_handValid.Length == hands) return;

        int points = hands * 2;
        _pressDir = new Vector3[points];
        for (int k = 0; k < points; k++) _pressDir[k] = Vector3.up;
        _pressSurface = new float[points];
        _indent = new float[points];
        _indentVel = new float[points];
        _points = new Vector3[points];
        _rawPoints = new Vector3[points];
        _tightestGap = new float[hands];
        _regrabBlocked = new bool[hands];
        _handValid = new bool[hands];
        _heldBy = -1;
    }

    private void OnEnable() => Active.Add(this);
    private void OnDisable() => Active.Remove(this);

    /// <summary>Makes a private copy of the mesh (so the original mesh shared with other objects is not modified) and stores
    /// the original shape.</summary>
    public void EnsureInitialized()
    {
        if (_baseVertices != null && _baseVertices.Length > 0) // after an Editor recompile a null array becomes an empty array
        {
            // The Editor keeps _baseVertices across recompiles but newly added fields are 0
            if (_maxRadius <= 0f)
                foreach (var v in _baseVertices) _maxRadius = Mathf.Max(_maxRadius, (v - _center).magnitude);
            if (_seamGroups == null) BuildSeamGroups(); // jagged arrays are not kept across recompiles
            if (_deformed == null || _deformed.Length != _baseVertices.Length) _deformed = new Vector3[_baseVertices.Length];
            return;
        }

        var filter = GetComponent<MeshFilter>();
        _mesh = Instantiate(filter.sharedMesh);
        _mesh.name = filter.sharedMesh.name + " (squishy)";
        _mesh.MarkDynamic();
        filter.sharedMesh = _mesh;

        _baseVertices = _mesh.vertices;
        _deformed = new Vector3[_baseVertices.Length];
        _center = _mesh.bounds.center;

        float sum = 0f;
        _maxRadius = 0f;
        foreach (var v in _baseVertices)
        {
            float d = (v - _center).magnitude;
            sum += d;
            _maxRadius = Mathf.Max(_maxRadius, d);
        }
        _radius = Mathf.Max(sum / Mathf.Max(_baseVertices.Length, 1), 1e-5f);

        BuildSeamGroups();
        EnsureHandState();
    }

    private void BuildSeamGroups()
    {
        var byPosition = new System.Collections.Generic.Dictionary<Vector3, System.Collections.Generic.List<int>>();
        for (int i = 0; i < _baseVertices.Length; i++)
        {
            // Rounded so that two "identical" vertices differing by float error still group together
            Vector3 key = new Vector3(
                Mathf.Round(_baseVertices[i].x * 1e5f),
                Mathf.Round(_baseVertices[i].y * 1e5f),
                Mathf.Round(_baseVertices[i].z * 1e5f));
            if (!byPosition.TryGetValue(key, out var list))
            {
                list = new System.Collections.Generic.List<int>();
                byPosition[key] = list;
            }
            list.Add(i);
        }

        var groups = new System.Collections.Generic.List<int[]>();
        foreach (var list in byPosition.Values)
        {
            if (list.Count > 1) groups.Add(list.ToArray());
        }
        _seamGroups = groups.ToArray();
    }

    private void RecalculateSmoothNormals()
    {
        _mesh.RecalculateNormals();
        if (_seamGroups.Length == 0) return;

        _normals = _mesh.normals;
        foreach (var group in _seamGroups)
        {
            Vector3 avg = Vector3.zero;
            foreach (int i in group) avg += _normals[i];
            avg.Normalize();
            foreach (int i in group) _normals[i] = avg;
        }
        _mesh.normals = _normals;
    }

    /// <summary>Total time (ms) all squeezable objects spent in Tick in the PREVIOUS frame, written to glove_diag.</summary>
    public static float FrameMs { get; private set; }
    private static int s_msFrame = -1;
    private static float s_msAcc;

    private void LateUpdate()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Tick(Time.deltaTime);
        float ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000f / System.Diagnostics.Stopwatch.Frequency;
        if (Time.frameCount != s_msFrame) { FrameMs = s_msAcc; s_msAcc = 0f; s_msFrame = Time.frameCount; }
        s_msAcc += ms;
    }

    /// <summary>Reads every hand's fingertips and runs one step. Public so it can run outside Play mode.</summary>
    public void Tick(float dt)
    {
        EnsureInitialized();
        EnsureHandState();

        for (int h = 0; h < _hands.Length; h++)
        {
            Transform thumb = _hands[h].thumbTip;
            Transform index = _hands[h].indexTip;
            // Hand lost (the Quest deactivated the two transforms) -> this hand counts as open:
            // no denting, and a held object is released.
            _handValid[h] = thumb != null && index != null &&
                            thumb.gameObject.activeInHierarchy && index.gameObject.activeInHierarchy;
            if (!_handValid[h]) continue;
            _points[2 * h] = MeasurePoint(thumb);
            _points[2 * h + 1] = MeasurePoint(index);
            _rawPoints[2 * h] = thumb.position;
            _rawPoints[2 * h + 1] = index.position;
        }

        StepHands(dt);
    }

    /// <summary>Head-attached hand: no real depth -> measured in the view plane.</summary>
    private bool IsHeadLocked(Transform tip) =>
        _measureInViewPlane && _viewReference != null && tip.IsChildOf(_viewReference);

    /// <summary>Fingertip position used for computing: bare hand = the real 3D position,
    /// head-attached hand = pulled to the object's depth (see _measureInViewPlane).</summary>
    private Vector3 MeasurePoint(Transform tip) =>
        IsHeadLocked(tip) ? ToObjectDepth(tip.position) : tip.position;

    /// <summary>Slides a point along the VIEW RAY from the eyes until it is at the object centre's depth -- seen from the eyes the
    /// point does not move at all. It must slide along the ray (not parallel to the view direction) because the hand is only tens of
    /// cm from the eyes and perspective is strong: sliding in parallel would visibly move the point on screen.</summary>
    private Vector3 ToObjectDepth(Vector3 worldPoint)
    {
        Vector3 eye = _viewReference.position;
        Vector3 forward = _viewReference.forward;
        float pointDepth = Vector3.Dot(worldPoint - eye, forward);
        float objectDepth = Vector3.Dot(transform.TransformPoint(_center) - eye, forward);
        if (pointDepth < 1e-4f || objectDepth < 1e-4f) return worldPoint;
        return eye + (worldPoint - eye) * (objectDepth / pointDepth);
    }

    // --- For experiments ---------------------------------------------------------

    /// <summary>Maximum compression, as a fraction of the radius (e.g. 0.25).</summary>
    public float MaxIndentFraction
    {
        get => _maxIndent;
        set => _maxIndent = Mathf.Clamp(value, 0.02f, 0.6f);
    }

    /// <summary>Resets the object: not held, no dents, at _home (if set). Used at the start of every experiment trial.</summary>
    public void ResetState()
    {
        EnsureHandState();
        _heldBy = -1;
        _releaseTimer = 0f;
        _lostTimer = 0f;
        for (int h = 0; h < _regrabBlocked.Length; h++) _regrabBlocked[h] = false;
        for (int k = 0; k < _indent.Length; k++)
        {
            _indent[k] = 0f;
            _indentVel[k] = 0f;
        }
        _pinchDepth = 0f;
        _pinchShift = 0f;
        Squish01 = 0f;
        OvershootMeters = 0f;
        if (_home != null) transform.SetPositionAndRotation(_home.position, _home.rotation);
        if (_mesh != null) ApplyDeformation();
    }

    // --- For the haptic layer ----------------------------------------------------

    /// <summary>Maximum compression (m), for the haptic layer to normalise force.</summary>
    public float MaxIndentMeters => _maxIndent * _radius * transform.lossyScale.x;

    /// <summary>How deep (m) the fingertip skin (REAL position, before FingertipSurfaceConstraint pushes it out) is into the object's
    /// ORIGINAL surface. Positive = pressing in, negative = still away from the surface. This is exactly the gap between the "real finger"
    /// and the "proxy finger on the surface" in the god-object method -> the force.</summary>
    public bool TryGetSkinDepth(Transform tip, out float depthMeters)
    {
        depthMeters = float.NegativeInfinity;
        if (_baseVertices == null || tip == null) return false;

        depthMeters = -SurfaceDistance(MeasurePoint(tip), transform.lossyScale.x);
        return true;
    }

    /// <summary>Surface normal (world, pointing OUT of the object) where the fingertip touches -- the direction the object pushes back
    /// on the finger. Approximated by the direction from the object centre to the fingertip (exact for round objects like a ball).</summary>
    public Vector3 ContactNormal(Transform tip)
    {
        if (tip == null) return Vector3.up;
        Vector3 d = MeasurePoint(tip) - transform.TransformPoint(_center);
        return d.sqrMagnitude > 1e-12f ? d.normalized : Vector3.up;
    }

    /// <summary>Whether the object is held by the very hand this fingertip belongs to.
    /// A fingertip not belonging to any hand in _hands -> only requires that the object is held.</summary>
    public bool IsHeldBy(Transform tip)
    {
        if (_heldBy < 0 || _hands == null) return false;
        for (int h = 0; h < _hands.Length; h++)
        {
            if (_hands[h].thumbTip == tip || _hands[h].indexTip == tip) return h == _heldBy;
        }
        return true;
    }

    /// <summary>Whether this fingertip belongs to the hand HOLDING the object (an unknown hand -> false, unlike IsHeldBy).</summary>
    public bool IsHoldingTip(Transform tip)
    {
        if (_heldBy < 0 || tip == null || _hands == null || _heldBy >= _hands.Length) return false;
        return _hands[_heldBy].thumbTip == tip || _hands[_heldBy].indexTip == tip;
    }

    /// <summary>While held: the point on the CURRENT (dented) surface where the drawn fingertip should be -- both when the real finger is
    /// inside AND when it is away from the surface (up to _snapMaxGap). Computed in real 3D so both eyes see the finger touch the object.
    /// The real finger position (the data) is unchanged -> the squeeze level / force stays correct.</summary>
    /// <param name="handTip">The fingertip transform the object reads (in _hands).</param>
    /// <param name="visualTipWorld">Position of the DRAWN hand's fingertip (may be a different skeleton, e.g. the bare hand).</param>
    public bool TryGetHeldContact(Transform handTip, Vector3 visualTipWorld, out Vector3 contactWorld)
    {
        contactWorld = visualTipWorld;
        if (!_snapHeldFingertips || _baseVertices == null || !IsHoldingTip(handTip)) return false;

        float scale = transform.lossyScale.x;
        Vector3 local = transform.InverseTransformPoint(visualTipWorld) - _center;
        float dist = local.magnitude;
        if (dist < 1e-6f) return false;
        Vector3 dir = local / dist;
        float onSurface = CurrentSurfaceRadius(dir) + _fingerRadius / scale;
        if ((dist - onSurface) * scale > _snapMaxGap) return false; // too far away -> about to release, leave it
        contactWorld = transform.TransformPoint(_center + dir * onSurface);
        return true;
    }

    // --- Keeping fingers from going through the object ---------------------------

    /// <summary>Checks whether the fingertip skin is INSIDE the current surface (including dents). If so, returns the new fingertip
    /// position where the skin just touches the surface -- FingertipSurfaceConstraint bends the virtual finger there.</summary>
    public bool TryResolvePenetration(Transform tip, out Vector3 resolvedWorld)
    {
        resolvedWorld = tip != null ? tip.position : Vector3.zero;
        if (_baseVertices == null || tip == null) return false;
        Vector3 tipWorld = tip.position;

        // Glove hand measured in the view plane: move the fingertip to the object's depth, compute there,
        // then move it back to the finger's original depth.
        bool projected = IsHeadLocked(tip);
        Vector3 eye = Vector3.zero;
        float depthRatio = 1f;
        Vector3 p = tipWorld;
        if (projected)
        {
            eye = _viewReference.position;
            Vector3 forward = _viewReference.forward;
            float pointDepth = Vector3.Dot(tipWorld - eye, forward);
            float objectDepth = Vector3.Dot(transform.TransformPoint(_center) - eye, forward);
            if (pointDepth < 1e-4f || objectDepth < 1e-4f) return false;
            depthRatio = objectDepth / pointDepth;
            p = eye + (tipWorld - eye) * depthRatio;
        }

        Vector3 local = transform.InverseTransformPoint(p) - _center;
        float dist = local.magnitude;
        if (dist < 1e-6f) return false;
        Vector3 dir = local / dist;

        float minDist = CurrentSurfaceRadius(dir) + _fingerRadius * depthRatio / transform.lossyScale.x;
        if (dist >= minDist) return false;

        resolvedWorld = transform.TransformPoint(_center + dir * minDist);
        if (projected) resolvedWorld = eye + (resolvedWorld - eye) / depthRatio;
        return true;
    }

    /// <summary>The hand of this fingertip is measured in the view plane (head-attached) -> FingertipSurfaceConstraint only checks the
    /// fingertip as before (TryResolvePenetration / TryGetHeldContact), not the whole finger.</summary>
    public bool MeasuresInViewPlane(Transform tip) => tip != null && IsHeadLocked(tip);

    /// <summary>Object centre (world).</summary>
    public Vector3 CenterWorld => transform.TransformPoint(_center);

    /// <summary>Held by this fingertip and the virtual fingers may be placed on the surface (_snapHeldFingertips).</summary>
    public bool SnapsHeldTip(Transform handTip) => _snapHeldFingertips && _baseVertices != null && IsHoldingTip(handTip);

    /// <summary>Symmetric pinch (_symmetricPinch) with this fingertip: the point on the finger AXIS (finger thickness fingerRadius m) that puts the
    /// pad exactly at the bottom of its dent -- the thumb on the -pinch axis side, the index on the +pinch axis side, opposite each other through the centre.
    /// grip = the pinch axis (world, thumb -> index).</summary>
    public bool TryGetPinchTarget(Transform handTip, float fingerRadius, out Vector3 target, out Vector3 grip)
    {
        target = grip = default;
        if (!_symmetricPinch || !SnapsHeldTip(handTip)) return false;
        Vector3 dir = _hands[_heldBy].thumbTip == handTip ? -_pinchAxis : _pinchAxis;
        float scale = transform.lossyScale.x;
        target = transform.TransformPoint(_center + dir * (CurrentSurfaceRadius(dir) + fingerRadius / scale));
        grip = transform.TransformDirection(_pinchAxis).normalized;
        return true;
    }

    /// <summary>Symmetric pinch: offset of the ball centre along the pinch axis (m, + = towards the index) from the middle of the real fingertips.</summary>
    public float PinchShift => _pinchShift;

    /// <summary>Sets the offset of the ball centre along the pinch axis (m, + = towards the index). FingertipSurfaceConstraint calls it when a virtual
    /// finger has to reach beyond its comfortable range: with the glove there is no real ball, so the real finger gap (e.g. 58 mm) is narrower than the
    /// virtual ball + finger thickness (76 mm) -- with the ball in the middle the index must stretch fully (pad 65 mm from the base joint = full length) to
    /// touch, giving a hooked finger. Moving the ball towards the thumb (which reaches further) lets both fingers curve naturally.</summary>
    public void SetPinchShift(float meters)
    {
        if (!_symmetricPinch || _heldBy < 0) return;
        float delta = meters - _pinchShift;
        if (Mathf.Abs(delta) < 1e-7f) return;
        _pinchShift = meters;
        Vector3 axisW = transform.TransformDirection(_pinchAxis).normalized;
        Transform w = _hands[_heldBy].wrist;
        if (w != null && w.gameObject.activeInHierarchy) _grabLocalPos += w.InverseTransformVector(axisW * delta);
        transform.position += axisW * delta;
    }

    /// <summary>Maximum gap (m) between a virtual finger and the surface for which it is still pulled onto the surface while held.</summary>
    public float SnapMaxGap => _snapMaxGap;

    /// <summary>Signed distance (m, real 3D) from the finger SURFACE -- finger axis through `point`, finger thickness `radius` -- to the
    /// CURRENT (dented) surface. Negative = inside. onSurface = finger-axis position where the finger surface just touches the object.
    /// Points far from the object: returns a positive estimate without computing the surface (fast).</summary>
    public float SkinGap(Vector3 point, float radius, out Vector3 onSurface)
    {
        onSurface = point;
        if (_baseVertices == null || _baseVertices.Length == 0) return float.PositiveInfinity;
        if (_maxRadius <= 0f) EnsureInitialized();
        float scale = transform.lossyScale.x;
        Vector3 local = transform.InverseTransformPoint(point) - _center;
        float dist = local.magnitude;
        float far = (dist - _maxRadius) * scale - radius;
        if (far > 0.03f) return far;
        if (dist < 1e-6f) return -_radius * scale;
        Vector3 dir = local / dist;
        float minDist = CurrentSurfaceRadius(dir) + radius / scale;
        onSurface = transform.TransformPoint(_center + dir * minDist);
        return (dist - minDist) * scale;
    }

    private static float Tanh(float x)
    {
        float e = Mathf.Exp(-2f * Mathf.Abs(x));
        return Mathf.Sign(x) * (1f - e) / (1f + e);
    }

    /// <summary>How far the CURRENT (dented) surface is from the centre in this direction -- the same formula as ApplyDeformation,
    /// computed for one direction only.</summary>
    private float CurrentSurfaceRadius(Vector3 dir)
    {
        float surface = SupportRadius(dir);
        Vector3 p = _center + dir * surface;
        float sigma = _contactSpread * _radius;
        float inv2Sigma2 = 1f / (2f * sigma * sigma);
        float r = surface;

        for (int k = 0; k < _indent.Length; k++)
        {
            if (Mathf.Abs(_indent[k]) < 1e-6f) continue;
            Vector3 contact = _center + _pressDir[k] * _pressSurface[k];
            float w = Mathf.Exp(-(p - contact).sqrMagnitude * inv2Sigma2);
            r -= _indent[k] * w * Mathf.Max(0f, Vector3.Dot(dir, _pressDir[k]));
        }
        return r;
    }

    /// <summary>One simulation step with the FIRST hand's two fingertips (world coordinates, not projected into the view plane);
    /// other hands count as open. Public so it can run outside Play mode.</summary>
    public void Step(Vector3 thumbWorld, Vector3 indexWorld, float dt)
    {
        EnsureInitialized();
        if (_hands.Length == 0)
        {
            _hands = new Hand[1];
            EnsureHandState();
        }
        for (int h = 0; h < _handValid.Length; h++) _handValid[h] = h == 0;
        _points[0] = _rawPoints[0] = thumbWorld;
        _points[1] = _rawPoints[1] = indexWorld;
        StepHands(dt);
    }

    /// <summary>One simulation step with _points / _handValid already filled in.</summary>
    private void StepHands(float dt)
    {
        if (dt <= 0f) return;

        UpdateGrab(dt);
        UpdateIndents(dt);
        ApplyDeformation();
    }

    // --- Attaching to the hand ---------------------------------------------------

    private void UpdateGrab(float dt)
    {
        float scale = transform.lossyScale.x;
        Vector3 centerWorld = transform.TransformPoint(_center);

        // 1. Is the holding hand still holding?
        if (_heldBy >= 0)
        {
            int h = _heldBy;
            // Hand lost: keep the object for _lostGraceSeconds (the hand is often lost briefly when turning / occluded),
            // release only after longer.
            _lostTimer = _handValid[h] ? 0f : _lostTimer + dt;
            bool release = _lostTimer > _lostGraceSeconds;
            if (_handValid[h])
            {
                float gap = Vector3.Distance(_points[2 * h], _points[2 * h + 1]);
                _tightestGap[h] = Mathf.Min(_tightestGap[h], gap);

                // The fingertips opened wider than the object's diameter -> they can no longer pinch it.
                // The RELEASE threshold must be wider than the GRAB threshold (_grabMargin): otherwise a finger that just entered the
                // grab zone would already be outside the release zone -> grab and immediately release.
                bool widerThanObject = gap > 2f * (_radius * scale + _fingerRadius + _grabMargin + _releaseMargin);
                // The fingers started to open compared with the tightest squeeze -> the user wants to release.
                // The "tightest" reference must not be smaller than the gap at MAXIMUM compression: the real fingers squeeze deep into the
                // object (measured: the two tips 2-4 cm apart while a 6 cm object only compresses ~1.5 cm); with that reference relaxing the
                // hand by 1 cm -- still deep inside the object -- would already drop it.
                float squeezedGap = 2f * (_radius * scale + _fingerRadius - MaxIndentMeters);
                float tightest = Mathf.Max(_tightestGap[h], squeezedGap);
                bool opening = _releaseRule == ReleaseRule.WhenFingersOpen && gap > tightest + _releaseOpening;
                release = widerThanObject || opening;
                // The opening must last a little before releasing (see _releaseDelay).
                _releaseTimer = release ? _releaseTimer + dt : 0f;
                release = _releaseTimer >= _releaseDelay;
            }

            if (release)
            {
                _heldBy = -1;
                _releaseTimer = 0f;
                _lostTimer = 0f;
                _regrabBlocked[h] = _handValid[h];
            }
        }

        // 2. A hand pinching correctly grabs the object (if nobody holds it)
        for (int h = 0; h < _handValid.Length; h++)
        {
            if (!_handValid[h])
            {
                _regrabBlocked[h] = false;
                continue;
            }

            Vector3 thumb = _points[2 * h];
            Vector3 index = _points[2 * h + 1];

            // Grab starts: both fingertips close to the surface AND on opposite sides
            // (two fingers on the same side is just touching, not grabbing).
            bool opposite = Vector3.Dot(thumb - centerWorld, index - centerWorld) < 0f;
            bool canGrab = opposite &&
                           SurfaceDistance(thumb, scale) < _grabMargin &&
                           SurfaceDistance(index, scale) < _grabMargin;

            // Right after releasing, the two fingers are still close to the surface -> without this block the next frame would grab
            // again at once. Only allow a new grab once the fingers have LEFT the grab zone at least once.
            if (_regrabBlocked[h])
            {
                if (!canGrab) _regrabBlocked[h] = false;
            }
            else if (canGrab && _heldBy < 0)
            {
                _heldBy = h;
                _releaseTimer = 0f;
                _grabAxis = (index - thumb).normalized;
                _grabRotation = transform.rotation;
                Vector3 axisLocal = transform.InverseTransformDirection(index - thumb);
                if (axisLocal.sqrMagnitude > 1e-10f) _pinchAxis = axisLocal.normalized;
                _pinchDepth = 0f;
                _pinchShift = 0f;
                _tightestGap[h] = Vector3.Distance(thumb, index);
                Transform wrist = _hands[h].wrist;
                if (wrist != null)
                {
                    _grabLocalPos = wrist.InverseTransformPoint(transform.position);
                    _grabLocalRot = Quaternion.Inverse(wrist.rotation) * transform.rotation;
                }
            }
        }

        float follow = 1f - Mathf.Exp(-_followSpeed * dt);
        Transform heldWrist = _heldBy >= 0 ? _hands[_heldBy].wrist : null;
        if (heldWrist != null)
        {
            // Fixed to the wrist like a real held object: moves / rotates exactly with the wrist, no lag, no shaking with the fingertips.
            // If the hand is lost (the Quest deactivates the wrist) the object stays put until the hand reappears.
            if (_handValid[_heldBy] && heldWrist.gameObject.activeInHierarchy)
            {
                // Slowly slide back to the middle of the two fingertips (corrects offsets, e.g. after changing grip). Uses the REAL 3D
                // positions (also for the glove hand) to correct depth offsets too; sliding slowly averages out the glove hand's depth noise.
                Vector3 mid = (_rawPoints[2 * _heldBy] + _rawPoints[2 * _heldBy + 1]) * 0.5f;
                if (_symmetricPinch) mid += transform.TransformDirection(_pinchAxis).normalized * _pinchShift;
                Vector3 wanted = heldWrist.InverseTransformPoint(mid + (transform.position - centerWorld));
                _grabLocalPos = Vector3.Lerp(_grabLocalPos, wanted, 1f - Mathf.Exp(-_recenterSpeed * dt));
                transform.SetPositionAndRotation(heldWrist.TransformPoint(_grabLocalPos), heldWrist.rotation * _grabLocalRot);
            }
        }
        else if (_heldBy >= 0 && _handValid[_heldBy])
        {
            // The object slides to the midpoint of the two fingertips and rotates with the line between them
            // (like a really pinched object turning with the hand).
            Vector3 thumb = _points[2 * _heldBy];
            Vector3 index = _points[2 * _heldBy + 1];
            Vector3 mid = (thumb + index) * 0.5f;
            Vector3 offset = transform.position - centerWorld; // the mesh centre may be offset from the pivot
            transform.position = Vector3.Lerp(transform.position, mid + offset, follow);

            Vector3 axis = index - thumb;
            if (axis.sqrMagnitude > 1e-10f)
            {
                transform.rotation = Quaternion.FromToRotation(_grabAxis, axis.normalized) * _grabRotation;
            }
        }
        else if (_heldBy < 0 && _home != null) // held while the hand is briefly lost -> stays put, does not return home
        {
            float back = 1f - Mathf.Exp(-_returnSpeed * dt);
            transform.position = Vector3.Lerp(transform.position, _home.position, back);
            transform.rotation = Quaternion.Slerp(transform.rotation, _home.rotation, back);
        }
    }

    /// <summary>Distance (m) from the fingertip SKIN to the object's surface, negative = the skin is inside.</summary>
    private float SurfaceDistance(Vector3 worldPoint, float scale)
    {
        Vector3 local = transform.InverseTransformPoint(worldPoint) - _center;
        float dist = local.magnitude;
        if (dist < 1e-6f) return -_radius * scale;
        return (dist - SupportRadius(local / dist)) * scale - _fingerRadius;
    }

    // --- Compression -------------------------------------------------------------

    private void UpdateIndents(float dt)
    {
        float maxIndent = _maxIndent * _radius;
        float fingerRadius = _fingerRadius / transform.lossyScale.x;
        float overshoot = 0f;
        float deepest = 0f;
        // The spring is always damped close to critical (ratio >= 0.8): 18 with stiffness 300 (ratio 0.52) made the dents bounce a few
        // times with every fingertip jump -> looked jittery.
        float damping = Mathf.Max(_damping, 1.6f * Mathf.Sqrt(Mathf.Max(_stiffness, 0f)));

        // Held with two fingers: this hand's two dents are SYMMETRIC on the pinch axis (thumb on -axis, index on +axis), with the same
        // depth -- two opposing forces through the ball centre. Axis and depth are both filtered slowly: they do not chase the jittery real fingertips.
        bool pinching = _symmetricPinch && _heldBy >= 0 && _heldBy < _handValid.Length && _handValid[_heldBy];
        if (pinching)
        {
            Vector3 a = transform.InverseTransformPoint(_points[2 * _heldBy + 1]) - transform.InverseTransformPoint(_points[2 * _heldBy]);
            float gap = a.magnitude;
            if (gap > 1e-6f)
                _pinchAxis = Vector3.Slerp(_pinchAxis, a / gap, 1f - Mathf.Exp(-dt / Mathf.Max(_pinchAxisSeconds, 1e-3f))).normalized;
            // How far each real finger presses in compared with just touching both faces (the glove has nothing to stop it -> often very deep);
            // tanh: the dent grows then saturates at the maximum (the harder the squeeze the stiffer the ball), without a sudden stop.
            float press = Mathf.Max(0f, 2f * (_radius + fingerRadius) - gap) * 0.5f;
            float depth = maxIndent * Tanh(press / Mathf.Max(maxIndent, 1e-6f));
            _pinchDepth = Mathf.Lerp(_pinchDepth, depth, 1f - Mathf.Exp(-dt / Mathf.Max(_pinchDepthSeconds, 1e-3f)));
            overshoot = Mathf.Max(overshoot, press - maxIndent);
        }

        for (int k = 0; k < _indent.Length; k++)
        {
            float target = 0f;
            Vector3 local = transform.InverseTransformPoint(_points[k]) - _center;
            float dist = local.magnitude;

            if (pinching && k / 2 == _heldBy)
            {
                Vector3 dir = k % 2 == 0 ? -_pinchAxis : _pinchAxis;
                _pressDir[k] = dir;
                _pressSurface[k] = SupportRadius(dir);
                target = _pinchDepth;
            }
            else if (_handValid[k / 2] && dist > 1e-6f)
            {
                Vector3 dir = local / dist;
                float surface = SupportRadius(dir);
                float pressDepth = surface - (dist - fingerRadius);
                target = Mathf.Clamp(pressDepth, 0f, maxIndent);
                overshoot = Mathf.Max(overshoot, pressDepth - maxIndent);

                // Only update the press direction while the fingertip IS pressing; on release keep the old direction so the dent
                // springs back in the right place.
                if (target > 0f)
                {
                    _pressDir[k] = dir;
                    _pressSurface[k] = surface;
                }
            }

            // Damped spring: the dent follows the target and springs back on release.
            _indentVel[k] = (_indentVel[k] + dt * _stiffness * (target - _indent[k])) / (1f + dt * damping);
            _indent[k] += _indentVel[k] * dt;
            _indent[k] = Mathf.Clamp(_indent[k], -0.3f * maxIndent, maxIndent);
            deepest = Mathf.Max(deepest, _indent[k]);
        }

        Squish01 = Mathf.Clamp01(deepest / Mathf.Max(maxIndent, 1e-6f));
        OvershootMeters = Mathf.Max(0f, overshoot) * transform.lossyScale.x;
    }

    /// <summary>How far the object's surface is from the centre in this direction. Exact for convex objects
    /// (sphere, box...); an approximation for concave ones.</summary>
    private float SupportRadius(Vector3 dir)
    {
        float best = 0f;
        for (int i = 0; i < _baseVertices.Length; i++)
        {
            float d = Vector3.Dot(_baseVertices[i] - _center, dir);
            if (d > best) best = d;
        }
        return best;
    }

    private void ApplyDeformation()
    {
        bool active = false;
        foreach (float indent in _indent) active |= Mathf.Abs(indent) > 1e-6f;
        if (!active)
        {
            // Back to the original shape -- write the mesh ONCE and stop, no computation every frame while nobody touches it.
            if (_meshDirty)
            {
                _mesh.vertices = _baseVertices;
                RecalculateSmoothNormals();
                _mesh.RecalculateBounds();
                _meshDirty = false;
            }
            return;
        }

        float sigma = _contactSpread * _radius;
        float inv2Sigma2 = 1f / (2f * sigma * sigma);

        for (int i = 0; i < _baseVertices.Length; i++)
        {
            Vector3 p = _baseVertices[i];
            Vector3 radial = (p - _center).normalized;
            Vector3 offset = Vector3.zero;

            for (int k = 0; k < _indent.Length; k++)
            {
                if (Mathf.Abs(_indent[k]) < 1e-6f) continue;

                // Vertices closer to the press point are pushed in more (bell curve), fading to 0 further away -> a round, soft dent.
                Vector3 contact = _center + _pressDir[k] * _pressSurface[k];
                float w = Mathf.Exp(-(p - contact).sqrMagnitude * inv2Sigma2);
                offset -= _pressDir[k] * (_indent[k] * w);

                // Bulge in the region perpendicular to the press direction (around the object's "belly").
                float side = 1f - Mathf.Abs(Vector3.Dot(radial, _pressDir[k]));
                offset += radial * (0.5f * _bulge * _indent[k] * side * side);
            }

            _deformed[i] = p + offset;
        }

        _mesh.vertices = _deformed;
        RecalculateSmoothNormals();
        _mesh.RecalculateBounds();
        _meshDirty = true;
    }

    /// <summary>Searches only the hand driven by FingerUDPReceiver, so the fingertips of another hand in the scene
    /// (e.g. the left hand) are not picked up by mistake.</summary>
    private static Transform FindInScene(string name)
    {
        var receiver = FindAnyObjectByType<FingerUDPReceiver>();
        return receiver != null ? FindDeepChild(receiver.transform, name) : null;
    }

    private static Transform FindDeepChild(Transform parent, string name)
    {
        foreach (Transform child in parent)
        {
            if (child.name == name) return child;
            Transform result = FindDeepChild(child, name);
            if (result != null) return result;
        }
        return null;
    }
}
