using Oculus.Interaction;
using UnityEngine;

/// <summary>
/// Giu ngon tay AO khong xuyen vao trong vat bop duoc (SquishyPinchable).
///
/// Tay that khep lai duoc vi ngoai doi khong co vat that chan -- nhung tay
/// ao se dung lai dung tren be mat (ke ca khi be mat dang lun). Moi khung
/// hinh, SAU KHI tay ao da duoc dat dang (boi HandVisual / FingerUDPReceiver),
/// script kiem tra tung ngon: neu ngon lot vao trong vat thi CHOT dang ngon
/// (dang ngay truoc khi cham, duoi thang them) va chi xoay CA NGON quanh khop
/// goc cho toi khi nam vua tren be mat -- nhu ngon that bi vat chan lai. Uon
/// tung khop (cach cu) lam ngon gap/be nguoc cong venh khi bop tay that sau.
///
/// Chi sua HINH ANH tay ao. Du lieu tay (vi tri ngon that) giu nguyen, nen vat
/// van biet ban dang bop chat co nao -> van lun dung muc.
///
/// Gan vao object chua HandVisual cua ban tay (vd OVRHandVisualLeft, hoac
/// StaticHandModel_Right cho tay gang). Xuong duoc tim theo ten XRHand_*.
/// </summary>
[DefaultExecutionOrder(200)] // sau FingerUDPReceiver (0) va SquishyPinchable (100)
public class FingertipSurfaceConstraint : MonoBehaviour
{
    [Tooltip("HandVisual cua ban tay nay. Dung de chay NGAY SAU khi no ghi xong dang tay (tranh bi ghi de). De trong = tu tim tren cung object.")]
    [SerializeField] private HandVisual _handVisual;
    [Tooltip("Nhung vat ma ban tay nay KHONG DUOC xuyen vao. De trong = moi vat bop duoc trong scene.\n\n" +
             "Tay gan theo dau (tay gang) duoc do 'theo goc nhin', tay tran do bang vi tri 3D that -- vat tu phan biet, khong can chia danh sach theo tay.")]
    [SerializeField] private SquishyPinchable[] _objects;
    [Tooltip("Chi xu ly ngon cai + ngon tro (2 ngon dung de bop). Tat = xu ly ca 5 ngon.")]
    [SerializeField] private bool _thumbAndIndexOnly = false;
    [Tooltip("Transform dau ngon cai/tro ma vat DOC de biet tay nay dang cam (vd LeftThumbTip cua IsdkFingertipProxy). " +
             "De trong = chinh XRHand_ThumbTip/IndexTip cua ban tay nay (tay gang). Khi dang cam, 2 dau ngon nay duoc dat nam tren be mat vat.")]
    [SerializeField] private Transform _dataThumbTip;
    [SerializeField] private Transform _dataIndexTip;
    [Tooltip("Vat CUNG (PhysicsPinchGrabbable): kiem tra ca TUNG DOT ngon (khong chi dau ngon) -- ngon ao khong cam vao vat o bat ky dau. " +
             "Ban kinh = do day ngon tay (met).")]
    [SerializeField] private float _fingerRadius = 0.008f;
    [Tooltip("So diem kiem tra tren moi dot ngon (ke ca diem cuoi dot).")]
    [Range(1, 10)]
    [SerializeField] private int _samplesPerBone = 6;
    [Tooltip("So vong lap xoay khop goc de day ngon ra khoi vat.")]
    [Range(1, 20)]
    [SerializeField] private int _wholeFingerPasses = 10;

    // Moi ngon: cac dot xuong xoay duoc (tu goc ra ngoai) + diem dau ngon.
    private static readonly string[][] ChainNames =
    {
        new[] { "XRHand_ThumbMetacarpal", "XRHand_ThumbProximal", "XRHand_ThumbDistal", "XRHand_ThumbTip" },
        new[] { "XRHand_IndexProximal", "XRHand_IndexIntermediate", "XRHand_IndexDistal", "XRHand_IndexTip" },
        new[] { "XRHand_MiddleProximal", "XRHand_MiddleIntermediate", "XRHand_MiddleDistal", "XRHand_MiddleTip" },
        new[] { "XRHand_RingProximal", "XRHand_RingIntermediate", "XRHand_RingDistal", "XRHand_RingTip" },
        new[] { "XRHand_LittleProximal", "XRHand_LittleIntermediate", "XRHand_LittleDistal", "XRHand_LittleTip" },
    };

