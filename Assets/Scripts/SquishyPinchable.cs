using UnityEngine;

/// <summary>
/// Vat the "bop duoc": khi dau ngon cai + dau ngon tro ao an vao be mat,
/// cac dinh (vertex) gan dau ngon bi day vao trong -> tao vet lun; bop du
/// chat tu 2 phia doi dien thi vat DINH vao tay (di theo diem giua 2 dau
/// ngon), tha ra thi vat nay ve hinh cu nhu lo xo.
///
/// Gan vao bat ky object nao co MeshFilter (qua cau, khoi, do choi...).
/// Mesh can du nhieu dinh -- qua cau mac dinh cua Unity (515 dinh) la du,
/// khoi lap phuong mac dinh (24 dinh) se lun rat tho.
///
/// Nhieu ban tay cung tuong tac duoc voi 1 vat (danh sach _hands): tay nao
/// cung an lun duoc; tay nao kep dung cach thi cam (moi luc chi 1 tay cam,
/// tay kia van an lun duoc; tay dang cam tha ra thi tay kia co the do lay).
///
/// Gia dinh scale DEU 3 truc (x = y = z) de doi don vi local <-> met.
///
/// Khong can FingerUDPReceiver gui them gi: script chi doc vi tri 2 dau ngon
/// ao SAU KHI FingerUDPReceiver da xoay xuong xong trong khung hinh nay
/// (DefaultExecutionOrder lon hon -> chay sau LateUpdate cua receiver).
/// </summary>
[DefaultExecutionOrder(100)]
[RequireComponent(typeof(MeshFilter))]
public class SquishyPinchable : MonoBehaviour
{
    /// <summary>1 ban tay: dau ngon cai + dau ngon tro.</summary>
    [System.Serializable]
    public struct Hand
    {
        [Tooltip("Tay tran: LeftThumbTip (do IsdkFingertipProxy cap nhat). Tay gang: XRHand_ThumbTip.")]
        public Transform thumbTip;
        public Transform indexTip;
    }

    [Header("Các bàn tay tương tác được")]
    [Tooltip("Moi tay trong danh sach deu an lun va cam duoc vat. De trong = tu tim XRHand_ThumbTip / XRHand_IndexTip trong ban tay co FingerUDPReceiver.")]
    [SerializeField] private Hand[] _hands;

    // Cach cu (moi vat chi 1 tay) -- giu lai de doc scene cu, tu chuyen sang _hands.
    [SerializeField, HideInInspector] private Transform _thumbTip;
    [SerializeField, HideInInspector] private Transform _indexTip;

    [Header("Tay gắn theo đầu (tay găng) -- đo theo góc nhìn")]
    [Tooltip("Chi ap dung cho tay GAN THEO DAU (con cua _viewReference, vd tay gang StaticHandModel_Right). " +
             "Tay tran do Quest theo doi co vi tri 3D that nen luon do bang khoang cach 3D, khong bi anh huong.\n\n" +
             "BAT (nen dung): bo qua chieu sau khi do dau ngon co cham vat khong -- keo 2 dau ngon ve CUNG DO SAU voi vat roi moi tinh.\n\n" +
             "Ly do: du lieu tu camera chi co 2D, va khi tat Preserve Rest Depth moi ngon ao nam phang o do sau cua goc ngon do -- goc ngon cai " +
             "gan mat hon goc ngon tro ~5 cm. Nhin tu mat thi 2 dau ngon cham nhau, nhung trong 3D chung van lech nhau ~5 cm theo chieu sau, " +
             "nen neu do khoang cach 3D thi vat khong bao gio thay minh bi kep.")]
    [SerializeField] private bool _measureInViewPlane = true;
    [Tooltip("Transform dai dien cho mat nguoi xem. De trong = parent cua ban tay (CenterEyeAnchor).")]
    [SerializeField] private Transform _viewReference;

