using UnityEngine;

/// <summary>
/// "Gang gia" don gian nhat: nhan tin hieu haptic va hien ra de kiem tra --
/// luc hien tai tung ngon (xem trong Inspector luc Play) va ghi moi su kien
/// ra Console. Khong can phan cung gi.
/// </summary>
public class HapticDebugOutput : MonoBehaviour, IHapticGlove
{
    [Tooltip("Ghi moi su kien (cham/roi/cam/tha) ra Console.")]
    [SerializeField] private bool _logEvents = true;

    [Header("Chỉ đọc (xem trong Play mode)")]
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
