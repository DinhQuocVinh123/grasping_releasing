using UnityEngine;

/// <summary>
/// "Gang gia" bang RUNG TAY CAM Quest -- de cam nhan tin hieu haptic TRUOC
/// khi gang that san sang. Cam tay cam o tay kia (hoac buoc vao co tay) trong
/// khi bop vat bang tay tran: Palmer et al. (IROS 2022) cho thay chuyen phan
/// hoi cua ngon cai/tro sang co tay van giup lam viec tot hon khong co gi.
///
/// Can bat "Simultaneous Hands And Controllers" (OVRManager) va Hand Tracking
/// Support = Controllers And Hands -- neu khong, cam tay cam len la Quest tat
/// theo doi tay.
///
/// Gioi han cua tay cam so voi gang: chi co 1 motor cho CA 2 ngon (lay ngon
/// manh hon), va motor phan hoi cham nen xung qua ngan (vai chuc ms) duoc
/// keo dai toi _minTransientSeconds de con cam nhan duoc.
/// </summary>
[DefaultExecutionOrder(300)] // sau HapticRenderer (150): gui lenh rung sau khi da co du lieu khung nay
public class ControllerVibrationOutput : MonoBehaviour, IHapticGlove
{
    public enum Target { Left, Right, Both }

    [SerializeField] private Target _controller = Target.Right;

    [Header("Lực liên tục")]
    [Tooltip("Luc lien tuc duoc nhan voi so nay truoc khi rung -- de rung nen yeu hon xung cham, xung cham moi noi bat.")]
    [Range(0f, 1f)]
    [SerializeField] private float _pressureGain = 0.6f;
    [Tooltip("Luc duoi muc nay thi khong rung (tranh rung rat nhe lien tuc khi ngon chi hoi sat be mat).")]
    [Range(0f, 0.5f)]
    [SerializeField] private float _pressureDeadzone = 0.05f;
    [Tooltip("Tan so rung nen cho luc lien tuc (0..1 cua dai tan so tay cam).")]
    [Range(0f, 1f)]
    [SerializeField] private float _pressureFrequency = 0.3f;

    [Header("Xung sự kiện")]
    [Tooltip("Xung ngan hon muc nay duoc keo dai ra -- motor tay cam can thoi gian de quay.")]
    [SerializeField] private float _minTransientSeconds = 0.06f;
    [Tooltip("Tan so (Hz) ung voi muc cao nhat cua tay cam, de doi tan so xung (Hz) sang thang 0..1.")]
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
        // Xung moi chi thay xung cu neu no manh hon phan con lai cua xung cu
        // (2 ngon cung phat Grab trong 1 khung -> khong bi dem 2 lan).
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
        // Tat dan theo ham mu, ve ~5% o cuoi xung
        return _transientAmplitude * Mathf.Exp(-3f * t / _transientDuration);
    }

    private void LateUpdate()
    {
        float pressure = Mathf.Max(_pressure[0], _pressure[1]);
        float continuous = pressure > _pressureDeadzone ? pressure * _pressureGain : 0f;
        float transient = CurrentTransientEnvelope();

        float amplitude = Mathf.Max(continuous, transient);
        float frequency = transient > continuous ? _transientFrequency01 : _pressureFrequency;

        // Chi gui lenh khi dang rung, hoac 1 lan de TAT khi vua het rung.
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
