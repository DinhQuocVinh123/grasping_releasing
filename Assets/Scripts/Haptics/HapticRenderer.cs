using System;
using UnityEngine;

/// <summary>
/// Computes the HAPTIC SIGNALS for the thumb and index finger of ONE hand and sends them to every output device
/// (IHapticGlove): the glove, the controller vibration, the debug output.
///
/// Every frame, for each finger:
///   1. CONTINUOUS force: how deep the REAL fingertip is inside the object. This follows the god-object method
///      (Zilles &amp; Salisbury 1995): the virtual finger is held on the surface (FingertipSurfaceConstraint), and
///      the gap between the real and the virtual finger is the force to render.
///   2. EVENTS: touch / untouch / grab / release -> one short vibration pulse each (Kuchenbecker et al. 2006);
///      the touch pulse is stronger for a faster approach.
///
/// Everything is computed on the headset from VIRTUAL contact, without a round trip through the PC, to keep the
/// delay between what is seen and what is felt as small as possible.
/// </summary>
[DefaultExecutionOrder(150)] // after SquishyPinchable (100), BEFORE FingertipSurfaceConstraint (200)
public class HapticRenderer : MonoBehaviour
{
    [Header("REAL fingertips (not yet pushed out of the object)")]
    [Tooltip("Bare hand: LeftThumbTip (updated by IsdkFingertipProxy). Glove hand: XRHand_ThumbTip; this script runs before FingertipSurfaceConstraint, so it still reads the real position.")]
    [SerializeField] private Transform _thumbTip;
    [SerializeField] private Transform _indexTip;

    [Header("Objects that can be felt")]
    [Tooltip("Empty = every squeezable object in the scene.")]
    [SerializeField] private SquishyPinchable[] _objects;
    [Tooltip("Rigid cubes (no compression). Empty = every PhysicsPinchGrabbable in the scene. Stiffness comes from the cube's HapticMaterial: " +
             "Pressure At Max Indent = 0, Extra Depth To Full Pressure = how far (m) the real finger goes in to reach full force.")]
    [SerializeField] private PhysicsPinchGrabbable[] _cubes;

    [Header("Output devices (any component implementing IHapticGlove)")]
    [SerializeField] private MonoBehaviour[] _outputs;

    [Header("No flicker at the surface edge")]
    [Tooltip("The fingertip must move this far (m) away from the surface to count as 'left'. Avoids constant touch/untouch while the finger trembles at the edge.")]
    [SerializeField] private float _untouchHysteresis = 0.002f;

    [Header("Two-finger pinch")]
    [Tooltip("An object HELD between thumb and index: both fingers get the SAME force, in opposite directions along the line between the fingertips " +
             "(an object at rest between two fingers needs equal forces on both sides). The shared depth = mean of both fingers' depths = half the squeeze, " +
             "regardless of which finger the object sits closer to (if tracking puts the index a few mm short, the index still gets the same force as the thumb). " +
             "Off = each finger uses its own depth, direction from the object centre to the fingertip.")]
    [SerializeField] private bool _equalPinchForces = true;

    /// <summary>Current state of one finger, for display / logging.</summary>
    [Serializable]
    public struct FingerState
    {
        public bool touching;
        public float depthMeters;
        public float pressure01;
        public SquishyPinchable touchedObject;
        [Tooltip("Force the object pushes back on the finger (N, world space): along the surface normal, magnitude = force level x the object's MaxForceNewton.")]
        public Vector3 forceNewton;
    }

    [Header("Read only (watch in Play mode)")]
    [SerializeField] private FingerState[] _state = new FingerState[2];

    /// <summary>Raised on every event, for logging / experiments. The last argument is the object involved
    /// (may be null if unknown).</summary>
    public event Action<HapticFinger, HapticEventType, HapticTransient, SquishyPinchable> WhenHapticEvent;

    public FingerState GetState(HapticFinger finger) => _state[(int)finger];

    private readonly float[] _previousDepth = new float[2];
    private readonly Component[] _touchedTarget = new Component[2]; // ball or cube being touched -- material for the Untouch pulse
    private IHapticGlove[] _gloves;
    private SquishyPinchable[] _heldLastFrame = Array.Empty<SquishyPinchable>();

    private void LateUpdate() => Tick(Time.deltaTime);