    private Transform[][] _chains;

    /// <summary>So ngon vua bi day ra khoi vat trong khung hinh gan nhat.</summary>
    public int ConstrainedFingerCount { get; private set; }

    private void Awake()
    {
        if (_handVisual == null) _handVisual = GetComponent<HandVisual>();
    }

    private void OnEnable()
    {
        if (_handVisual != null) _handVisual.WhenHandVisualUpdated += Apply;
    }

    private void OnDisable()
    {
        if (_handVisual != null) _handVisual.WhenHandVisualUpdated -= Apply;
    }

    // Luon chay them o LateUpdate, phong khi event cua HandVisual khong ban
    // ra (cung ly do voi FingerUDPReceiver). Chay 2 lan cung khong sao: lan
    // sau thay da ngon da nam tren be mat roi thi khong lam gi.
    private void LateUpdate() => Apply();

    [Header("Dang ngon khi cham vat (khong cong venh)")]
    [Tooltip("Luc vua cham vat, dang ngon (goc cac khop giua/dau) duoc GIU NGUYEN nhu ngay truoc khi cham, roi duoi thang them " +
             "theo ti le nay (0 = giu y dang cu, 1 = thang han). Sau do chi XOAY CA NGON quanh khop goc de nam tren be mat -- " +
             "bop tay that chat hon cung khong lam ngon ao gap/be nguoc.")]
    [Range(0f, 1f)]
    [SerializeField] private float _straightenOnContact = 0.6f;
    [Tooltip("Khop goc duoc xoay lech toi da bao nhieu do so voi tay that de day ngon ra khoi vat.")]
    [SerializeField] private float _maxRootCorrectionDeg = 70f;
    [Tooltip("Roi vat: ngon ao chuyen ve theo tay that trong khoang nay (giay).")]
    [SerializeField] private float _releaseBlendSeconds = 0.1f;

    private const int Bones = 3; // moi ngon 3 dot xoay duoc (+ diem dau ngon)

    private sealed class FingerState
    {
        public readonly Quaternion[] Free = new Quaternion[Bones];   // dang tay that gan nhat KHI CHUA CHAM
        public readonly Quaternion[] Frozen = new Quaternion[Bones]; // dang giu trong luc cham
        public readonly Quaternion[] Out = new Quaternion[Bones];    // dang vua ghi ra
        public bool HasFree, HasOut, Contact, Blending;
        // Vat cung dang cham + mat da chot: phan ngon lot vao vat luon day ra MAT NAY (khong phai
        // mat gan nhat -- gan canh hop mat gan nhat doi qua lai lam ngon nhay).
        public PhysicsPinchGrabbable FaceBody;
        public int Face = -1;
        public bool Held; // khung truoc ngon nay dang CAM vat
    }

    private FingerState[] _state;

    // Vi tri dau ngon theo TAY THAT (ngay truoc khi bi sua) -- cho vat doc khi quyet dinh cam/tha.
    private static readonly System.Collections.Generic.Dictionary<Transform, Vector3> s_trackedTip =
        new System.Collections.Generic.Dictionary<Transform, Vector3>();

    /// <summary>Vi tri dau ngon theo tay that lan xu ly gan nhat. Dau ngon khong bi script nay
    /// sua (vd diem du lieu tay trai) thi tra ve vi tri hien tai.</summary>
    public static Vector3 TrackedPosition(Transform tip)
    {
        if (tip == null) return Vector3.zero;
        return s_trackedTip.TryGetValue(tip, out Vector3 p) ? p : tip.position;
    }
    private readonly Quaternion[] _scratch = new Quaternion[Bones];