    [Header("Độ lún")]
    [Tooltip("Ban kinh dau ngon (met). Diem 'dau ngon' cua mo hinh tay nam o TRONG ngon, da ngon nam cach no khoang nay -- " +
             "vat bat dau lun khi DA ngon cham be mat, va FingertipSurfaceConstraint giu da ngon nam dung tren be mat.")]
    [SerializeField] private float _fingerRadius = 0.008f;
    [Tooltip("Do lun TOI DA, tinh theo phan cua ban kinh vat (0.25 = lun toi da 1/4 ban kinh). Nho = vat cung, lon = vat mem.")]
    [Range(0.02f, 0.6f)]
    [SerializeField] private float _maxIndent = 0.25f;
    [Tooltip("Do rong cua vet lun, theo phan cua ban kinh. Nho = vet lun nhon nhu an bang dau but, lon = vet lun rong va tron.")]
    [Range(0.1f, 1.5f)]
    [SerializeField] private float _contactSpread = 0.45f;
    [Tooltip("Vat phinh ra 2 ben khi bi bop (nhu bong that giu the tich). 0 = khong phinh.")]
    [Range(0f, 1f)]
    [SerializeField] private float _bulge = 0.35f;
    [Tooltip("Do cung cua lo xo: vet lun bam theo dau ngon nhanh co nao, va nay ve nhanh co nao khi tha.")]
    [SerializeField] private float _stiffness = 300f;
    [Tooltip("Giam chan lo xo. Thap = nay rung rinh vai lan truoc khi dung, cao = ve tu tu khong rung.")]
    [SerializeField] private float _damping = 18f;

    [Header("Dính vào tay")]
    [Tooltip("Bat dau cam khi CA HAI dau ngon cach be mat vat khong qua khoang nay (met) va nam o 2 phia doi dien nhau. Lon hon = de bat hon, khong can chinh xac.")]
    [SerializeField] private float _grabMargin = 0.015f;
    [Tooltip("Tha ra khi 2 dau ngon mo rong hon duong kinh vat them khoang nay (met). Luon ap dung, du chon cach tha nao.")]
    [SerializeField] private float _releaseMargin = 0.01f;

    public enum ReleaseRule
    {
        WhenFingersOpen,     // tha ngay khi 2 ngon bat dau mo ra (khuyen dung)
        WhenWiderThanObject, // chi tha khi 2 ngon mo rong hon ca vat (cach cu)
    }

    [Tooltip("WhenFingersOpen: tha khi 2 ngon MO RA them _releaseOpening so voi luc bop CHAT NHAT -- theo Prachyabrued & Borst (IJHCS 2012). " +
             "Ngon that da lot sau vao trong vat (khong co gi chan), nen neu doi 2 ngon mo rong hon ca vat moi tha thi phai mo tay rat xa, vat 'dinh mai khong roi'.\n\n" +
             "WhenWiderThanObject: cach cu, giu lai de so sanh.")]
    [SerializeField] private ReleaseRule _releaseRule = ReleaseRule.WhenFingersOpen;
    [Tooltip("Voi WhenFingersOpen: 2 ngon mo ra them bao nhieu (met) so voi luc bop chat nhat thi tha. Nho = tha nhay, lon = kho roi hon nhung it bi tha nham khi tay run.")]
    [SerializeField] private float _releaseOpening = 0.01f;
    [Tooltip("Khi dang cam, vat truot ve diem giua 2 dau ngon nhanh co nao.")]
    [SerializeField] private float _followSpeed = 12f;

    [Header("Khi thả ra")]
    [Tooltip("Vi tri vat quay ve khi khong con bi cam. De trong = vat nam yen o cho vua tha.")]
    [SerializeField] private Transform _home;
    [SerializeField] private float _returnSpeed = 6f;

    /// <summary>Tat ca vat bop duoc dang bat trong scene -- de
    /// FingertipSurfaceConstraint biet can kiem tra nhung vat nao.</summary>
    public static readonly System.Collections.Generic.List<SquishyPinchable> Active =
        new System.Collections.Generic.List<SquishyPinchable>();

    /// <summary>Dang bi cam (dinh vao tay) hay khong.</summary>
    public bool IsHeld => _heldBy >= 0;

