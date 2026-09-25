using System;
using UnityEngine;

/// <summary>
/// Tinh TIN HIEU HAPTIC cho ngon cai + ngon tro cua MOT ban tay, roi gui toi
/// moi thiet bi dau ra (IHapticGlove) -- gang that, rung tay cam, bang debug.
///
/// Moi khung hinh, voi tung ngon:
///   1. Luc LIEN TUC: da ngon THAT an sau bao nhieu vao vat. Theo phuong phap
///      god-object (Zilles &amp; Salisbury 1995): ngon ao bi giu tren be mat
///      (FingertipSurfaceConstraint), do lech giua ngon that va ngon ao chinh
///      la luc can tao ra.
///   2. SU KIEN: cham / roi / cam / tha -> moi su kien 1 xung rung ngan (Kuchenbecker
///      et al. 2006); xung cham manh hay nhe theo toc do an vao.
///
/// Tat ca tinh ngay tren kinh, tu va cham AO -- khong cho vong qua may tinh,
/// de do tre giua hinh va cam giac nho nhat co the.
/// </summary>
[DefaultExecutionOrder(150)] // sau SquishyPinchable (100), TRUOC FingertipSurfaceConstraint (200)
public class HapticRenderer : MonoBehaviour
{
    [Header("Đầu ngón THẬT (chưa bị đẩy ra khỏi vật)")]
    [Tooltip("Tay tran: LeftThumbTip (do IsdkFingertipProxy cap nhat). Tay gang: XRHand_ThumbTip -- script chay truoc FingertipSurfaceConstraint nen van doc duoc vi tri that.")]
    [SerializeField] private Transform _thumbTip;
    [SerializeField] private Transform _indexTip;

    [Header("Vật cảm nhận được")]
    [Tooltip("De trong = moi vat bop duoc trong scene.")]
    [SerializeField] private SquishyPinchable[] _objects;

    [Header("Thiết bị đầu ra (mọi component có IHapticGlove)")]
    [SerializeField] private MonoBehaviour[] _outputs;

    [Header("Chống nhấp nháy ở mép bề mặt")]
    [Tooltip("Da ngon phai roi xa be mat hon khoang nay (met) moi tinh la 'roi'. Tranh cham/roi lien tuc khi ngon run o ngay mep.")]
    [SerializeField] private float _untouchHysteresis = 0.002f;

    /// <summary>Trang thai hien tai cua 1 ngon -- de hien thi / ghi log.</summary>
    [Serializable]
    public struct FingerState
    {
        public bool touching;
        public float depthMeters;
        public float pressure01;
        public SquishyPinchable touchedObject;
    }

    [Header("Chỉ đọc (xem trong Play mode)")]
    [SerializeField] private FingerState[] _state = new FingerState[2];

    /// <summary>Phat ra moi khi co su kien -- cho log / thi nghiem. Tham so cuoi
    /// la vat lien quan (co the null neu khong xac dinh duoc).</summary>
    public event Action<HapticFinger, HapticEventType, HapticTransient, SquishyPinchable> WhenHapticEvent;

    public FingerState GetState(HapticFinger finger) => _state[(int)finger];

    private readonly float[] _previousDepth = new float[2];
    private IHapticGlove[] _gloves;
    private SquishyPinchable[] _heldLastFrame = Array.Empty<SquishyPinchable>();

    private void LateUpdate() => Tick(Time.deltaTime);

    /// <summary>Mot buoc tinh. Public de chay thu ngoai Play mode.</summary>
    public void Tick(float dt)
    {
        if (dt <= 0f) return;
        CacheOutputs();

        var objects = CurrentObjects();
        UpdateFinger(HapticFinger.Thumb, _thumbTip, objects, dt);
        UpdateFinger(HapticFinger.Index, _indexTip, objects, dt);
        UpdateGrabEvents(objects);
    }

    private void UpdateFinger(HapticFinger finger, Transform tip, System.Collections.Generic.IReadOnlyList<SquishyPinchable> objects, float dt)
    {
        int f = (int)finger;
        FingerState state = _state[f];

        // Tim vat ma da ngon an vao SAU NHAT
        float depth = float.NegativeInfinity;
        SquishyPinchable deepest = null;
        bool tipValid = tip != null && tip.gameObject.activeInHierarchy;
        if (tipValid)
        {
            foreach (var obj in objects)
            {
                if (obj == null || !obj.isActiveAndEnabled) continue;
                if (obj.TryGetSkinDepth(tip, out float d) && d > depth)
                {
                    depth = d;
                    deepest = obj;
                }
            }
        }

        bool touching = state.touching
            ? depth > -_untouchHysteresis
            : depth > 0f;

        if (touching && !state.touching)
        {
            // Toc do an vao ngay luc cham: cham nhanh -> xung manh
            float approachSpeed = float.IsInfinity(_previousDepth[f])
                ? 0f
                : Mathf.Max(0f, (depth - _previousDepth[f]) / dt);
            Emit(finger, HapticEventType.Touch, MaterialOf(deepest).TransientFor(HapticEventType.Touch, approachSpeed), deepest);
        }
        else if (!touching && state.touching)
        {
            Emit(finger, HapticEventType.Untouch, MaterialOf(state.touchedObject).TransientFor(HapticEventType.Untouch, 0f), state.touchedObject);
        }

        state.touching = touching;
        state.depthMeters = tipValid ? depth : 0f;
        state.touchedObject = touching ? deepest : null;
        state.pressure01 = touching && deepest != null
            ? MaterialOf(deepest).PressureFromDepth(depth, deepest.MaxIndentMeters)
            : 0f;
        _state[f] = state;
        _previousDepth[f] = tipValid ? depth : float.NegativeInfinity;

        foreach (var glove in _gloves) glove.SetPressure(finger, state.pressure01);
    }

    private void UpdateGrabEvents(System.Collections.Generic.IReadOnlyList<SquishyPinchable> objects)
    {
        // "Da cam" ve mat CAM GIAC = vat dang dinh tay VA ca 2 da ngon da thuc
        // su cham no. SquishyPinchable co vung "hut" (Grab Margin) nen co the
        // bat dinh khi ngon con cach be mat vai mm -- neu phat xung Grab ngay
        // luc do, gang se bao "da cam" truoc khi ngon cham vat.
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

    private static HapticMaterial MaterialOf(SquishyPinchable obj)
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
                else if (output != null) Debug.LogWarning($"{output.name}: {output.GetType().Name} khong implement IHapticGlove, bo qua.", this);
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
