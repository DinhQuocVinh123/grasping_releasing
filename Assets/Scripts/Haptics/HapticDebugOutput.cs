using UnityEngine;

/// <summary>
/// The simplest haptic output: no hardware, it just shows the haptic signals so they can be checked.
/// The current pressure of each finger is visible in the Inspector during Play, and every contact
/// event is written to the Console.
/// </summary>
public class HapticDebugOutput : MonoBehaviour, IHapticGlove
{
    [Tooltip("Write every event (touch / untouch / grab / release) to the Console.")]
    [SerializeField] private bool _logEvents = true;

    [Header("Read only (watch in Play mode)")]
    [Range(0f, 1f)] [SerializeField] private float _thumbPressure;
    [Range(0f, 1f)] [SerializeField] private float _indexPressure;
    [SerializeField] private int _eventCount;
    [SerializeField] private string _lastEvent;

    public float ThumbPressure => _thumbPressure;
    public float IndexPressure => _indexPressure;
    public int EventCount => _eventCount;
    public string LastEvent => _lastEvent;

    public void SetPressure(HapticFinger finger, float pressure01)
    {
        if (finger == HapticFinger.Thumb) _thumbPressure = pressure01;
        else _indexPressure = pressure01;
    }

    public void PlayTransient(HapticFinger finger, HapticEventType type, HapticTransient transient)
    {
        _eventCount++;
        _lastEvent = $"{type} {finger} amp={transient.amplitude:F2} {transient.frequencyHz:F0}Hz {transient.durationSeconds * 1000f:F0}ms";
        if (_logEvents) Debug.Log($"[Haptic] {_lastEvent}", this);
    }
}