    /// <summary>Muc lun hien tai 0..1 (1 = lun toi da). De sau nay gui sang
    /// gang haptic lam luc phan hoi.</summary>
    public float Squish01 { get; private set; }

    /// <summary>Ngon THAT dang an VUOT qua muc lun toi da bao nhieu (met) --
    /// tuc la phan ngon that da lot vao trong vat ma nguoi dung khong nhin
    /// thay (ngon ao bi giu tren be mat). 0 = khong vuot.</summary>
    public float OvershootMeters { get; private set; }

    private Mesh _mesh;
    private Vector3[] _baseVertices;
    private Vector3[] _deformed;
    private Vector3 _center;  // tam vat trong he local
    private float _radius;    // ban kinh trung binh trong he local

    // Mesh thuong co "duong noi" (seam): cung 1 diem nhung luu thanh nhieu
    // dinh trung nhau (de dan anh be mat). RecalculateNormals tinh rieng
    // tung dinh nen khi lun, 2 ben duong noi sang toi khac nhau -> hien ra
    // vet nut. Luu lai cac nhom dinh trung nhau de lay trung binh normal.
    private int[][] _seamGroups;
    private Vector3[] _normals;

    // Moi dau ngon la mot "diem an": huong an, do lun hien tai, van toc lo xo.
    // Tay h co 2 diem: 2h (ngon cai) va 2h+1 (ngon tro).
    private Vector3[] _pressDir = new Vector3[0];
    private float[] _pressSurface = new float[0];
    private float[] _indent = new float[0];
    private float[] _indentVel = new float[0];
    private bool _meshDirty;

    private int _heldBy = -1; // tay dang cam (-1 = khong ai cam)
    private Vector3 _grabAxis;
    private Quaternion _grabRotation;
    private float[] _tightestGap = new float[0]; // moi tay: khoang cach 2 dau ngon nho nhat tu luc bat dau cam
    private bool[] _regrabBlocked = new bool[0];

    // Bo dem dung lai moi khung hinh (tranh tao mang moi)
    private Vector3[] _points = new Vector3[0];
    private bool[] _handValid = new bool[0];

    private void Awake()
    {
        MigrateLegacyHand();
        if (_hands.Length == 0)
        {
            Transform thumb = FindInScene("XRHand_ThumbTip");
            Transform index = FindInScene("XRHand_IndexTip");
            if (thumb != null && index != null) _hands = new[] { new Hand { thumbTip = thumb, indexTip = index } };
        }
        if (_viewReference == null)
        {
            var receiver = FindAnyObjectByType<FingerUDPReceiver>();
            if (receiver != null) _viewReference = receiver.transform.parent;
        }
        EnsureInitialized();
    }

    private void OnValidate() => MigrateLegacyHand();

    /// <summary>Scene cu chi co 1 cap _thumbTip/_indexTip -> chuyen thanh tay dau tien.</summary>
    private void MigrateLegacyHand()
    {
        if (_hands == null) _hands = new Hand[0];
        if (_hands.Length == 0 && (_thumbTip != null || _indexTip != null))
        {
            _hands = new[] { new Hand { thumbTip = _thumbTip, indexTip = _indexTip } };
        }
        _thumbTip = null;
        _indexTip = null;
    }

    /// <summary>Cap phat trang thai theo so tay (doi so tay trong Inspector luc chay cung khong sao).</summary>
    private void EnsureHandState()
    {
        if (_hands == null) _hands = new Hand[0];
        int hands = _hands.Length;
        if (_handValid.Length == hands) return;

        int points = hands * 2;
        _pressDir = new Vector3[points];
        for (int k = 0; k < points; k++) _pressDir[k] = Vector3.up;
        _pressSurface = new float[points];
        _indent = new float[points];
        _indentVel = new float[points];
        _points = new Vector3[points];
        _tightestGap = new float[hands];
        _regrabBlocked = new bool[hands];
        _handValid = new bool[hands];
        _heldBy = -1;
    }

    private void OnEnable() => Active.Add(this);
    private void OnDisable() => Active.Remove(this);