    /// <summary>One computation step. Public so it can run outside Play mode.</summary>
    public void Tick(float dt)
    {
        if (dt <= 0f) return;
        CacheOutputs();

        var objects = CurrentObjects();
        Contact thumb = Measure(_thumbTip, objects);
        Contact index = Measure(_indexTip, objects);
        if (_equalPinchForces) SharePinch(objects, ref thumb, ref index);
        UpdateFinger(HapticFinger.Thumb, _thumbTip, thumb, dt);
        UpdateFinger(HapticFinger.Index, _indexTip, index, dt);
        UpdateGrabEvents(objects);
    }

    /// <summary>Penetration of one fingertip into the object it is DEEPEST in (ball OR cube).</summary>
    private struct Contact
    {
        public bool valid;              // the fingertip exists (is tracked)
        public float depth;             // m, + = inside the object
        public SquishyPinchable obj;    // ball
        public PhysicsPinchGrabbable cube; // rigid cube
        public Vector3 normal;          // direction the object pushes the finger; zero = ask the object (ContactNormal)

        public Component Target => obj != null ? obj : cube;
    }

    private Contact Measure(Transform tip, System.Collections.Generic.IReadOnlyList<SquishyPinchable> objects)
    {
        var c = new Contact { valid = tip != null && tip.gameObject.activeInHierarchy, depth = float.NegativeInfinity };
        if (!c.valid) return c;
        foreach (var obj in objects)
        {
            if (obj == null || !obj.isActiveAndEnabled) continue;
            if (obj.TryGetSkinDepth(tip, out float d) && d > c.depth)
            {
                c.depth = d;
                c.obj = obj;
            }
        }
        foreach (var cube in CurrentCubes())
        {
            if (cube == null || !cube.isActiveAndEnabled) continue;
            if (cube.TryGetSkinDepth(tip, out float d) && d > c.depth)
            {
                c.depth = d;
                c.obj = null;
                c.cube = cube;
            }
        }
        return c;
    }

    /// <summary>Object held between the two fingers: both fingers use the MEAN depth (= half the squeeze: total depth =
    /// 2 * radius - distance between the tips when they are on opposite sides), force along the line between the tips, pushing
    /// each finger away from the other. The centre-to-tip direction (ContactNormal) flips when a tip gets close to the centre;
    /// the line between the two fingers does not.</summary>
    private void SharePinch(System.Collections.Generic.IReadOnlyList<SquishyPinchable> objects, ref Contact thumb, ref Contact index)
    {
        if (!thumb.valid || !index.valid) return;
        foreach (var obj in objects)
        {
            if (obj == null || !obj.isActiveAndEnabled || !obj.IsHeldBy(_thumbTip)) continue;
            if (!obj.TryGetSkinDepth(_thumbTip, out float dThumb) || !obj.TryGetSkinDepth(_indexTip, out float dIndex)) return;
            SetPinch(ref thumb, ref index, 0.5f * (dThumb + dIndex), obj, null);
            return;
        }
        foreach (var cube in CurrentCubes())
        {
            if (cube == null || !cube.isActiveAndEnabled) continue;
            if (!cube.TryGetPinchDepth(_thumbTip, _indexTip, out float shared)) continue;
            SetPinch(ref thumb, ref index, shared, null, cube);
            return;
        }
    }

    private void SetPinch(ref Contact thumb, ref Contact index, float depth, SquishyPinchable obj, PhysicsPinchGrabbable cube)
    {
        Vector3 axis = _thumbTip.position - _indexTip.position; // index -> thumb
        if (axis.sqrMagnitude < 1e-10f) return;
        axis.Normalize();

        thumb.depth = index.depth = depth;
        thumb.obj = index.obj = obj;
        thumb.cube = index.cube = cube;
        thumb.normal = axis;
        index.normal = -axis;
    }

