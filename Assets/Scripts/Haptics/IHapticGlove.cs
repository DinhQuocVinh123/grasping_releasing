using UnityEngine;

/// <summary>Fingers that have a haptic actuator on the glove (the current glove has thumb and index only).</summary>
public enum HapticFinger
{
    Thumb = 0,
    Index = 1,
}

/// <summary>Contact events. Each event comes with one short vibration pulse (a transient).</summary>
public enum HapticEventType
{
    Touch,    // fingertip has just touched the object's surface
    Untouch,  // fingertip has just left the surface
    Grab,     // object is now held (squeezed from both sides)
    Release,  // object has just been let go
}

/// <summary>A short vibration pulse, a decaying sine:
/// amplitude * e^(-t / duration) * sin(2 * pi * frequency * t).
/// Each glove driver decides how to reproduce it on its own hardware.</summary>
[System.Serializable]
public struct HapticTransient
{
    [Range(0f, 1f)] public float amplitude;
    [Tooltip("Frequency (Hz). Low = soft contact, high = hard contact.")]
    public float frequencyHz;
    [Tooltip("Time (s) for the pulse to decay.")]
    public float durationSeconds;

    public HapticTransient(float amplitude, float frequencyHz, float durationSeconds)
    {
        this.amplitude = amplitude;
        this.frequencyHz = frequencyHz;
        this.durationSeconds = durationSeconds;
    }

    public HapticTransient Scaled(float factor) =>
        new HapticTransient(Mathf.Clamp01(amplitude * factor), frequencyHz, durationSeconds);
}

/// <summary>
/// Common interface for every haptic device: the glove, the Quest controller vibration, the debug output.
/// HapticRenderer only talks to this interface, so a new device only needs a new class that implements it,
/// without touching the force computation.
///
/// Two kinds of signal, after Kuchenbecker et al. (IEEE TVCG 2006): a CONTINUOUS force that follows the
/// penetration depth, plus a SHORT PULSE at the moment of contact. With the continuous force alone a
/// virtual object feels spongy; adding the pulse makes it feel much more real.
/// </summary>
public interface IHapticGlove
{
    /// <summary>Called EVERY FRAME. 0 = no contact, 1 = maximum force.</summary>
    void SetPressure(HapticFinger finger, float pressure01);

    /// <summary>Called once per contact event.</summary>
    void PlayTransient(HapticFinger finger, HapticEventType type, HapticTransient transient);
}
