using UnityEngine;

/// <summary>
/// How an object FEELS when touched: how much force for how much compression, and what the vibration pulses
/// on touch / grab / release are like. Put it on the same object as SquishyPinchable; without it the object
/// uses the default values (a soft ball).
///
/// Kept separate from SquishyPinchable so the same deformation can carry different feels (rubber ball,
/// foam ball, orange...), tuned in the Inspector without code changes.
/// </summary>
public class HapticMaterial : MonoBehaviour
{
    [Header("Continuous force from compression")]
    [Tooltip("Force (0..1) when the object is compressed to its MAXIMUM. Within that range the force rises linearly (like a spring). Low = soft object, high = hard object.")]
    [Range(0f, 1f)]
    [SerializeField] private float _pressureAtMaxIndent = 0.6f;
    [Tooltip("Once the object is fully compressed and the REAL finger keeps pressing ('bottoming out'), the force rises from the value above to 1.0 over this distance (m). Small = abrupt, hard bottoming out.")]
    [SerializeField] private float _extraDepthToFullPressure = 0.01f;
    [Tooltip("REAL force (N) for a force level of 1.0, to convert the 0..1 level to newtons for the actuator. " +
             "A soft ball is a few N; keep it below the actuator's safe maximum.")]
    [SerializeField] private float _maxForceNewton = 4f;

    /// <summary>Real force (N) for a force level of 1.0.</summary>
    public float MaxForceNewton => _maxForceNewton;

    [Header("Touch pulse")]
    [Tooltip("Pulse when the fingertip touches the surface. This amplitude is for a FAST touch; slower touches give a smaller pulse (see below).")]
    [SerializeField] private HapticTransient _touch = new HapticTransient(0.8f, 120f, 0.025f);
    [Tooltip("Approach speed (m/s) for a full-amplitude touch pulse. Slower -> smaller pulse, as in reality: a hard tap is louder.")]
    [SerializeField] private float _speedForFullTouch = 0.3f;
    [Tooltip("Minimum touch pulse amplitude (as a fraction), so even a very light touch is felt.")]
    [Range(0f, 1f)]
    [SerializeField] private float _minTouchScale = 0.3f;
    [SerializeField] private HapticTransient _untouch = new HapticTransient(0.2f, 80f, 0.02f);

    [Header("Grab / release pulses")]
    [SerializeField] private HapticTransient _grab = new HapticTransient(0.5f, 100f, 0.04f);
    [SerializeField] private HapticTransient _release = new HapticTransient(0.3f, 80f, 0.03f);

    /// <summary>Force (0..1) at maximum compression; can be changed at run time (experiment).</summary>
    public float PressureAtMaxIndent
    {
        get => _pressureAtMaxIndent;
        set => _pressureAtMaxIndent = Mathf.Clamp01(value);
    }

    private static HapticMaterial _default;

    /// <summary>Default values for objects without a HapticMaterial.</summary>
    public static HapticMaterial Default
    {
        get
        {
            if (_default == null)
            {
                var go = new GameObject("HapticMaterial (default)") { hideFlags = HideFlags.HideAndDontSave };
                _default = go.AddComponent<HapticMaterial>();
            }
            return _default;
        }
    }

    /// <summary>Converts the fingertip's penetration depth (m) to a force level 0..1.</summary>
    public float PressureFromDepth(float depthMeters, float maxIndentMeters)
    {
        if (depthMeters <= 0f) return 0f;
        if (maxIndentMeters <= 1e-6f || depthMeters >= maxIndentMeters)
        {
            float extra = depthMeters - Mathf.Max(maxIndentMeters, 0f);
            float t = Mathf.Clamp01(extra / Mathf.Max(_extraDepthToFullPressure, 1e-5f));
            return Mathf.Lerp(_pressureAtMaxIndent, 1f, t);
        }
        return _pressureAtMaxIndent * depthMeters / maxIndentMeters;
    }

    /// <summary>Pulse for an event; the touch pulse scales with the approach speed.</summary>
    public HapticTransient TransientFor(HapticEventType type, float approachSpeed)
    {
        switch (type)
        {
            case HapticEventType.Touch:
                float speed01 = Mathf.Clamp01(approachSpeed / Mathf.Max(_speedForFullTouch, 1e-4f));
                return _touch.Scaled(Mathf.Lerp(_minTouchScale, 1f, speed01));
            case HapticEventType.Untouch: return _untouch;
            case HapticEventType.Grab: return _grab;
            default: return _release;
        }
    }
}