    private void UpdateFinger(HapticFinger finger, Transform tip, Contact contact, float dt)
    {
        int f = (int)finger;
        FingerState state = _state[f];
        float depth = contact.depth;
        SquishyPinchable deepest = contact.obj;
        Component target = contact.Target;
        bool tipValid = contact.valid;

        bool touching = state.touching
            ? depth > -_untouchHysteresis
            : depth > 0f;

        if (touching && !state.touching)
        {
            // Approach speed at the moment of contact: a fast touch -> a strong pulse
            float approachSpeed = float.IsInfinity(_previousDepth[f])
                ? 0f
                : Mathf.Max(0f, (depth - _previousDepth[f]) / dt);
            Emit(finger, HapticEventType.Touch, MaterialOf(target).TransientFor(HapticEventType.Touch, approachSpeed), deepest);
        }
        else if (!touching && state.touching)
        {
            Emit(finger, HapticEventType.Untouch, MaterialOf(_touchedTarget[f]).TransientFor(HapticEventType.Untouch, 0f), state.touchedObject);
        }

        state.touching = touching;
        state.depthMeters = tipValid ? depth : 0f;
        state.touchedObject = touching ? deepest : null;
        _touchedTarget[f] = touching ? target : null;
        // Rigid cube: maximum compression = 0 -> force rises from 0 to full over the material's Extra Depth To Full Pressure.
        float maxIndent = deepest != null ? deepest.MaxIndentMeters : 0f;
        state.pressure01 = touching && target != null
            ? MaterialOf(target).PressureFromDepth(depth, maxIndent)
            : 0f;
        // Force vector (god-object): the object pushes the finger OUTWARD along the normal, magnitude proportional to the force level.
        Vector3 normal = contact.normal;
        if (normal == Vector3.zero && deepest != null) normal = deepest.ContactNormal(tip);
        else if (normal == Vector3.zero && contact.cube != null) normal = contact.cube.ContactNormal(tip);
        state.forceNewton = touching && target != null
            ? normal * (state.pressure01 * MaterialOf(target).MaxForceNewton)
            : Vector3.zero;
        _state[f] = state;
        _previousDepth[f] = tipValid ? depth : float.NegativeInfinity;

        foreach (var glove in _gloves) glove.SetPressure(finger, state.pressure01);
    }

    private void UpdateGrabEvents(System.Collections.Generic.IReadOnlyList<SquishyPinchable> objects)
    {
        // "Grabbed" in terms of FEEL = the object is held AND both fingertips actually touch it. SquishyPinchable has a
        // "snap" zone (Grab Margin), so it can attach while a finger is still a few mm from the surface; sending the Grab
        // pulse at that moment would make the glove say "grabbed" before the finger touches the object.
        var heldNow = new System.Collections.Generic.List<SquishyPinchable>();
        foreach (var obj in objects)
        {
            if (obj == null || !obj.isActiveAndEnabled || !obj.IsHeldBy(_thumbTip)) continue;

            bool alreadyHeld = Array.IndexOf(_heldLastFrame, obj) >= 0;
            bool bothTouching = _state[0].touchedObject == obj && _state[1].touchedObject == obj;
            if (alreadyHeld || bothTouching) heldNow.Add(obj);
        }

        foreach (var obj in heldNow)
        {
            if (Array.IndexOf(_heldLastFrame, obj) < 0) EmitBoth(HapticEventType.Grab, obj);
        }
        foreach (var obj in _heldLastFrame)
        {
            if (obj != null && !heldNow.Contains(obj)) EmitBoth(HapticEventType.Release, obj);
        }
        _heldLastFrame = heldNow.ToArray();
    }

    private void EmitBoth(HapticEventType type, SquishyPinchable obj)
    {
        HapticTransient transient = MaterialOf(obj).TransientFor(type, 0f);
        Emit(HapticFinger.Thumb, type, transient, obj);
        Emit(HapticFinger.Index, type, transient, obj);
    }

    private void Emit(HapticFinger finger, HapticEventType type, HapticTransient transient, SquishyPinchable obj)
    {
        foreach (var glove in _gloves) glove.PlayTransient(finger, type, transient);
        WhenHapticEvent?.Invoke(finger, type, transient, obj);
    }

    private System.Collections.Generic.IReadOnlyList<SquishyPinchable> CurrentObjects() =>
        _objects != null && _objects.Length > 0 ? _objects : SquishyPinchable.Active;

    private System.Collections.Generic.IReadOnlyList<PhysicsPinchGrabbable> CurrentCubes() =>
        _cubes != null && _cubes.Length > 0 ? _cubes : PhysicsPinchGrabbable.Active;

    private static HapticMaterial MaterialOf(Component obj)
    {
        if (obj != null && obj.TryGetComponent(out HapticMaterial material)) return material;
        return HapticMaterial.Default;
    }

    private void CacheOutputs()
    {
        if (_gloves != null && _gloves.Length == CountOutputs()) return;

        var list = new System.Collections.Generic.List<IHapticGlove>();
        if (_outputs != null)
        {
            foreach (var output in _outputs)
            {
                if (output is IHapticGlove glove) list.Add(glove);
                else if (output != null) Debug.LogWarning($"{output.name}: {output.GetType().Name} does not implement IHapticGlove, ignored.", this);
            }
        }
        _gloves = list.ToArray();
    }

    private int CountOutputs()
    {
        int n = 0;
        if (_outputs != null) foreach (var output in _outputs) if (output is IHapticGlove) n++;
        return n;
    }
}