    public void Apply()
    {
        ConstrainedFingerCount = 0;
        System.Collections.Generic.IReadOnlyList<SquishyPinchable> objects =
            _objects != null && _objects.Length > 0 ? _objects : SquishyPinchable.Active;
        if (objects.Count == 0 && PhysicsPinchGrabbable.Active.Count == 0) return;
        EnsureChains();
        if (_state == null || _state.Length != _chains.Length)
        {
            _state = new FingerState[_chains.Length];
            for (int i = 0; i < _state.Length; i++) _state[i] = new FingerState();
        }

        int count = _thumbAndIndexOnly ? 2 : _chains.Length;
        for (int f = 0; f < count; f++)
        {
            Transform[] chain = _chains[f];
            if (chain == null) continue;
            FingerState st = _state[f];

            // Ham nay chay 2 lan moi khung (event HandVisual + LateUpdate). Neu dang ngon van
            // la dang minh vua ghi (chua ai dat lai) thi da xu ly roi -- bo qua.
            if (st.HasOut && SameAsOut(chain, st)) continue;

            Transform tip = chain[Bones];
            if (f < 2) s_trackedTip[tip] = tip.position; // dang ngon luc nay = tay that vua ghi
            // Ngon cai / tro cua tay DANG CAM: luon dat tren be mat (xuyen vao hay ho ra deu sua).
            Transform dataTip = f == 0 ? (_dataThumbTip != null ? _dataThumbTip : tip)
                              : f == 1 ? (_dataIndexTip != null ? _dataIndexTip : tip) : null;

            // 1. Tay THAT (dang vua duoc ghi) co cham vat nao khong?
            bool held = HeldTarget(chain, dataTip, objects, out _);
            bool contact = held || Penetrates(chain, objects);

            if (!contact)
            {
                st.Contact = false;
                st.Held = false;
                st.FaceBody = null;
                st.Face = -1;
                for (int j = 0; j < Bones; j++) st.Free[j] = chain[j].localRotation;
                st.HasFree = true;
                if (st.Blending) BlendBack(chain, st); // vua roi vat: chuyen muot ve tay that
                SaveOut(chain, st);
                continue;
            }

            // 2. Vua cham: chot dang ngon (dang tay that ngay truoc khi cham, duoi thang them)
            if (!st.Contact)
            {
                st.Contact = true;
                FreezeShape(chain, st);
            }

            // 3. Giu dang da chot cho cac khop giua/dau; khop goc bat dau tu tay that roi
            //    xoay ca ngon ra khoi vat.
            //    Dang CAM lien tuc: bat dau tu tu the ngon khung truoc (khong phai tay that) -> ngon
            //    nam yen tren mat vat, chi xoay toi thieu theo diem cham; nhieu tracking ngon that
            //    (ngon cai hay dao dong) khong con lam ngon ao dong mo.
            Quaternion trackedRoot = chain[0].localRotation;
            bool keepPose = held && st.Held && st.HasOut;
            if (keepPose) chain[0].localRotation = st.Out[0];
            for (int j = 1; j < Bones; j++) chain[j].localRotation = st.Frozen[j];
            LockFace(chain, dataTip, st);
            SolveRoot(chain, dataTip, objects, st);
            if (!held) ClampRoot(chain, trackedRoot); // dang cam: diem dich nam tren mat vat, khong can gioi han
            st.Held = held;

            st.Blending = true;
            ConstrainedFingerCount++;
            SaveOut(chain, st);
        }
    }

    /// <summary>Dang cam: diem tren be mat ma dau ngon nen nam (ke ca khi dang ho ra).</summary>
    private static bool HeldTarget(Transform[] chain, Transform dataTip,
        System.Collections.Generic.IReadOnlyList<SquishyPinchable> objects, out Vector3 target)
    {
        target = default;
        if (dataTip == null) return false;
        Vector3 tipPos = chain[Bones].position;
        foreach (var obj in objects)
            if (obj != null && obj.isActiveAndEnabled && obj.TryGetHeldContact(dataTip, tipPos, out target)) return true;
        foreach (var body in PhysicsPinchGrabbable.Active)
            if (body != null && body.TryGetHeldContact(dataTip, tipPos, out target)) return true;
        return false;
    }

