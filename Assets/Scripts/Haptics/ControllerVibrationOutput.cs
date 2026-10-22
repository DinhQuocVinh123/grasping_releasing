using UnityEngine;

/// <summary>
/// Haptic output through the Quest CONTROLLER's vibration, to feel the haptic signals before the real glove
/// is ready. Hold the controller in the other hand (or strap it to the wrist) while squeezing with the bare
/// hand: Palmer et al. (IROS 2022) showed that moving thumb/index feedback to the wrist still works better
/// than no feedback.
///
/// Needs "Simultaneous Hands And Controllers" (OVRManager) and Hand Tracking Support = Controllers And Hands;
/// otherwise the Quest stops tracking hands as soon as a controller is picked up.
///
/// Limits compared with the glove: one motor for BOTH fingers (the stronger finger wins), and the motor
/// responds slowly, so very short pulses (tens of ms) are stretched to _minTransientSeconds to be felt.
/// </summary>
[DefaultExecutionOrder(300)] // after HapticRenderer (150): vibrate once this frame's data is ready
public class ControllerVibrationOutput : MonoBehaviour, IHapticGlove
{
    public enum Target { Left, Right, Both }

    [SerializeField] private Target _controller = Target.Right;

    [Header("Continuous force")]
    [Tooltip("The continuous force is multiplied by this before vibrating, so the background vibration is weaker than touch pulses and the pulses stand out.")]
    [Range(0f, 1f)]
    [SerializeField] private float _pressureGain = 0.6f;
    [Tooltip("No vibration below this force (avoids a constant faint buzz when the finger is just next to the surface).")]
    [Range(0f, 0.5f)]
    [SerializeField] private float _pressureDeadzone = 0.05f;
    [Tooltip("Vibration frequency for the continuous force (0..1 of the controller's range).")]
    [Range(0f, 1f)]
    [SerializeField] private float _pressureFrequency = 0.3f;

    [Header("Event pulses")]
    [Tooltip("Pulses shorter than this are stretched: the controller motor needs time to spin up.")]
    [SerializeField] private float _minTransientSeconds = 0.06f;
    [Tooltip("Frequency (Hz) that maps to the controller's top setting, to convert pulse frequency (Hz) to 0..1.")]
    [SerializeField] private float _maxControllerHz = 320f;

    private readonly float[] _pressure = new float[2];
    private float _transientAmplitude;
    private float _transientStart = -1f;
    private float _transientDuration;
    private float _transientFrequency01;
    private bool _wasVibrating;

    public void SetPressure(HapticFinger finger, float pressure01)
    {
        _pressure[(int)finger] = pressure01;
    }

    public void PlayTransient(HapticFinger finger, HapticEventType type, HapticTransient transient)
    {
        // A new pulse only replaces the current one if it is stronger than what is left of it
        // (both fingers sending Grab in the same frame must not count twice).
        if (transient.amplitude < CurrentTransientEnvelope()) return;

        _transientAmplitude = transient.amplitude;
        _transientStart = Time.time;
        _transientDuration = Mathf.Max(transient.durationSeconds, _minTransientSeconds);
        _transientFrequency01 = Mathf.Clamp01(transient.frequencyHz / Mathf.Max(_maxControllerHz, 1f));
    }

    private float CurrentTransientEnvelope()
    {
        if (_transientStart < 0f) return 0f;
        float t = Time.time - _transientStart;
        if (t >= _transientDuration) return 0f;
        // Exponential decay, down to about 5% at the end of the pulse
        return _transientAmplitude * Mathf.Exp(-3f * t / _transientDuration);
    }

    private void LateUpdate()
    {
        float pressure = Mathf.Max(_pressure[0], _pressure[1]);
        float continuous = pressure > _pressureDeadzone ? pressure * _pressureGain : 0f;
        float transient = CurrentTransientEnvelope();

        float amplitude = Mathf.Max(continuous, transient);
        float frequency = transient > continuous ? _transientFrequency01 : _pressureFrequency;

        // Only send while vibrating, plus once to turn it OFF when it has just stopped.
        if (amplitude > 0f || _wasVibrating) Vibrate(frequency, amplitude);
        _wasVibrating = amplitude > 0f;
    }

    private void OnDisable()
    {
        Vibrate(0f, 0f);
        _wasVibrating = false;
    }

    private void Vibrate(float frequency01, float amplitude01)
    {
        if (_controller != Target.Right) OVRInput.SetControllerVibration(frequency01, amplitude01, OVRInput.Controller.LTouch);
        if (_controller != Target.Left) OVRInput.SetControllerVibration(frequency01, amplitude01, OVRInput.Controller.RTouch);
    }
}