    /// <summary>Tach mesh ra ban rieng (de khong lam hong mesh goc dung
    /// chung voi object khac) va luu lai hinh dang goc.</summary>
    public void EnsureInitialized()
    {
        if (_baseVertices != null) return;

        var filter = GetComponent<MeshFilter>();
        _mesh = Instantiate(filter.sharedMesh);
        _mesh.name = filter.sharedMesh.name + " (squishy)";
        _mesh.MarkDynamic();
        filter.sharedMesh = _mesh;

        _baseVertices = _mesh.vertices;
        _deformed = new Vector3[_baseVertices.Length];
        _center = _mesh.bounds.center;

        float sum = 0f;
        foreach (var v in _baseVertices) sum += (v - _center).magnitude;
        _radius = Mathf.Max(sum / Mathf.Max(_baseVertices.Length, 1), 1e-5f);

        BuildSeamGroups();
        EnsureHandState();
    }

    private void BuildSeamGroups()
    {
        var byPosition = new System.Collections.Generic.Dictionary<Vector3, System.Collections.Generic.List<int>>();
        for (int i = 0; i < _baseVertices.Length; i++)
        {
            // Lam tron de 2 dinh "trung nhau" nhung lech nhau sai so float van gom chung nhom
            Vector3 key = new Vector3(
                Mathf.Round(_baseVertices[i].x * 1e5f),
                Mathf.Round(_baseVertices[i].y * 1e5f),
                Mathf.Round(_baseVertices[i].z * 1e5f));
            if (!byPosition.TryGetValue(key, out var list))
            {
                list = new System.Collections.Generic.List<int>();
                byPosition[key] = list;
            }
            list.Add(i);
        }

        var groups = new System.Collections.Generic.List<int[]>();
        foreach (var list in byPosition.Values)
        {
            if (list.Count > 1) groups.Add(list.ToArray());
        }
        _seamGroups = groups.ToArray();
    }

    private void RecalculateSmoothNormals()
    {
        _mesh.RecalculateNormals();
        if (_seamGroups.Length == 0) return;

        _normals = _mesh.normals;
        foreach (var group in _seamGroups)
        {
            Vector3 avg = Vector3.zero;
            foreach (int i in group) avg += _normals[i];
            avg.Normalize();
            foreach (int i in group) _normals[i] = avg;
        }
        _mesh.normals = _normals;
    }

    private void LateUpdate() => Tick(Time.deltaTime);

    /// <summary>Doc vi tri dau ngon cua moi tay roi chay mot buoc. Public de
    /// chay thu ngoai Play mode.</summary>
    public void Tick(float dt)
    {
        EnsureInitialized();
        EnsureHandState();

        for (int h = 0; h < _hands.Length; h++)
        {
            Transform thumb = _hands[h].thumbTip;
            Transform index = _hands[h].indexTip;
            // Mat dau tay (Quest tat 2 transform) -> tay nay coi nhu da buong
            // ra: khong an lun, dang cam thi tha.
            _handValid[h] = thumb != null && index != null &&
                            thumb.gameObject.activeInHierarchy && index.gameObject.activeInHierarchy;
            if (!_handValid[h]) continue;
            _points[2 * h] = MeasurePoint(thumb);
            _points[2 * h + 1] = MeasurePoint(index);
        }

        StepHands(dt);
    }

    /// <summary>Tay gan theo dau: khong co chieu sau that -> do theo goc nhin.</summary>
    private bool IsHeadLocked(Transform tip) =>
        _measureInViewPlane && _viewReference != null && tip.IsChildOf(_viewReference);

    /// <summary>Vi tri dau ngon dung de tinh: tay tran = vi tri 3D that,
    /// tay gan theo dau = keo ve do sau cua vat (xem _measureInViewPlane).</summary>
    private Vector3 MeasurePoint(Transform tip) =>
        IsHeadLocked(tip) ? ToObjectDepth(tip.position) : tip.position;

