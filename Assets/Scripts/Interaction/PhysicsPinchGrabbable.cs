using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A RIGID object driven by real Unity physics (Rigidbody): grabbed with a PINCH (thumb and index tips on two
/// sides of the object); when released it FALLS under gravity, bounces, rolls, and can be THROWN (it keeps the
/// hand's velocity at release).
///
/// Unlike SquishyPinchable (soft ball, computes its own compression and force, no physics), this object does not
/// deform. Grab and release work like the ball so both feel the same:
///   - grab: BOTH fingertips close to the surface (_grabMargin) and on opposite sides
///   - release: the fingers open _releaseOpening wider than at grab time, for _releaseDelay seconds in a row
///     (avoids accidental releases when the glove fingertips jump)
///   - while held: the object follows the WRIST (Quest 72 Hz, smooth) if the hand has one
///
/// While held the Rigidbody is kinematic (physics cannot pull it out of the hand) but it still pushes other
/// objects; on release physics is turned back on and the hand's velocity applied.
/// If it falls too far (1.5 m below _home, or 4 m away from it) it goes back home.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[DefaultExecutionOrder(100)] // after FingerUDPReceiver / IsdkFingertipProxy, before FingertipSurfaceConstraint (200)
public class PhysicsPinchGrabbable : MonoBehaviour
{
    [System.Serializable]
    public struct Hand
    {
        public Transform thumbTip;
        public Transform indexTip;
        [Tooltip("Optional: the wrist; while held, the object follows the wrist (smoother than the glove fingertips).")]
        public Transform wrist;
    }

    [SerializeField] private Hand[] _hands = new Hand[0];
    [Tooltip("Fingertip radius (m): the tip point lies inside the finger, the skin is this far from it.")]
    [SerializeField] private float _fingerRadius = 0.008f;
    [SerializeField] private float _grabMargin = 0.02f;
    [SerializeField] private float _releaseOpening = 0.015f;
    [SerializeField] private float _releaseDelay = 0.12f;
    [Tooltip("If the holding hand is lost for less than this (s), the object stays held.")]
    [SerializeField] private float _lostGraceSeconds = 0.35f;
    [Tooltip("Antipodal grasp: each fingertip must press on a face at an angle to that face's normal of at most this " +
             "(degrees), i.e. a friction cone (mu ~ 1 -> 45 degrees). Two fingers on the SAME face, or on the two ends of an edge, cannot grab.")]
    [SerializeField] private float _frictionConeDeg = 45f;
    [Tooltip("While held: the object slides back to the MIDDLE between the two tips (along the pinch direction) at this rate (1/s).")]
    [SerializeField] private float _recenterSpeed = 3f;
    [Tooltip("While held: each finger's contact point on the box face SLIDES SLOWLY after the real hand at this rate (1/s), " +
             "and always stays on the SAME face chosen at grab time, never jumping over an edge to another face.")]
    [SerializeField] private float _contactSlideSpeed = 4f;
    [Tooltip("Hand without a wrist: how fast the object slides to the middle of the two tips.")]
    [SerializeField] private float _followSpeed = 20f;
    [Tooltip("Throw velocity = the hand's mean velocity over this time (s) before release.")]
    [SerializeField] private float _throwWindow = 0.1f;
    [SerializeField] private float _throwScale = 1f;

    [Header("Return home")]
    [Tooltip("Start / reset position. Empty = the position at start.")]
    [SerializeField] private Transform _home;
    [SerializeField] private float _resetBelow = 1.5f;
    [SerializeField] private float _resetDistance = 4f;

    public static readonly List<PhysicsPinchGrabbable> Active = new List<PhysicsPinchGrabbable>();

    public bool IsHeld => _heldBy >= 0;

    private Rigidbody _rb;
    private Collider _collider;
    private int _heldBy = -1;
    private bool[] _valid = new bool[0];
    private bool[] _regrabBlocked = new bool[0];
    private float _gapAtGrab, _releaseTimer, _lostTimer;
    private Vector3 _localPos;      // object position in the wrist frame (or relative to the midpoint of the two fingers)
    private Quaternion _localRot;
    private Vector3 _homePos;
    private Quaternion _homeRot;
    private Vector3 _throwLinear, _throwAngular; // velocity when the fingers STARTED to open (the actual release comes _releaseDelay later)
    private float _clock; // own simulation clock (Tick/PhysicsStep), so it can run outside the game loop
    // While holding (box): the face each finger presses on (0 = thumb, 1 = index), fixed at grab time.
    // Face code = axis*2 + (1 if the positive side). Contact points are stored in the box's local space.
    private readonly int[] _heldFace = { -1, -1 };
    private readonly Vector3[] _heldPoint = new Vector3[2];
    private readonly Queue<(float t, Vector3 p, Quaternion r)> _history = new Queue<(float, Vector3, Quaternion)>();

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _collider = GetComponent<Collider>();
        _homePos = transform.position;
        _homeRot = transform.rotation;
        _rb.interpolation = RigidbodyInterpolation.Interpolate;
        _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
    }

    private void OnEnable() => Active.Add(this);
    private void OnDisable() => Active.Remove(this);

    private void EnsureState()
    {
        if (_valid.Length == _hands.Length) return;
        _valid = new bool[_hands.Length];
        _regrabBlocked = new bool[_hands.Length];
        _heldBy = -1;
    }

    /// <summary>Puts the object back home, at rest.</summary>
    public void ResetToHome()
    {
        if (_rb == null) Awake(); // called before Awake (e.g. from the Editor)
        _heldBy = -1;
        _heldFace[0] = _heldFace[1] = -1;
        _rb.isKinematic = false;
        Vector3 p = _home != null ? _home.position : _homePos;
        Quaternion r = _home != null ? _home.rotation : _homeRot;
        _rb.position = p;
        _rb.rotation = r;
        transform.SetPositionAndRotation(p, r);
        _rb.linearVelocity = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
    }

    // Grab/release runs in Update (every frame, in step with the hand). A held object moves with
    // MovePosition in FixedUpdate so it still pushes other objects.
    private void Update() => Tick(Time.deltaTime);
    private void FixedUpdate() => PhysicsStep(Time.fixedDeltaTime);

    /// <summary>One grab/release step. Public so it can run outside Play mode.</summary>
    public void Tick(float dt)
    {
        EnsureState();
        for (int h = 0; h < _hands.Length; h++)
        {
            Transform a = _hands[h].thumbTip, b = _hands[h].indexTip;
            _valid[h] = a != null && b != null && a.gameObject.activeInHierarchy && b.gameObject.activeInHierarchy;
        }

        if (_heldBy >= 0) UpdateHeld(dt);
        if (_heldBy < 0) TryGrab();

        if (_heldBy < 0 && !_rb.isKinematic)
        {
            Vector3 home = _home != null ? _home.position : _homePos;
            if (transform.position.y < home.y - _resetBelow || (transform.position - home).sqrMagnitude > _resetDistance * _resetDistance)
                ResetToHome();
        }
    }

    private void TryGrab()
    {
        for (int h = 0; h < _hands.Length; h++)
        {
            if (!_valid[h]) { _regrabBlocked[h] = false; continue; }
            Vector3 thumb = Tracked(_hands[h].thumbTip), index = Tracked(_hands[h].indexTip);
            bool canGrab = SkinDistance(thumb) < _grabMargin && SkinDistance(index) < _grabMargin && IsAntipodal(thumb, index);
            if (_regrabBlocked[h])
            {
                if (!canGrab) _regrabBlocked[h] = false;
                continue;
            }
            if (!canGrab) continue;

            _heldBy = h;
            _gapAtGrab = Vector3.Distance(thumb, index);
            _releaseTimer = _lostTimer = 0f;
            _rb.isKinematic = true;
            _history.Clear();
            Pose anchor = Anchor(h);
            _localPos = Quaternion.Inverse(anchor.rotation) * (transform.position - anchor.position);
            _localRot = Quaternion.Inverse(anchor.rotation) * transform.rotation;
            // Fix the face for each finger (IsAntipodal has confirmed two opposite faces)
            _heldFace[0] = FaceOf(thumb);
            _heldFace[1] = FaceOf(index);
            if (_heldFace[0] >= 0) _heldPoint[0] = ProjectOnFace(thumb, _heldFace[0]);
            if (_heldFace[1] >= 0) _heldPoint[1] = ProjectOnFace(index, _heldFace[1]);
            return;
        }
    }

    private void UpdateHeld(float dt)
    {
        int h = _heldBy;
        _lostTimer = _valid[h] ? 0f : _lostTimer + dt;
        bool release = _lostTimer > _lostGraceSeconds;
        if (_valid[h])
        {
            float gap = Vector3.Distance(Tracked(_hands[h].thumbTip), Tracked(_hands[h].indexTip));
            // Reference = the gap at grab time, but never less than the object's width between the fingers:
            // if the real fingers squeeze "through" a rigid object (nothing stops them), relaxing the hand a
            // little must still keep it held.
            _gapAtGrab = Mathf.Min(_gapAtGrab, Mathf.Max(gap, ObjectWidthBetweenFingers(h)));
            // Contact points slide slowly after the real hand, but never leave the chosen face
            float slide = 1f - Mathf.Exp(-_contactSlideSpeed * dt);
            SlideContact(0, Tracked(_hands[h].thumbTip), slide);
            SlideContact(1, Tracked(_hands[h].indexTip), slide);
            bool opening = gap > _gapAtGrab + _releaseOpening;
            // Throw: the hand has usually slowed down by the time the release condition has lasted _releaseDelay,
            // so take the velocity at the moment the fingers start to open.
            if (opening && _releaseTimer == 0f) MeasureVelocity(out _throwLinear, out _throwAngular);
            _releaseTimer = opening ? _releaseTimer + dt : 0f;
            release |= _releaseTimer >= _releaseDelay;
        }
        if (!release) return;

        _heldBy = -1;
        _heldFace[0] = _heldFace[1] = -1;
        _regrabBlocked[h] = _valid[h];
        _rb.isKinematic = false;
        if (_releaseTimer <= 0f) MeasureVelocity(out _throwLinear, out _throwAngular); // released because the hand was lost
        _rb.linearVelocity = _throwLinear * _throwScale;
        _rb.angularVelocity = _throwAngular;
        _releaseTimer = 0f;
    }

    /// <summary>One physics step: a held object follows the hand. Public so it can be tested.</summary>
    public void PhysicsStep(float dt)
    {
        _clock += dt;
        if (_heldBy < 0 || !_valid[_heldBy]) return;
        Pose anchor = Anchor(_heldBy);
        RecenterBetweenFingers(anchor, dt);
        Vector3 pos = anchor.position + anchor.rotation * _localPos;
        Quaternion rot = anchor.rotation * _localRot;
        if (_hands[_heldBy].wrist == null)
        {
            float k = 1f - Mathf.Exp(-_followSpeed * dt);
            pos = Vector3.Lerp(_rb.position, pos, k);
            rot = Quaternion.Slerp(_rb.rotation, rot, k);
        }
        _rb.MovePosition(pos);
        _rb.MoveRotation(rot);

        _history.Enqueue((_clock, pos, rot));
        while (_history.Count > 2 && _clock - _history.Peek().t > _throwWindow) _history.Dequeue();
    }

    /// <summary>Mean velocity (linear + angular) of the object over the last _throwWindow.</summary>
    private void MeasureVelocity(out Vector3 linear, out Vector3 angular)
    {
        linear = angular = Vector3.zero;
        if (_history.Count < 2) return;
        var first = _history.Peek();
        (float t, Vector3 p, Quaternion r) last = default;
        foreach (var e in _history) last = e;
        float span = last.t - first.t;
        if (span < 1e-3f) return;
        linear = (last.p - first.p) / span;
        (last.r * Quaternion.Inverse(first.r)).ToAngleAxis(out float deg, out Vector3 axis);
        if (deg > 180f) deg -= 360f;
        if (!float.IsNaN(axis.x) && !float.IsInfinity(axis.x)) angular = axis * (deg * Mathf.Deg2Rad / span);
    }

    /// <summary>A proper antipodal pinch: two contact points on OPPOSITE faces, with the line between the fingertips
    /// inside the friction cone of both faces (Nguyen 1988, two-finger force closure).</summary>
    private bool IsAntipodal(Vector3 thumb, Vector3 index)
    {
        Vector3 axis = index - thumb;
        if (axis.sqrMagnitude < 1e-8f) return false;
        axis.Normalize();
        float cosCone = Mathf.Cos(_frictionConeDeg * Mathf.Deg2Rad);
        // OUTWARD normal at the contact: the thumb presses against the axis direction, the index along it
        return Vector3.Dot(OutwardNormal(thumb), -axis) > cosCone &&
               Vector3.Dot(OutwardNormal(index), axis) > cosCone;
    }

    /// <summary>Outward normal of the object's face closest to this point.</summary>
    private Vector3 OutwardNormal(Vector3 point)
    {
        int face = FaceOf(point);
        if (face >= 0) return FaceNormal(face);
        Vector3 c = ClosestSurfacePoint(point, out bool inside);
        Vector3 dir = inside ? c - point : point - c;
        return dir.sqrMagnitude > 1e-10f ? dir.normalized : (point - _collider.bounds.center).normalized;
    }

    /// <summary>The box face closest to this point (code = axis*2 + 1 if the positive side). -1 = the object is not a box.
    /// Picks the face with the LARGEST signed distance (m): outside the box = the face the point sticks out of most,
    /// inside the box = the nearest face.</summary>
    public int FaceOf(Vector3 world)
    {
        if (!(_collider is BoxCollider box)) return -1;
        Transform t = box.transform;
        Vector3 half = box.size * 0.5f, sc = t.lossyScale;
        Vector3 p = t.InverseTransformPoint(world) - box.center;
        int best = 0; float bestD = float.MinValue;
        for (int a = 0; a < 3; a++)
        {
            float d = (Mathf.Abs(p[a]) - half[a]) * Mathf.Abs(sc[a]);
            if (d > bestD) { bestD = d; best = a; }
        }
        return best * 2 + (p[best] >= 0f ? 1 : 0);
    }

    /// <summary>Outward normal (world) of a box face.</summary>
    private Vector3 FaceNormal(int face)
    {
        Vector3 n = Vector3.zero;
        n[face / 2] = (face & 1) == 1 ? 1f : -1f;
        return _collider.transform.TransformDirection(n).normalized;
    }

    /// <summary>Projects a point onto a box face (box local space), pulled in from the edges a little so the finger pad
    /// sits fully on the face instead of hanging over an edge.</summary>
    private Vector3 ProjectOnFace(Vector3 world, int face)
    {
        var box = (BoxCollider)_collider;
        Transform t = box.transform;
        Vector3 half = box.size * 0.5f, sc = t.lossyScale;
        Vector3 p = t.InverseTransformPoint(world) - box.center;
        int axis = face / 2;
        for (int a = 0; a < 3; a++)
        {
            if (a == axis) continue;
            float inset = Mathf.Min(_fingerRadius * 0.75f / Mathf.Max(Mathf.Abs(sc[a]), 1e-6f), half[a]);
            p[a] = Mathf.Clamp(p[a], -half[a] + inset, half[a] - inset);
        }
        p[axis] = (face & 1) == 1 ? half[axis] : -half[axis];
        return p + box.center;
    }

    private void SlideContact(int finger, Vector3 tip, float k)
    {
        int face = _heldFace[finger];
        if (face < 0) return;
        _heldPoint[finger] = Vector3.Lerp(_heldPoint[finger], ProjectOnFace(tip, face), k);
    }

    /// <summary>The fingertip position from the REAL hand, not the virtual finger already placed on the object's surface
    /// (while holding, the virtual finger always sits on the surface, so reading it would never see the hand open to release).</summary>
    private static Vector3 Tracked(Transform tip) => FingertipSurfaceConstraint.TrackedPosition(tip);

    /// <summary>Whether this fingertip (thumb / index) belongs to the hand holding the object.</summary>
    public bool IsHoldingTip(Transform tip) =>
        _heldBy >= 0 && tip != null && (_hands[_heldBy].thumbTip == tip || _hands[_heldBy].indexTip == tip);

    /// <summary>The box face this fingertip is pressing on (while held). -1 = not held / not a box.</summary>
    public int HeldFace(Transform dataTip)
    {
        if (_heldBy < 0 || dataTip == null) return -1;
        if (_hands[_heldBy].thumbTip == dataTip) return _heldFace[0];
        if (_hands[_heldBy].indexTip == dataTip) return _heldFace[1];
        return -1;
    }

    /// <summary>Slides the object (ONLY along the pinch direction) so its centre moves to the middle of the two tips:
    /// both fingers always hug the two faces evenly. Offsets in other directions (e.g. holding near one end of a box) are kept.</summary>
    private void RecenterBetweenFingers(Pose anchor, float dt)
    {
        Vector3 a = Tracked(_hands[_heldBy].thumbTip), b = Tracked(_hands[_heldBy].indexTip);
        Vector3 axis = b - a;
        if (axis.sqrMagnitude < 1e-8f) return;
        axis.Normalize();
        Vector3 pos = anchor.position + anchor.rotation * _localPos;
        Vector3 center = pos + (_collider.bounds.center - _rb.position);
        float along = Vector3.Dot(center - (a + b) * 0.5f, axis);
        float k = 1f - Mathf.Exp(-_recenterSpeed * dt);
        pos -= axis * (along * k);
        _localPos = Quaternion.Inverse(anchor.rotation) * (pos - anchor.position);
    }

    /// <summary>Attachment point: the wrist (if any) or the midpoint of the two tips, oriented along the finger axis.</summary>
    private Pose Anchor(int h)
    {
        Transform w = _hands[h].wrist;
        if (w != null && w.gameObject.activeInHierarchy) return new Pose(w.position, w.rotation);
        Vector3 a = Tracked(_hands[h].thumbTip), b = Tracked(_hands[h].indexTip);
        Vector3 axis = b - a;
        Quaternion rot = axis.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(axis.normalized, Vector3.up) : Quaternion.identity;
        return new Pose((a + b) * 0.5f, rot);
    }

    /// <summary>Distance from the fingertip SKIN to the object's surface (negative = the skin is inside).</summary>
    public float SkinDistance(Vector3 point)
    {
        Vector3 closest = ClosestSurfacePoint(point, out bool inside);
        float d = Vector3.Distance(point, closest);
        return (inside ? -d : d) - _fingerRadius;
    }

    /// <summary>Object width between the fingers + 2 finger radii (m) = the gap between the tips when they just touch both faces.
    /// A box pinched on two opposite faces: use the box thickness along the pinch axis (constant). Using the surface points
    /// closest to each finger failed: when the real fingers squeeze deep into the box (nothing stops them) those points jump to the
    /// side faces, the "width" shrinks to 1-2 cm and the release threshold drops with it, so relaxing the hand slightly (gap still
    /// &lt; box width) dropped the box (measured: 4 releases at a gap of 4.5-5.6 cm, box 4 cm + finger = 5.6 cm).</summary>
    private float ObjectWidthBetweenFingers(int h)
    {
        int f0 = _heldFace[0], f1 = _heldFace[1];
        if (f0 >= 0 && f1 >= 0 && f0 / 2 == f1 / 2 && f0 != f1 && _collider is BoxCollider box)
        {
            int axis = f0 / 2;
            return box.size[axis] * Mathf.Abs(box.transform.lossyScale[axis]) + 2f * _fingerRadius;
        }
        Vector3 a = Tracked(_hands[h].thumbTip), b = Tracked(_hands[h].indexTip);
        Vector3 pa = ClosestSurfacePoint(a, out _), pb = ClosestSurfacePoint(b, out _);
        return Vector3.Distance(pa, pb) + 2f * _fingerRadius;
    }

    /// <summary>Closest point ON the surface (also when the point is inside) for Box and Sphere colliders.
    /// Other colliders: Physics.ClosestPoint (only correct when the point is outside).</summary>
    public Vector3 ClosestSurfacePoint(Vector3 world, out bool inside)
    {
        inside = false;
        if (_collider is BoxCollider box)
        {
            Transform t = box.transform;
            Vector3 half = box.size * 0.5f;
            Vector3 p = t.InverseTransformPoint(world) - box.center;
            Vector3 q = new Vector3(Mathf.Clamp(p.x, -half.x, half.x), Mathf.Clamp(p.y, -half.y, half.y), Mathf.Clamp(p.z, -half.z, half.z));
            if (q == p)
            {
                inside = true;
                // Push out through the nearest face (in metres, since the box may be scaled non-uniformly)
                Vector3 s = t.lossyScale;
                float dx = (half.x - Mathf.Abs(p.x)) * Mathf.Abs(s.x);
                float dy = (half.y - Mathf.Abs(p.y)) * Mathf.Abs(s.y);
                float dz = (half.z - Mathf.Abs(p.z)) * Mathf.Abs(s.z);
                if (dx <= dy && dx <= dz) q.x = Mathf.Sign(p.x) * half.x;
                else if (dy <= dz) q.y = Mathf.Sign(p.y) * half.y;
                else q.z = Mathf.Sign(p.z) * half.z;
            }
            return t.TransformPoint(q + box.center);
        }
        if (_collider is SphereCollider sphere)
        {
            Vector3 c = sphere.transform.TransformPoint(sphere.center);
            float r = sphere.radius * Mathf.Max(Mathf.Abs(sphere.transform.lossyScale.x),
                Mathf.Max(Mathf.Abs(sphere.transform.lossyScale.y), Mathf.Abs(sphere.transform.lossyScale.z)));
            Vector3 d = world - c;
            inside = d.sqrMagnitude < r * r;
            return c + (d.sqrMagnitude > 1e-12f ? d.normalized : Vector3.up) * r;
        }
        return Physics.ClosestPoint(world, _collider, transform.position, transform.rotation);
    }

    // --- For HapticRenderer: force on the fingertips when touching / pinching ---

    /// <summary>How deep the fingertip skin is inside the object (m, + = inside, negative = still away from the surface).</summary>
    public bool TryGetSkinDepth(Transform tip, out float depthMeters)
    {
        depthMeters = float.NegativeInfinity;
        if (_collider == null || tip == null) return false;
        depthMeters = -SkinDistance(tip.position);
        return true;
    }

    /// <summary>Direction the object pushes the fingertip (world): the normal of the box face closest to the fingertip.</summary>
    public Vector3 ContactNormal(Transform tip)
    {
        if (tip == null || _collider == null) return Vector3.up;
        int face = FaceOf(tip.position);
        if (face >= 0) return FaceNormal(face);
        Vector3 d = tip.position - _collider.bounds.center;
        return d.sqrMagnitude > 1e-12f ? d.normalized : Vector3.up;
    }

    /// <summary>Pinched between EXACTLY these two tips: the SHARED depth of each finger = half the squeeze (object width between the
    /// fingers, plus 2 finger radii, minus the gap between the tips). Stays stable when the real fingers squeeze deep into the box and
    /// does not depend on which finger the box sits closer to, unlike SkinDistance, which jumps to a side face when a finger passes the centre.</summary>
    public bool TryGetPinchDepth(Transform thumbTip, Transform indexTip, out float depthMeters)
    {
        depthMeters = 0f;
        if (_heldBy < 0 || thumbTip == null || indexTip == null) return false;
        if (_hands[_heldBy].thumbTip != thumbTip || _hands[_heldBy].indexTip != indexTip) return false;
        float gap = Vector3.Distance(thumbTip.position, indexTip.position);
        depthMeters = 0.5f * (ObjectWidthBetweenFingers(_heldBy) - gap);
        return true;
    }

    // --- For FingertipSurfaceConstraint: virtual fingers do not go through the object, and sit on the surface while holding ---

    /// <summary>Fingertip skin inside the object -> the point (fingertip centre) where the skin just touches the surface.</summary>
    public bool TryResolvePenetration(Transform tip, out Vector3 resolved) =>
        ResolvePoint(tip.position, _fingerRadius, out resolved);

    /// <summary>A point on the finger's AXIS (finger thickness `radius` m) inside the object -> the nearest point where the
    /// finger surface just touches the object. Used for EVERY finger segment, not just the tip.</summary>
    public bool ResolvePoint(Vector3 point, float radius, out Vector3 resolved)
    {
        resolved = point;
        Vector3 s = ClosestSurfacePoint(point, out bool inside);
        Vector3 d = point - s;
        if (!inside && d.sqrMagnitude >= radius * radius) return false;
        Vector3 outward = inside ? -d : d;
        outward = outward.sqrMagnitude > 1e-12f ? outward.normalized : OutwardNormal(point);
        resolved = s + outward * radius;
        return true;
    }

    /// <summary>Like ResolvePoint, but a point INSIDE the box is always pushed out through the chosen `face`
    /// (not the nearest face): near a box edge the nearest face flips between two faces every frame and the virtual
    /// finger jumps. face &lt; 0: same as ResolvePoint.</summary>
    public bool ResolvePoint(Vector3 point, float radius, int face, out Vector3 resolved)
    {
        if (face < 0 || !(_collider is BoxCollider box)) return ResolvePoint(point, radius, out resolved);
        Transform t = box.transform;
        Vector3 half = box.size * 0.5f;
        Vector3 p = t.InverseTransformPoint(point) - box.center;
        bool inside = Mathf.Abs(p.x) <= half.x && Mathf.Abs(p.y) <= half.y && Mathf.Abs(p.z) <= half.z;
        if (!inside) return ResolvePoint(point, radius, out resolved); // outside: the nearest point is unique, no jumping
        int axis = face / 2;
        float sign = (face & 1) == 1 ? 1f : -1f;
        float depth = (half[axis] - sign * p[axis]) * Mathf.Abs(t.lossyScale[axis]); // metres from the point to the chosen face
        resolved = point + FaceNormal(face) * (depth + radius);
        return true;
    }

    /// <summary>A fingertip of the HOLDING hand (up to 3 cm away or inside) -> placed on the surface.
    /// Box: the contact point is fixed on the face chosen at grab time.</summary>
    public bool TryGetHeldContact(Transform dataTip, Vector3 visualTip, out Vector3 contact)
    {
        contact = visualTip;
        if (!IsHoldingTip(dataTip)) return false;
        int finger = _hands[_heldBy].thumbTip == dataTip ? 0 : 1;
        int face = _heldFace[finger];
        // Box: while held the finger SITS ON THE FACE however far the real (noisy) hand drifts away; releasing is
        // UpdateHeld's job (a clear opening for _releaseDelay). Previously a gap over 3 cm let the virtual finger open
        // and then snap back the next frame -> the thumb kept opening and closing.
        if (face < 0 && SkinDistance(visualTip) > 0.03f) return false;
        contact = face >= 0
            ? _collider.transform.TransformPoint(_heldPoint[finger]) + FaceNormal(face) * _fingerRadius
            : SurfacePlusFinger(visualTip);
        return true;
    }

    private Vector3 SurfacePlusFinger(Vector3 p)
    {
        Vector3 s = ClosestSurfacePoint(p, out bool inside);
        Vector3 outward = inside ? s - p : p - s;
        if (outward.sqrMagnitude < 1e-12f) outward = s - _collider.bounds.center;
        return s + outward.normalized * _fingerRadius;
    }
}
