using UnityEngine;

/// <summary>
/// "Cam giac" cua mot vat khi cham vao: lun bao nhieu thi luc bao nhieu, va
/// cac xung rung luc cham / cam / tha trong ra sao. Gan cung object voi
/// SquishyPinchable. Khong gan = dung gia tri mac dinh (qua bong mem).
///
/// Tach rieng khoi SquishyPinchable de cung 1 kieu bien dang co the mang nhieu
/// cam giac khac nhau (bong cao su, bong xop, qua cam...) -- chinh trong
/// Inspector, khong can sua code.
/// </summary>
public class HapticMaterial : MonoBehaviour
{
    [Header("Lực liên tục theo độ lún")]
    [Tooltip("Luc (0..1) khi lun DEN MUC TOI DA cua vat. Trong khoang lun, luc tang deu (nhu lo xo). Thap = vat mem, cao = vat cung.")]
    [Range(0f, 1f)]
    [SerializeField] private float _pressureAtMaxIndent = 0.6f;
    [Tooltip("Sau khi vat da lun toi da ma ngon THAT van an tiep (vat 'cham day'), luc tang tiep tu muc tren len 1.0 trong khoang nay (met). Nho = cam giac cham day dot ngot, cung.")]
    [SerializeField] private float _extraDepthToFullPressure = 0.01f;

    [Header("Xung rung khi chạm")]
    [Tooltip("Xung luc da ngon vua cham be mat. Bien do nay ung voi cu cham NHANH; cham cham thi xung nho lai (xem ben duoi).")]
    [SerializeField] private HapticTransient _touch = new HapticTransient(0.8f, 120f, 0.025f);
    [Tooltip("Toc do an vao (m/s) de xung cham dat bien do day du. Cham cham hon -> xung nho hon, giong that: go manh thi keu to.")]
    [SerializeField] private float _speedForFullTouch = 0.3f;
    [Tooltip("Bien do toi thieu (theo ti le) cua xung cham, de cham rat nhe van con cam nhan duoc.")]
    [Range(0f, 1f)]
    [SerializeField] private float _minTouchScale = 0.3f;
    [SerializeField] private HapticTransient _untouch = new HapticTransient(0.2f, 80f, 0.02f);

    [Header("Xung rung khi cầm / thả")]
    [SerializeField] private HapticTransient _grab = new HapticTransient(0.5f, 100f, 0.04f);
    [SerializeField] private HapticTransient _release = new HapticTransient(0.3f, 80f, 0.03f);

    /// <summary>Luc (0..1) khi lun den muc toi da -- doi duoc luc chay (thi nghiem).</summary>
    public float PressureAtMaxIndent
    {
        get => _pressureAtMaxIndent;
        set => _pressureAtMaxIndent = Mathf.Clamp01(value);
    }

    private static HapticMaterial _default;

    /// <summary>Gia tri mac dinh cho vat khong gan HapticMaterial.</summary>
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

    /// <summary>Doi do an sau cua da ngon (met) thanh luc 0..1.</summary>
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

    /// <summary>Xung cua su kien; rieng xung cham co bien do theo toc do an vao.</summary>
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