    /// <summary>Truot diem doc theo TIA NHIN tu mat cho toi khi no cung do sau
    /// voi tam vat -- nhin tu mat thi diem khong he di chuyen. Phai di theo tia
    /// (khong di song song huong nhin) vi tay chi cach mat vai chuc cm, phoi
    /// canh rat manh: truot song song se lam diem lech han cho tren man hinh.</summary>
    private Vector3 ToObjectDepth(Vector3 worldPoint)
    {
        Vector3 eye = _viewReference.position;
        Vector3 forward = _viewReference.forward;
        float pointDepth = Vector3.Dot(worldPoint - eye, forward);
        float objectDepth = Vector3.Dot(transform.TransformPoint(_center) - eye, forward);
        if (pointDepth < 1e-4f || objectDepth < 1e-4f) return worldPoint;
        return eye + (worldPoint - eye) * (objectDepth / pointDepth);
    }

    // --- Cho thi nghiem -------------------------------------------------------

    /// <summary>Do lun toi da, theo phan cua ban kinh (vd 0.25).</summary>
    public float MaxIndentFraction
    {
        get => _maxIndent;
        set => _maxIndent = Mathf.Clamp(value, 0.02f, 0.6f);
    }

    /// <summary>Dua vat ve trang thai ban dau: khong bi cam, het lun, nam o
    /// cho _home (neu co). Dung khi bat dau moi luot thi nghiem.</summary>
    public void ResetState()
    {
        EnsureHandState();
        _heldBy = -1;
        for (int h = 0; h < _regrabBlocked.Length; h++) _regrabBlocked[h] = false;
        for (int k = 0; k < _indent.Length; k++)
        {
            _indent[k] = 0f;
            _indentVel[k] = 0f;
        }
        Squish01 = 0f;
        OvershootMeters = 0f;
        if (_home != null) transform.SetPositionAndRotation(_home.position, _home.rotation);
        if (_mesh != null) ApplyDeformation();
    }

    // --- Cho tang haptic ------------------------------------------------------

    /// <summary>Do lun toi da (met) -- de tang haptic chuan hoa luc.</summary>
    public float MaxIndentMeters => _maxIndent * _radius * transform.lossyScale.x;

    /// <summary>Da dau ngon (vi tri THAT, chua bi FingertipSurfaceConstraint day
    /// ra) dang an sau bao nhieu (met) vao be mat GOC cua vat. Duong = dang an
    /// vao, am = con cach be mat. Day chinh la do lech giua "ngon that" va
    /// "ngon dai dien nam tren be mat" trong phuong phap god-object -> luc.</summary>
    public bool TryGetSkinDepth(Transform tip, out float depthMeters)
    {
        depthMeters = float.NegativeInfinity;
        if (_baseVertices == null || tip == null) return false;

        depthMeters = -SurfaceDistance(MeasurePoint(tip), transform.lossyScale.x);
        return true;
    }

    /// <summary>Vat co dang bi CHINH ban tay chua dau ngon nay cam khong.
    /// Dau ngon khong thuoc tay nao trong _hands -> chi can vat dang bi cam.</summary>
    public bool IsHeldBy(Transform tip)
    {
        if (_heldBy < 0 || _hands == null) return false;
        for (int h = 0; h < _hands.Length; h++)
        {
            if (_hands[h].thumbTip == tip || _hands[h].indexTip == tip) return h == _heldBy;
        }
        return true;
    }

    // --- Chong ngon tay xuyen vao vat -----------------------------------------