    /// <summary>Co phan nao cua ngon lot vao vat khong. Bong mem: chi xet dau ngon (bong lun
    /// duoc); vat cung: xet doc ca ngon.</summary>
    private bool Penetrates(Transform[] chain, System.Collections.Generic.IReadOnlyList<SquishyPinchable> objects)
    {
        foreach (var obj in objects)
            if (obj != null && obj.isActiveAndEnabled && obj.TryResolvePenetration(chain[Bones], out _)) return true;
        foreach (var body in PhysicsPinchGrabbable.Active)
            if (body != null && DeepestPoint(chain, body, -1, out _, out _) > 0f) return true;
        return false;
    }

    /// <summary>Chot mat vat cung cho ngon nay: dang cam -> mat vat da chot luc cam; vua cham ->
    /// mat gan diem lot sau nhat, giu nguyen cho toi khi roi vat.</summary>
    private void LockFace(Transform[] chain, Transform dataTip, FingerState st)
    {
        foreach (var body in PhysicsPinchGrabbable.Active)
        {
            if (body == null) continue;
            int held = body.HeldFace(dataTip);
            if (held >= 0) { st.FaceBody = body; st.Face = held; return; }
        }
        if (st.FaceBody != null && st.FaceBody.isActiveAndEnabled) return;
        st.FaceBody = null;
        st.Face = -1;
        foreach (var body in PhysicsPinchGrabbable.Active)
            if (body != null && DeepestPoint(chain, body, -1, out Vector3 q, out _) > 0f)
            {
                st.FaceBody = body;
                st.Face = body.FaceOf(q);
                return;
            }
    }

    /// <summary>Diem lot sau nhat doc ngon vao vat cung. Tra ve do sau (met), 0 = khong lot.</summary>
    private float DeepestPoint(Transform[] chain, PhysicsPinchGrabbable body, int face, out Vector3 point, out Vector3 resolved)
    {
        point = resolved = default;
        float deepest = 0f;
        for (int j = 0; j < Bones; j++)
        {
            for (int k = 1; k <= _samplesPerBone; k++)
            {
                Vector3 q = Vector3.Lerp(chain[j].position, chain[j + 1].position, (float)k / _samplesPerBone);
                if (!body.ResolvePoint(q, _fingerRadius, face, out Vector3 r)) continue;
                float d = (r - q).sqrMagnitude;
                if (d > deepest) { deepest = d; point = q; resolved = r; }
            }
        }
        return Mathf.Sqrt(deepest);
    }

    /// <summary>Xoay CA NGON quanh khop goc (dang ngon giu nguyen) cho toi khi dau ngon nam tren
    /// be mat (dang cam) va khong phan nao con lot vao vat.</summary>
    private void SolveRoot(Transform[] chain, Transform dataTip,
        System.Collections.Generic.IReadOnlyList<SquishyPinchable> objects, FingerState st)
    {
        Transform root = chain[0];
        for (int iter = 0; iter < _wholeFingerPasses; iter++)
        {
            bool moved = false;
            if (HeldTarget(chain, dataTip, objects, out Vector3 held))
                moved |= RotateRoot(root, chain[Bones].position, held);
            foreach (var obj in objects)
                if (obj != null && obj.isActiveAndEnabled && obj.TryResolvePenetration(chain[Bones], out Vector3 t))
                    moved |= RotateRoot(root, chain[Bones].position, t);
            foreach (var body in PhysicsPinchGrabbable.Active)
                if (body != null && DeepestPoint(chain, body, body == st.FaceBody ? st.Face : -1, out Vector3 q, out Vector3 r) > 0f)
                    moved |= RotateRoot(root, q, r);
            if (!moved) break;
        }
    }

