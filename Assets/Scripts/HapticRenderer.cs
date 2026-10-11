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
    [Tooltip("Khoi cung (khong lun). De trong = moi PhysicsPinchGrabbable trong scene. Do cung lay tu HapticMaterial tren khoi: " +
             "Pressure At Max Indent = 0, Extra Depth To Full Pressure = so met ngon that lan vao thi luc dat toi da.")]
    [SerializeField] private PhysicsPinchGrabbable[] _cubes;

    [Header("Thiết bị đầu ra (mọi component có IHapticGlove)")]
    [SerializeField] private MonoBehaviour[] _outputs;

    [Header("Chống nhấp nháy ở mép bề mặt")]
    [Tooltip("Da ngon phai roi xa be mat hon khoang nay (met) moi tinh la 'roi'. Tranh cham/roi lien tuc khi ngon run o ngay mep.")]
    [SerializeField] private float _untouchHysteresis = 0.002f;

    [Header("Kẹp hai ngón")]
    [Tooltip("Vat dang bi CAM giua ngon cai va ngon tro: hai ngon nhan CUNG muc luc, nguoc chieu nhau theo duong noi hai dau ngon " +
             "-- vat dung yen giua hai ngon thi luc hai ben phai bang nhau. Do lun chung = trung binh do lun hai ngon = nua do bop, " +
             "khong phu thuoc vat dang lech ve ngon nao (tracking dat ngon tro hut vai mm thi ngon tro van cham cung luc ngon cai). " +
             "Tat = moi ngon tinh rieng theo do lun cua no, huong tu tam vat ra dau ngon.")]
    [SerializeField] private bool _equalPinchForces = true;

    /// <summary>Trang thai hien tai cua 1 ngon -- de hien thi / ghi log.</summary>
    [Serializable]
    public struct FingerState
    {
        public bool touching;
        public float depthMeters;
        public float pressure01;
        public SquishyPinchable touchedObject;
        [Tooltip("Luc vat day nguoc len ngon (Newton, toa do the gioi): huong theo phap tuyen be mat, do lon = muc luc x MaxForceNewton cua vat.")]
        public Vector3 forceNewton;
    }

    [Header("Chỉ đọc (xem trong Play mode)")]
    [SerializeField] private FingerState[] _state = new FingerState[2];

    /// <summary>Phat ra moi khi co su kien -- cho log / thi nghiem. Tham so cuoi
    /// la vat lien quan (co the null neu khong xac dinh duoc).</summary>
    public event Action<HapticFinger, HapticEventType, HapticTransient, SquishyPinchable> WhenHapticEvent;

    public FingerState GetState(HapticFinger finger) => _state[(int)finger];

    private readonly float[] _previousDepth = new float[2];
    private readonly Component[] _touchedTarget = new Component[2]; // bong hoac khoi dang cham -- vat lieu cho xung Untouch
    private IHapticGlove[] _gloves;
    private SquishyPinchable[] _heldLastFrame = Array.Empty<SquishyPinchable>();

    private void LateUpdate() => Tick(Time.deltaTime);

    /// <summary>Mot buoc tinh. Public de chay thu ngoai Play mode.</summary>
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

    /// <summary>Do lun cua 1 dau ngon vao vat SAU NHAT ma no an vao (bong HOAC khoi).</summary>
    private struct Contact
    {
        public bool valid;              // dau ngon dang co (dang theo doi duoc)
        public float depth;             // met, + = trong vat
        public SquishyPinchable obj;    // bong
        public PhysicsPinchGrabbable cube; // khoi cung
        public Vector3 normal;          // huong vat day ngon ra; zero = hoi vat (ContactNormal)

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

    /// <summary>Vat dang bi cam giua 2 ngon: ca 2 ngon lay do lun TRUNG BINH (= nua do bop: tong do lun = 2*ban kinh -
    /// khoang cach 2 dau ngon khi 2 ngon o 2 phia doi dien), luc huong theo duong noi 2 dau ngon, day moi ngon ra xa ngon kia.
    /// Huong tu tam vat ra dau ngon (ContactNormal) lat chieu khi dau ngon lun gan toi tam -- duong noi 2 ngon thi khong.</summary>
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
        Vector3 axis = _thumbTip.position - _indexTip.position; // ngon tro -> ngon cai
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
            // Toc do an vao ngay luc cham: cham nhanh -> xung manh
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
        // Khoi cung: do lun toi da = 0 -> luc tang tu 0 len toi da trong Extra Depth To Full Pressure cua vat lieu.
        float maxIndent = deepest != null ? deepest.MaxIndentMeters : 0f;
        state.pressure01 = touching && target != null
            ? MaterialOf(target).PressureFromDepth(depth, maxIndent)
            : 0f;
        // Vector luc (god-object): vat day ngon RA NGOAI theo phap tuyen, do lon ti le muc luc.
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