    /// <summary>Kiem tra da dau ngon co dang lot vao TRONG be mat hien tai
    /// (da tinh ca vet lun) khong. Neu co, tra ve vi tri dau ngon moi sao cho
    /// da ngon vua cham be mat -- FingertipSurfaceConstraint se uon ngon tay
    /// ao toi do.</summary>
    public bool TryResolvePenetration(Transform tip, out Vector3 resolvedWorld)
    {
        resolvedWorld = tip != null ? tip.position : Vector3.zero;
        if (_baseVertices == null || tip == null) return false;
        Vector3 tipWorld = tip.position;

        // Voi tay gang (do theo goc nhin): dua dau ngon ve do sau cua vat,
        // tinh o do, roi dua nguoc ve do sau cu cua ngon.
        bool projected = IsHeadLocked(tip);
        Vector3 eye = Vector3.zero;
        float depthRatio = 1f;
        Vector3 p = tipWorld;
        if (projected)
        {
            eye = _viewReference.position;
            Vector3 forward = _viewReference.forward;
            float pointDepth = Vector3.Dot(tipWorld - eye, forward);
            float objectDepth = Vector3.Dot(transform.TransformPoint(_center) - eye, forward);
            if (pointDepth < 1e-4f || objectDepth < 1e-4f) return false;
            depthRatio = objectDepth / pointDepth;
            p = eye + (tipWorld - eye) * depthRatio;
        }

        Vector3 local = transform.InverseTransformPoint(p) - _center;
        float dist = local.magnitude;
        if (dist < 1e-6f) return false;
        Vector3 dir = local / dist;

        float minDist = CurrentSurfaceRadius(dir) + _fingerRadius * depthRatio / transform.lossyScale.x;
        if (dist >= minDist) return false;

        resolvedWorld = transform.TransformPoint(_center + dir * minDist);
        if (projected) resolvedWorld = eye + (resolvedWorld - eye) / depthRatio;
        return true;
    }

    /// <summary>Be mat HIEN TAI (sau khi lun) cach tam bao xa theo huong nay --
    /// cung cong thuc voi ApplyDeformation, chi tinh cho 1 huong.</summary>
    private float CurrentSurfaceRadius(Vector3 dir)
    {
        float surface = SupportRadius(dir);
        Vector3 p = _center + dir * surface;
        float sigma = _contactSpread * _radius;
        float inv2Sigma2 = 1f / (2f * sigma * sigma);
        float r = surface;

        for (int k = 0; k < _indent.Length; k++)
        {
            if (Mathf.Abs(_indent[k]) < 1e-6f) continue;
            Vector3 contact = _center + _pressDir[k] * _pressSurface[k];
            float w = Mathf.Exp(-(p - contact).sqrMagnitude * inv2Sigma2);
            r -= _indent[k] * w * Mathf.Max(0f, Vector3.Dot(dir, _pressDir[k]));
        }
        return r;
    }

    /// <summary>Mot buoc mo phong voi vi tri 2 dau ngon cua TAY DAU TIEN (toa
    /// do the gioi, khong chieu theo goc nhin); cac tay khac coi nhu buong ra.
    /// Public de co the chay thu ngoai Play mode.</summary>
    public void Step(Vector3 thumbWorld, Vector3 indexWorld, float dt)
    {
        EnsureInitialized();
        if (_hands.Length == 0)
        {
            _hands = new Hand[1];
            EnsureHandState();
        }
        for (int h = 0; h < _handValid.Length; h++) _handValid[h] = h == 0;
        _points[0] = thumbWorld;
        _points[1] = indexWorld;
        StepHands(dt);
    }

    /// <summary>Mot buoc mo phong voi _points / _handValid da dien san.</summary>
    private void StepHands(float dt)
    {
        if (dt <= 0f) return;

        UpdateGrab(dt);
        UpdateIndents(dt);
        ApplyDeformation();
    }

    // --- Dinh vao tay ---------------------------------------------------------