    private static bool RotateRoot(Transform root, Vector3 from, Vector3 to)
    {
        Vector3 a = from - root.position, b = to - root.position;
        if (a.sqrMagnitude < 1e-10f || b.sqrMagnitude < 1e-10f || (to - from).sqrMagnitude < 1e-8f) return false;
        root.rotation = Quaternion.FromToRotation(a, b) * root.rotation;
        return true;
    }

    private void ClampRoot(Transform[] chain, Quaternion trackedRoot)
    {
        float angle = Quaternion.Angle(trackedRoot, chain[0].localRotation);
        if (angle > _maxRootCorrectionDeg)
            chain[0].localRotation = Quaternion.Slerp(trackedRoot, chain[0].localRotation, _maxRootCorrectionDeg / angle);
    }

    /// <summary>Chot dang ngon luc vua cham: dang tay that ngay truoc khi cham (neu co), roi
    /// duoi thang tung khop theo _straightenOnContact (tinh hinh hoc, khong can biet tu the nghi cua rig).</summary>
    private void FreezeShape(Transform[] chain, FingerState st)
    {
        for (int j = 0; j < Bones; j++) _scratch[j] = chain[j].localRotation;
        if (st.HasFree)
            for (int j = 1; j < Bones; j++) chain[j].localRotation = st.Free[j];

        for (int j = 1; j < Bones; j++)
        {
            Vector3 parentDir = chain[j].position - chain[j - 1].position;
            Vector3 dir = chain[j + 1].position - chain[j].position;
            if (parentDir.sqrMagnitude < 1e-10f || dir.sqrMagnitude < 1e-10f) continue;
            Quaternion straighten = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(dir, parentDir), _straightenOnContact);
            chain[j].rotation = straighten * chain[j].rotation;
        }
        for (int j = 0; j < Bones; j++) st.Frozen[j] = chain[j].localRotation;
        for (int j = 0; j < Bones; j++) chain[j].localRotation = _scratch[j];
    }

    private void BlendBack(Transform[] chain, FingerState st)
    {
        float k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(_releaseBlendSeconds, 1e-3f));
        bool done = true;
        for (int j = 0; j < Bones; j++)
        {
            Quaternion tracked = chain[j].localRotation;
            chain[j].localRotation = Quaternion.Slerp(st.Out[j], tracked, k);
            if (Quaternion.Angle(chain[j].localRotation, tracked) > 0.5f) done = false;
        }
        if (done) st.Blending = false;
    }

    private static void SaveOut(Transform[] chain, FingerState st)
    {
        for (int j = 0; j < Bones; j++) st.Out[j] = chain[j].localRotation;
        st.HasOut = true;
    }

    private static bool SameAsOut(Transform[] chain, FingerState st)
    {
        for (int j = 0; j < Bones; j++)
            if (Quaternion.Angle(chain[j].localRotation, st.Out[j]) > 0.01f) return false;
        return true;
    }

    /// <summary>Tim xuong theo ten. Tim lai neu bo xuong dang dung bi tat --
    /// HandVisual co the doi giua 2 bo xuong (OpenXR / Oculus) luc chay.</summary>
    private void EnsureChains()
    {
        if (_chains != null && _chains[0] != null && _chains[0][0].gameObject.activeInHierarchy) return;

        _chains = new Transform[ChainNames.Length][];
        for (int f = 0; f < ChainNames.Length; f++)
        {
            var chain = new Transform[ChainNames[f].Length];
            bool complete = true;
            for (int j = 0; j < chain.Length; j++)
            {
                chain[j] = FindActiveDeepChild(transform, ChainNames[f][j]);
                if (chain[j] == null) { complete = false; break; }
            }
            _chains[f] = complete ? chain : null;
        }
    }

    private static Transform FindActiveDeepChild(Transform parent, string name)
    {
        foreach (Transform child in parent)
        {
            if (!child.gameObject.activeInHierarchy) continue;
            if (child.name == name) return child;
            Transform result = FindActiveDeepChild(child, name);
            if (result != null) return result;
        }
        return null;
    }
}