    private void UpdateGrab(float dt)
    {
        float scale = transform.lossyScale.x;
        Vector3 centerWorld = transform.TransformPoint(_center);

        // 1. Tay dang cam con giu tiep khong?
        if (_heldBy >= 0)
        {
            int h = _heldBy;
            bool release = !_handValid[h]; // mat dau tay -> tha
            if (!release)
            {
                float gap = Vector3.Distance(_points[2 * h], _points[2 * h + 1]);
                _tightestGap[h] = Mathf.Min(_tightestGap[h], gap);

                // 2 dau ngon da mo rong hon duong kinh vat -> khong con kep duoc nua.
                // Nguong THA phai rong hon nguong CAM (_grabMargin): neu khong, ngon
                // vua lot vao vung cam da nam ngoai vung tha -> cam roi tha ngay.
                bool widerThanObject = gap > 2f * (_radius * scale + _fingerRadius + _grabMargin + _releaseMargin);
                // 2 ngon da bat dau mo ra so voi luc bop chat nhat -> nguoi dung muon tha
                bool opening = _releaseRule == ReleaseRule.WhenFingersOpen && gap > _tightestGap[h] + _releaseOpening;
                release = widerThanObject || opening;
            }

            if (release)
            {
                _heldBy = -1;
                _regrabBlocked[h] = _handValid[h];
            }
        }

        // 2. Tay nao kep dung cach thi cam (neu chua ai cam)
        for (int h = 0; h < _handValid.Length; h++)
        {
            if (!_handValid[h])
            {
                _regrabBlocked[h] = false;
                continue;
            }

            Vector3 thumb = _points[2 * h];
            Vector3 index = _points[2 * h + 1];

            // Bat dau cam: ca 2 dau ngon deu sat be mat VA o 2 phia doi dien
            // nhau (cham 2 ngon cung 1 phia thi chi la cham, chua phai cam).
            bool opposite = Vector3.Dot(thumb - centerWorld, index - centerWorld) < 0f;
            bool canGrab = opposite &&
                           SurfaceDistance(thumb, scale) < _grabMargin &&
                           SurfaceDistance(index, scale) < _grabMargin;

            // Vua tha xong thi 2 ngon van con sat be mat -> neu khong chan, khung
            // hinh sau lai cam lai ngay. Chi cho cam lai khi ngon da ROI khoi
            // vung cam it nhat 1 lan.
            if (_regrabBlocked[h])
            {
                if (!canGrab) _regrabBlocked[h] = false;
            }
            else if (canGrab && _heldBy < 0)
            {
                _heldBy = h;
                _grabAxis = (index - thumb).normalized;
                _grabRotation = transform.rotation;
                _tightestGap[h] = Vector3.Distance(thumb, index);
            }
        }

        float follow = 1f - Mathf.Exp(-_followSpeed * dt);
        if (_heldBy >= 0)
        {
            // Vat truot ve giua 2 dau ngon, va xoay theo truc noi 2 dau ngon
            // (nhu vat bi kep that xoay theo tay).
            Vector3 thumb = _points[2 * _heldBy];
            Vector3 index = _points[2 * _heldBy + 1];
            Vector3 mid = (thumb + index) * 0.5f;
            Vector3 offset = transform.position - centerWorld; // tam mesh co the lech pivot
            transform.position = Vector3.Lerp(transform.position, mid + offset, follow);

            Vector3 axis = index - thumb;
            if (axis.sqrMagnitude > 1e-10f)
            {
                transform.rotation = Quaternion.FromToRotation(_grabAxis, axis.normalized) * _grabRotation;
            }
        }
        else if (_home != null)
        {
            float back = 1f - Mathf.Exp(-_returnSpeed * dt);
            transform.position = Vector3.Lerp(transform.position, _home.position, back);
            transform.rotation = Quaternion.Slerp(transform.rotation, _home.rotation, back);
        }
    }

    /// <summary>Khoang cach (met) tu DA dau ngon toi be mat vat, am = da ngon da an vao trong.</summary>
    private float SurfaceDistance(Vector3 worldPoint, float scale)
    {
        Vector3 local = transform.InverseTransformPoint(worldPoint) - _center;
        float dist = local.magnitude;
        if (dist < 1e-6f) return -_radius * scale;
        return (dist - SupportRadius(local / dist)) * scale - _fingerRadius;
    }

    // --- Do lun ---------------------------------------------------------------

    private void UpdateIndents(float dt)
    {
        float maxIndent = _maxIndent * _radius;
        float fingerRadius = _fingerRadius / transform.lossyScale.x;
        float overshoot = 0f;
        float deepest = 0f;

        for (int k = 0; k < _indent.Length; k++)
        {
            float target = 0f;
            Vector3 local = transform.InverseTransformPoint(_points[k]) - _center;
            float dist = local.magnitude;

            if (_handValid[k / 2] && dist > 1e-6f)
            {
                Vector3 dir = local / dist;
                float surface = SupportRadius(dir);
                float pressDepth = surface - (dist - fingerRadius);
                target = Mathf.Clamp(pressDepth, 0f, maxIndent);
                overshoot = Mathf.Max(overshoot, pressDepth - maxIndent);

                // Chi cap nhat huong an khi dau ngon DANG an vao; khi tha,
                // giu nguyen huong cu de vet lun nay ve dung cho cu.
                if (target > 0f)
                {
                    _pressDir[k] = dir;
                    _pressSurface[k] = surface;
                }
            }

            // Lo xo co giam chan: do lun duoi theo muc tieu, va nay ve khi tha.
            float accel = _stiffness * (target - _indent[k]) - _damping * _indentVel[k];
            _indentVel[k] += accel * dt;
            _indent[k] += _indentVel[k] * dt;
            _indent[k] = Mathf.Clamp(_indent[k], -0.3f * maxIndent, maxIndent);
            deepest = Mathf.Max(deepest, _indent[k]);
        }

        Squish01 = Mathf.Clamp01(deepest / Mathf.Max(maxIndent, 1e-6f));
        OvershootMeters = Mathf.Max(0f, overshoot) * transform.lossyScale.x;
    }

    /// <summary>Be mat vat nam cach tam bao xa theo huong nay. Voi vat loi
    /// (qua cau, khoi...) day la khoang cach chinh xac; voi vat lom thi xap xi.</summary>
    private float SupportRadius(Vector3 dir)
    {
        float best = 0f;
        for (int i = 0; i < _baseVertices.Length; i++)
        {
            float d = Vector3.Dot(_baseVertices[i] - _center, dir);
            if (d > best) best = d;
        }
        return best;
    }

    private void ApplyDeformation()
    {
        bool active = false;
        foreach (float indent in _indent) active |= Mathf.Abs(indent) > 1e-6f;
        if (!active)
        {
            // Da ve hinh goc -- chi ghi lai mesh MOT lan roi thoi, khong ton
            // cong tinh toan moi khung hinh khi khong ai cham vao.
            if (_meshDirty)
            {
                _mesh.vertices = _baseVertices;
                RecalculateSmoothNormals();
                _mesh.RecalculateBounds();
                _meshDirty = false;
            }
            return;
        }

        float sigma = _contactSpread * _radius;
        float inv2Sigma2 = 1f / (2f * sigma * sigma);

        for (int i = 0; i < _baseVertices.Length; i++)
        {
            Vector3 p = _baseVertices[i];
            Vector3 radial = (p - _center).normalized;
            Vector3 offset = Vector3.zero;

            for (int k = 0; k < _indent.Length; k++)
            {
                if (Mathf.Abs(_indent[k]) < 1e-6f) continue;

                // Dinh cang gan cho an thi bi day vao cang nhieu (duong cong
                // hinh chuong), xa ra thi giam dan ve 0 -> vet lun tron, mem.
                Vector3 contact = _center + _pressDir[k] * _pressSurface[k];
                float w = Mathf.Exp(-(p - contact).sqrMagnitude * inv2Sigma2);
                offset -= _pressDir[k] * (_indent[k] * w);

                // Phinh ra o vung vuong goc voi huong an (quanh "bung" vat).
                float side = 1f - Mathf.Abs(Vector3.Dot(radial, _pressDir[k]));
                offset += radial * (0.5f * _bulge * _indent[k] * side * side);
            }

            _deformed[i] = p + offset;
        }

        _mesh.vertices = _deformed;
        RecalculateSmoothNormals();
        _mesh.RecalculateBounds();
        _meshDirty = true;
    }

    /// <summary>Tim trong CHINH ban tay dang duoc FingerUDPReceiver dieu khien
    /// -- tranh bat nham dau ngon cua tay khac trong scene (vd tay trai).</summary>
    private static Transform FindInScene(string name)
    {
        var receiver = FindAnyObjectByType<FingerUDPReceiver>();
        return receiver != null ? FindDeepChild(receiver.transform, name) : null;
    }

    private static Transform FindDeepChild(Transform parent, string name)
    {
        foreach (Transform child in parent)
        {
            if (child.name == name) return child;
            Transform result = FindDeepChild(child, name);
            if (result != null) return result;
        }
        return null;
    }
}
