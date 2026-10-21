using UnityEngine;

/// <summary>
/// Dung tay gang ao TU ANH camera (xem HandImageFit): moi anh Python gui ve -> doi 9 diem thanh 9 tia
/// tu camera luc chup -> giai 1 lan -> giua 2 anh chi truot muot toi ket qua moi (KHONG giai lai theo
/// co tay Quest dang rung). Python mat tay: giu tu the cuoi _holdSeconds roi tra dan ve tay Quest.
///
/// FingerUDPReceiver goi Drive() moi khi HandVisual ghi xong khop. Tat component nay = tay Quest thuan
/// (chi 3 ngon giua/ap ut/ut van nam).
/// </summary>
public sealed class ImageHandSolver : MonoBehaviour
{
    [Tooltip("Moi tang truot muot (co 2 tang noi tiep, giay) tu ket qua da loc sang khung hinh -- de tay chay DEU giua 2 anh " +
             "(18 anh/giay), phan chong rung do bo loc One-Euro ben duoi lo. 1 tang 0.025 (ban 03/10 17:00) don chuyen dong vao " +
             "1-2 khung sau moi anh -> giat khi nhac vat. Truoc 03/10: 1 tang 0.06 + loc o Python (tre ~115 ms). " +
             "20/10: 0.02 -> 0.015 (tools/filter_sim.py tren glove_diag 12:17: cung beta 200 tay ao bot ~18 ms tre, giat khi chay " +
             "0.45 -> 0.69 mm; 0.01 bot 26 ms nhung giat x2.5).")]
    [SerializeField] private float _displaySeconds = 0.015f;
    [Header("Loc One-Euro (moi lan giai): tay dung yen -> loc manh, tay chay -> bam nhanh")]
    [Tooltip("Tan so cat khi tay dung yen (Hz). Nho = muot hon nhung tre hon.")]
    [SerializeField] private float _minCutoff = 0.7f;
    [Tooltip("Tan so cat tang them theo toc do co tay (Hz moi m/s). 20/10: 80 -> 200 (bam nhanh hon luc tay bat dau chay, " +
             "rung dung yen 1.07 -> 1.15 mm mo phong; minCutoff gan nhu khong anh huong do tre).")]
    [SerializeField] private float _betaPosition = 200f;
    [Tooltip("Tan so cat tang them theo toc do xoay co tay (Hz moi do/s).")]
    [SerializeField] private float _betaRotation = 0.1f;
    [Tooltip("Tan so cat tang them theo toc do gap khop ngon (Hz moi do/s), tinh rieng tung khop.")]
    [SerializeField] private float _betaAngle = 0.05f;
    [Tooltip("Tan so cat khi uoc luong toc do (Hz).")]
    [SerializeField] private float _speedCutoff = 1f;
    [Tooltip("Giua 2 anh, day vi tri co tay di tiep theo van toc bay nhieu phan (0 = dung yen cho anh moi). " +
             "1 = di deu theo van toc -> bu lai phan tre cua 2 tang truot muot.")]
    [SerializeField] private float _extrapolate = 1f;
    [Tooltip("Nhu tren nhung cho huong co tay + goc ngon (de 1 thi rung xoay luc dung yen tang ro).")]
    [SerializeField] private float _extrapolateRotation = 0.5f;
    [Tooltip("Ngoai suy toi da bay nhieu giay sau lan giai cuoi (anh den ~0.056 s/anh).")]
    [SerializeField] private float _maxExtrapolateSeconds = 0.06f;
    [Header("Do tin cay tung ket qua (chong giat khi 2 ngon sap cham nhau)")]
    [Tooltip("Sai so anh (theo long ban tay) duoi muc nay = tin han; tren _errBad = gan nhu bo. glove_diag 04/10 16:36: tay tu do " +
             "TB 0.042, luc 2 ngon sap cham 0.147 -- co tay nhay 26-78 do, ngon cai 10-18 do moi lan, One-Euro tuong tay chay nhanh nen cho qua.")]
    [SerializeField] private float _errGood = 0.07f;
    [SerializeField] private float _errBad = 0.20f;
    [Tooltip("Huong co tay nhay hon muc nay (do) so voi ket qua da loc ma ket qua ngay truoc KHONG xac nhan -> chi tin 20% " +
             "(che do moi _trustRejectsNotSlows: bo han ket qua do).")]
    [SerializeField] private float _jumpDegrees = 15f;
    [Tooltip("2 ket qua lien tiep lech nhau duoi muc nay (do, cach nhau < 0.2 s) = xac nhan dong tac that (vd xoay tay nhanh).")]
    [SerializeField] private float _confirmDegrees = 10f;
    [Tooltip("Do tin cay trung binh cac diem cua 1 ngon (model Python) tu muc thap -> cao: duoi = giu nguyen dang ngon, tren = tin han. " +
             "Luc 2 ngon sap cham, diem ngon cai chi 0.2-0.35 (nhin ro: 0.6-1.2).")]
    [SerializeField] private float _confLow = 0.25f;
    [SerializeField] private float _confHigh = 0.6f;
    [Tooltip("Muc tin toi thieu moi ket qua (de khong dung han khi model mai khong chac). Chi dung o che do cu.")]
    [SerializeField] private float _minTrust = 0.03f;
    [Tooltip("Ket qua tin duoi muc nay KHONG tinh la 'thay tay' (tay o mep khung / bi khuat -> model doan sai, glove_diag 13/10: " +
             "fit 0.03 lien tuc ma w van 1). Chi toan ket qua kem -> giu dang tot cuoi _holdSeconds roi tra ve tay Quest, gate " +
             "cua GloveForceEsp32Output ve 0. Sau khi mat tay, ket qua DAU TIEN phai dat muc nay moi duoc lay. 0 = nhu cu.")]
    [SerializeField] private float _minGoodTrust = 0.1f;

    [Header("Do tre: ket qua kem thi BO, ket qua dat thi bam NHANH (18/10)")]
    [Tooltip("BAT: muc tin chi dung de LOAI ket qua (duoi _rejectBelow = bo qua), ket qua duoc nhan thi bam voi toc do it nhat " +
             "_acceptFloor. Co tay chi xet sai so anh + 3 diem long ban tay (co tay, goc ngon cai, goc ngon tro) -- khong bi ngon cai " +
             "bi khuat keo cham theo.\n\nTAT (cach cu): toc do bam NHAN voi muc tin -> luc 2 ngon sap cham (tin ~0.1) tay ao bo toi " +
             "cho moi trong ~2 s.")]
    [SerializeField] private bool _trustRejectsNotSlows = true;
    [Tooltip("Ket qua (co tay hoac tung ngon) tin duoi muc nay thi bo, khong cap nhat. Bang _minGoodTrust: duoi muc do coi nhu khong thay tay.")]
    [SerializeField] private float _rejectBelow = 0.1f;
    [Tooltip("Ket qua duoc nhan bam toi it nhat bang muc nay so voi ket qua tin han (1 = luon bam nhu ket qua tot nhat).")]
    [Range(0.1f, 1f)]
    [SerializeField] private float _acceptFloor = 0.6f;

    [Header("Bu do tre bang chuyen dong Quest (THU NGHIEM, mac dinh tat)")]
    [Tooltip("BAT: vi tri co tay = ket qua anh + doan tay Quest da di ke tu luc CHUP anh do -> bu ca phan tre WiFi + Python. " +
             "Quest nhin tay deo gang rung chieu sau 4-10 cm, nen chi bat sau khi glove_diag cho thay chuyen dong Quest du muot.")]
    [SerializeField] private bool _useQuestMotion = false;
    [Tooltip("Chi lay phan chuyen dong Quest VUONG GOC voi tia nhin camera (bo phan xa/gan -- phan Quest rung nhieu nhat).")]
    [SerializeField] private bool _questMotionLateralOnly = true;
    [Tooltip("Doan bu toi da (met) -- chan Quest nhay sai.")]
    [SerializeField] private float _questMotionMaxMeters = 0.15f;

    [Header("Tay ra khoi khung camera (18/10)")]
    [Tooltip("BAT: diem Python gui ve nam trong dai mep anh hoac ngoai anh (model DOAN, khong nhin thay) -> trong so 0 khi giai. " +
             "Ngon con it hon _minFingerPoints diem trong khung -> GIU dang tot cuoi cua ngon do. Ca tay it hon _minInFramePoints " +
             "diem (hoac mat ca 3 diem long ban tay) -> khong giai, coi nhu khong thay tay.")]
    [SerializeField] private bool _rejectEdgePoints = true;
    [Tooltip("Dai mep anh (phan cua chieu rong / cao) coi la 'sat mep, khong tin'.")]
    [Range(0f, 0.15f)]
    [SerializeField] private float _edgeMargin = 0.03f;
    [Tooltip("Ca tay can it nhat bay nhieu diem (tren 9) trong khung moi giai.")]
    [Range(3, 9)]
    [SerializeField] private int _minInFramePoints = 5;
    [Tooltip("Moi ngon (4 diem) can it nhat bay nhieu diem trong khung, khong thi giu dang cu cua ngon do.")]
    [Range(1, 4)]
    [SerializeField] private int _minFingerPoints = 2;
    [Tooltip("BAT: mat tay lau -> chi CO TAY tra ve Quest, ngon cai + tro GIU dang tot cuoi. TAT (cu): ca ngon cung ve tay Quest -- " +
             "Quest khong nhin duoc ngon deo gang nen ngon ao bi van meo.")]
    [SerializeField] private bool _keepFingersWhenLost = true;
    [Tooltip("BAT: ngon cai hoac tro dang bi GIU dang (ra khoi khung) -> ForceWeight ve 0, gate luc cua GloveForceEsp32Output tat " +
             "luc dan (khong day theo 1 ngon dang dung yen).")]
    [SerializeField] private bool _cutForceWhenFingerFrozen = true;

    [Tooltip("Khi bo giai doi sang cach hieu khac (vd thoat khoi dang ngon gap sai), truot sang dang moi trong khoang nay (giay).")]
    [SerializeField] private float _switchSeconds = 0.3f;
    [Tooltip("Python mat tay: giu tu the cuoi bay lau (giay) roi moi tra ve tay Quest.")]
    [SerializeField] private float _holdSeconds = 1.0f;
    [Tooltip("Thoi gian chuyen giua tay theo anh va tay Quest (giay).")]
    [SerializeField] private float _blendSeconds = 0.3f;
    [Tooltip("Chieu sau Quest (gang moi rung 4-10 cm) duoc trung binh bay nhieu giay truoc khi lam goi y.")]
    [SerializeField] private float _depthPriorSeconds = 0.6f;
    [Tooltip("Huong co tay Quest duoc lam muot bay nhieu giay truoc khi lam goi y (chi de chon long ban tay up/ngua).")]
    [SerializeField] private float _rotPriorSeconds = 0.3f;
    [Tooltip("Dap an truoc cu hon muc nay (giay) thi giai lai tu dau, khong bam theo no.")]
    [SerializeField] private float _warmSeconds = 0.5f;
    [Tooltip("Diem co do tin cay duoi muc nay bi bo qua.")]
    [SerializeField] private float _minConfidence = 0.2f;
    [Tooltip("Trong so diem 1 (goc ngon cai sat co tay -- khong dan bang keo, model dat kem chac).")]
    [SerializeField] private float _thumbBaseWeight = 0.5f;
    [Tooltip("Dung huong co tay Quest lam goi y (rang buoc yeu). TAT mac dinh: voi gang moi Quest hay doan NGUOC long ban tay " +
             "(glove_diag 01/10 17:45: mu ban tay quay ve camera ma Quest bao long ban tay quay ve mat) -> keo tay ao ve dang nghieng canh.")]
    [SerializeField] private bool _useQuestRotation = false;
    [Tooltip("Dung cum mieng luc giac tren mu ban tay (Python tim theo mau) de biet ban tay lat bao nhieu. TAT tu 02/10: " +
             "chay lai 5 ban ghi, bat len lam sai so anh tang (vd 0.056 -> 0.066), ngon giat gap doi; diem Python tim lech " +
             "vi tri mo hinh trung binh 0.42 long ban tay (hay bat nham mau go/hop giay/chuot) -> keo co tay sai, ngon tro " +
             "phai cong queo de bu, ro nhat khi voi tay ra xa.")]
    [SerializeField] private bool _useDorsalTiles = false;
    [Tooltip("Kich thuoc tay gang ao = kich thuoc Quest do duoc o TAY TRAI TRAN (Hand.Scale) -- 2 tay cung co, con Quest nhin " +
             "tay deo gang thi uoc luong kich thuoc khong dang tin. Chua thay tay trai lan nao thi giu kich thuoc Quest dat.")]
    [SerializeField] private bool _useLeftHandScale = true;

    // 3 diem long ban tay: co tay (0), goc ngon cai (1), goc ngon tro (5) -- quyet dinh vi tri/huong co tay
    private static readonly int[] PalmPoints = { 0, 1, 5 };

    private FingerUDPReceiver _receiver;
    private GloveLiveStreamer _streamer;
    private Transform _wrist;
    private FingerChainFitter _thumb, _index;
    private HandImageFit _fit;
    private Transform[] _between;      // xuong giua co tay va goc ngon (vd xuong ban tay ngon tro)
    private Quaternion[] _betweenRest;

    private readonly Vector2[] _uv = new Vector2[HandImageFit.Points];
    private readonly float[] _conf = new float[HandImageFit.Points];
    private readonly float[] _confIn = new float[HandImageFit.Points]; // = _conf, diem sat mep/ngoai anh = 0
    private readonly Vector3[] _dirs = new Vector3[HandImageFit.Points];
    private readonly float[] _w = new float[HandImageFit.Points];

    private int _lastFid;
    private float _lastGoodTime = -999f;     // lan cuoi co ket qua dat _minGoodTrust
    private float _depthPrior;
    private bool _hasDepthPrior;
    private float _lastDepthTime;
    private Quaternion _rotPrior;
    private bool _hasRotPrior;
    private float _lastRotTime;

    private Vector3 _dispP;
    private Quaternion _dispR = Quaternion.identity;
    private readonly float[] _dispA = new float[8];
    private bool _hasDisp;
    private int _dispFrame = -1;
    private float _weight;
    private int _seenSwitches;
    private float _slowUntil;
    private bool _thumbFrozen, _indexFrozen; // ngon ra khoi khung -> giu dang cu
    private float _forceGate = 1f;

    // Ket qua da loc One-Euro (cap nhat moi lan giai xong)
    private Vector3 _fP;
    private Quaternion _fR = Quaternion.identity;
    private readonly float[] _fA = new float[8];
    private float _fSpeedP, _fSpeedR;
    private readonly float[] _fSpeedA = new float[8];
    private Vector3 _fVel, _fAngVel;           // m/s, truc*do/s
    private readonly float[] _fVelA = new float[8];
    private float _fTime = -999f;              // lan loc gan nhat (moc ngoai suy hien thi)
    private float _fTimeW, _fTimeT, _fTimeI;    // lan cuoi NHAN ket qua co tay / ngon cai / ngon tro (che do moi)
    private Quaternion _rawPrevR = Quaternion.identity; // ket qua tho lan truoc (de xac nhan cu nhay)
    private float _rawPrevTime = -999f;
    // Tang truot giua (hien thi = 2 tang noi tiep)
    private Vector3 _midP;
    private Quaternion _midR = Quaternion.identity;
    private readonly float[] _midA = new float[8];
    private bool _hasFilter;
    // Tam tay theo Quest + camera LUC CHUP anh vua giai (bu do tre bang chuyen dong Quest)
    private Vector3 _solveHintCenter, _solveCamPos;
    private bool _hasSolveHint;

    // --- Chan doan (FingerUDPReceiver ghi vao glove_diag) ---
    public float Weight => _weight;
    public int SolvedFrameId { get; private set; }
    public float ImageError => _fit != null ? _fit.ImageError : 0f;
    public float Cost => _fit != null ? _fit.LastCost : 0f;
    public float Depth => _fit != null ? _fit.Depth : 0f;
    public float DepthPrior => _hasDepthPrior ? _depthPrior : 0f;
    public float Pinch => _fit != null ? _fit.Pinch : 0f;
    public int Switches => _fit != null ? _fit.Switches : 0;
    public float SolveMs { get; private set; }
    /// <summary>Muc tin cua lan giai gan nhat (0..1) cho vi tri/huong co tay.</summary>
    public float LastTrust { get; private set; } = 1f;
    public bool Ready => _fit != null;
    public int DorsalUsed => _fit != null ? _fit.DorsalUsed : -1;
    /// <summary>Kich thuoc tay gang ao hien tai (1 = khung xuong chuan, long ban tay ~9.9 cm).</summary>
    public float HandScale => _wrist != null ? _wrist.lossyScale.x : 0f;
    /// <summary>Tuoi anh vua giai (ms): tu luc chup toi luc ket qua Python ve Unity.</summary>
    public float FrameAgeMs { get; private set; }
    /// <summary>Nhu FrameAgeMs nhung tinh tu luc camera PHOI SANG (som hon ca phan camera xu ly). 0 = khong doc duoc.</summary>
    public float ExposureAgeMs { get; private set; }
    /// <summary>Co tay giai THO tu anh gan nhat (chua loc, chua ngoai suy) va luc camera phoi sang anh do
    /// (Time.realtimeSinceStartup, 0 = khong biet) -- "tay that luc chup", de do tre tay ao so voi tay that.</summary>
    public Vector3 RawWrist { get; private set; }
    public float RawExposureTime { get; private set; }
    /// <summary>Co tay dang hien lech ket qua giai moi nhat bao nhieu (mm / do) -- phan tre do bo loc.</summary>
    public float LagMm { get; private set; }
    public float LagDeg { get; private set; }
    /// <summary>Muc tin lan giai gan nhat: co tay, ngon cai, ngon tro (0..1, truoc khi ap nguong nhan/bo).</summary>
    public float WristTrust { get; private set; }
    public float ThumbTrust { get; private set; }
    public float IndexTrust { get; private set; }
    /// <summary>So diem (tren 9) sat mep / ngoai anh o anh vua nhan.</summary>
    public int EdgePoints { get; private set; }
    /// <summary>0 = tay day du trong khung, 1 = mot phan ra ngoai (co diem bi bo / ngon dang giu dang), 2 = khong thay tay (giu dang / ve Quest).</summary>
    public int State { get; private set; } = 2;
    /// <summary>Gate luc cho GloveForceEsp32Output: = Weight, ve 0 khi ngon cai/tro dang giu dang (_cutForceWhenFingerFrozen).</summary>
    public float ForceWeight => _weight * _forceGate;
    public bool ThumbFrozen => _thumbFrozen;
    public bool IndexFrozen => _indexFrozen;

    private float _initWristScale = 1f;
    private Oculus.Interaction.HandVisual _gloveVisual, _leftVisual;
    private float _leftScale;

    /// <summary>Goi tu FingerUDPReceiver ngay sau khi tao dong hoc 5 ngon (rig o tu the nghi).</summary>
    /// <param name="fitters">5 ngon: cai, tro (dung tay), giua/ap ut/ut (tinh vi tri cum luc giac khi nam).</param>
    public void Initialize(FingerUDPReceiver receiver, Transform wrist, FingerChainFitter[] fitters, Vector3 palmTarget, Vector3 closedAngles)
    {
        _receiver = receiver;
        _wrist = wrist;
        _thumb = fitters[0];
        _index = fitters[1];
        if (wrist == null || _thumb == null || _index == null) return;
        FingerChainFitter thumb = _thumb, index = _index;
        _fit = new HandImageFit(wrist, fitters, palmTarget, closedAngles);
        _initWristScale = Mathf.Max(wrist.lossyScale.x, 1e-4f);
        _gloveVisual = receiver.GetComponent<Oculus.Interaction.HandVisual>();

        var list = new System.Collections.Generic.List<Transform>();
        foreach (Transform root in new[] { thumb.RootBone, index.RootBone })
            for (Transform t = root.parent; t != null && t != wrist; t = t.parent)
                if (!list.Contains(t)) list.Add(t);
        _between = list.ToArray();
        _betweenRest = new Quaternion[_between.Length];
        for (int i = 0; i < _between.Length; i++) _betweenRest[i] = _between[i].localRotation;
    }

    /// <summary>Dung tay ao cho khung nay. questUpdated = HandVisual vua ghi co tay moi (questPos/Rot).
    /// holdLonger = dang cam vat ma mat du lieu -> giu tu the cuoi them (khong de vat tuot).</summary>
    public void Drive(bool questUpdated, Vector3 questPos, Quaternion questRot, bool dataValid, bool holdLonger = false)
    {
        if (_fit == null) return;
        ApplyHandScale();
        if (questUpdated) UpdateRotPrior(questRot);
        if (dataValid) TrySolveNewFrame();

        if (Time.frameCount != _dispFrame)
        {
            _dispFrame = Time.frameCount;
            bool fresh = _fit.HasSolution && (Time.time - _lastGoodTime <= _holdSeconds || holdLonger);
            _weight = Mathf.MoveTowards(_weight, fresh ? 1f : 0f, Time.deltaTime / Mathf.Max(_blendSeconds, 1e-3f));
            bool frozen = _thumbFrozen || _indexFrozen;
            _forceGate = _cutForceWhenFingerFrozen && frozen ? 0f : 1f; // GloveForceEsp32Output tu lam muot gate
            State = !fresh ? 2 : (EdgePoints > 0 || frozen ? 1 : 0);
            if (_fit.HasSolution)
            {
                if (!_hasFilter) ResetFilter(Time.time);
                if (!_hasDisp || _weight <= 0.001f)
                {
                    _dispP = _midP = _fP;
                    _dispR = _midR = _fR;
                    System.Array.Copy(_fA, _dispA, 8);
                    System.Array.Copy(_fA, _midA, 8);
                    _hasDisp = true;
                }
                else
                {
                    // Bo giai vua doi sang cach hieu khac (dang tay khac han): truot cham hon mot chut
                    if (_fit.Switches != _seenSwitches)
                    {
                        _seenSwitches = _fit.Switches;
                        _slowUntil = Time.time + _switchSeconds;
                    }
                    float tau = Time.time < _slowUntil ? _switchSeconds * 0.25f : _displaySeconds;
                    float a = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(tau, 1e-3f));
                    // Dich = ket qua da loc, day tiep theo van toc ke tu lan giai cuoi
                    float ahead = Mathf.Clamp(Time.time - _fTime, 0f, _maxExtrapolateSeconds);
                    float aheadR = _extrapolateRotation * ahead;
                    Vector3 tgtP = _fP + _extrapolate * ahead * _fVel;
                    if (_useQuestMotion && TryQuestMotion(out Vector3 questDelta)) tgtP = _fP + questDelta;
                    Quaternion tgtR = Quaternion.AngleAxis(_fAngVel.magnitude * aheadR, _fAngVel.normalized) * _fR;
                    // 2 tang truot noi tiep: van toc hien thi lien tuc (khong giat theo nhip anh)
                    _midP = Vector3.Lerp(_midP, tgtP, a);
                    _dispP = Vector3.Lerp(_dispP, _midP, a);
                    _midR = Quaternion.Slerp(_midR, tgtR, a);
                    _dispR = Quaternion.Slerp(_dispR, _midR, a);
                    for (int k = 0; k < 8; k++)
                    {
                        _midA[k] = Mathf.Lerp(_midA[k], _fA[k] + aheadR * _fVelA[k], a);
                        _dispA[k] = Mathf.Lerp(_dispA[k], _midA[k], a);
                    }
                }
                LagMm = Vector3.Distance(_dispP, _fit.Position) * 1000f;
                LagDeg = Quaternion.Angle(_dispR, _fit.Rotation);
            }
        }

        // Ghi vao xuong (moi lan goi -- HandVisual co the vua ghi de). Co tay: tron tay anh <-> tay Quest theo _weight.
        // Ngon: _keepFingersWhenLost -> luon dang tu anh (dang tot cuoi khi mat tay), vi Quest khong nhin duoc ngon deo gang.
        float w = _hasDisp ? _weight : 0f;
        float wf = _keepFingersWhenLost ? (_hasDisp ? 1f : 0f) : w;
        if (wf > 0f)
            for (int i = 0; i < _between.Length; i++)
                _between[i].localRotation = Quaternion.Slerp(_between[i].localRotation, _betweenRest[i], wf);
        if (w > 0f)
            _wrist.SetPositionAndRotation(Vector3.Lerp(questPos, _dispP, w), Quaternion.Slerp(questRot, _dispR, w));
        _thumb.SetAngles(_dispA[0], _dispA[1], _dispA[2], _dispA[3]);
        _index.SetAngles(_dispA[4], _dispA[5], _dispA[6], _dispA[7]);
        _thumb.Apply(wf);
        _index.Apply(wf);
    }

    /// <summary>Doan tay Quest da di ke tu luc chup anh vua giai (tam tay = goc ngon giua, cung diem lam goi y).</summary>
    private bool TryQuestMotion(out Vector3 delta)
    {
        delta = Vector3.zero;
        if (!_hasSolveHint || _receiver == null || !_receiver.TryGetRawHandPose(out Vector3 center, out _)) return false;
        delta = center - _solveHintCenter;
        if (_questMotionLateralOnly)
        {
            Vector3 ray = _solveHintCenter - _solveCamPos;
            if (ray.sqrMagnitude > 1e-6f) delta = Vector3.ProjectOnPlane(delta, ray.normalized);
        }
        delta = Vector3.ClampMagnitude(delta, _questMotionMaxMeters);
        return true;
    }

    /// <summary>Kich thuoc tay gang ao lay tu tay trai tran, roi bao bo giai kich thuoc hien tai (HandVisual dat lai
    /// kich thuoc goc ban tay moi khung; ham nay chay SAU HandVisual nen ghi de duoc).</summary>
    private void ApplyHandScale()
    {
        if (_useLeftHandScale && _gloveVisual != null && _gloveVisual.Root != null)
        {
            if (_leftVisual == null && Time.frameCount % 60 == 0)
            {
                foreach (var hv in FindObjectsByType<Oculus.Interaction.HandVisual>(FindObjectsInactive.Exclude))
                    if (hv != _gloveVisual && hv.Hand != null && hv.Hand.Handedness == Oculus.Interaction.Input.Handedness.Left)
                    {
                        _leftVisual = hv;
                        break;
                    }
            }
            var left = _leftVisual != null ? _leftVisual.Hand : null;
            if (left != null && left.IsTrackedDataValid && left.Scale > 0.6f && left.Scale < 1.4f) _leftScale = left.Scale;
            if (_leftScale > 0f)
            {
                Transform root = _gloveVisual.Root;
                float parent = root.parent != null ? Mathf.Max(root.parent.lossyScale.x, 1e-4f) : 1f;
                root.localScale = Vector3.one * (_leftScale / parent);
            }
        }
        _fit.SetScale(_wrist.lossyScale.x / _initWristScale);
    }

    private void UpdateRotPrior(Quaternion questRot)
    {
        if (!_hasRotPrior || Time.time - _lastRotTime > 1f)
            _rotPrior = questRot;
        else
            _rotPrior = Quaternion.Slerp(_rotPrior, questRot,
                1f - Mathf.Exp(-(Time.time - _lastRotTime) / Mathf.Max(_rotPriorSeconds, 1e-3f)));
        _hasRotPrior = true;
        _lastRotTime = Time.time;
    }

    private bool InFrame(Vector2 uv) =>
        uv.x >= _edgeMargin && uv.x <= 1f - _edgeMargin && uv.y >= _edgeMargin && uv.y <= 1f - _edgeMargin;

    private void TrySolveNewFrame()
    {
        if (!_receiver.TryGetPixelPoints(out int fid, _uv, _conf, out int dorsal, out Vector2 dorsalUV) || fid == _lastFid) return;
        _lastFid = fid;
        if (_streamer == null) _streamer = FindAnyObjectByType<GloveLiveStreamer>();
        if (_streamer == null || !_streamer.TryGetFramePose(fid, out Pose cam)) return;
        if (_streamer.TryGetFrameAge(fid, out float age)) FrameAgeMs = age * 1000f;

        // Diem sat mep / ngoai anh: model doan chu khong nhin thay -> trong so 0, tinh nhu do tin cay 0
        int inFrame = 0, palmIn = 0, thumbIn = 0, indexIn = 0, edge = 0;
        for (int i = 0; i < HandImageFit.Points; i++)
        {
            if (!_streamer.TryGetFrameRay(fid, _uv[i], out Ray ray)) return;
            _dirs[i] = ray.direction;
            bool inside = !_rejectEdgePoints || InFrame(_uv[i]);
            if (inside)
            {
                inFrame++;
                if (i == 0 || i == 1 || i == 5) palmIn++;
                if (i >= 1 && i <= 4) thumbIn++;
                else if (i >= 5) indexIn++;
            }
            else edge++;
            _confIn[i] = inside ? _conf[i] : 0f;
            float c = inside && _conf[i] >= _minConfidence ? 1f : 0f;
            _w[i] = i == 1 ? c * _thumbBaseWeight : c;
        }
        EdgePoints = edge;
        if (_rejectEdgePoints && (inFrame < _minInFramePoints || palmIn == 0))
        {
            // Qua it diem nhin thay that -> khong giai (giai ra dang tay meo); khong lam moi "lan cuoi thay tay"
            WristTrust = ThumbTrust = IndexTrust = 0f;
            return;
        }
        _thumbFrozen = _rejectEdgePoints && thumbIn < _minFingerPoints;
        _indexFrozen = _rejectEdgePoints && indexIn < _minFingerPoints;

        // Chieu sau tay theo Quest LUC CHUP (tam tay = goc ngon giua), trung binh theo thoi gian
        _hasSolveHint = false;
        if (_streamer.TryGetFrameHint(fid, out Vector3 center, out _))
        {
            _solveHintCenter = center;
            _solveCamPos = cam.position;
            _hasSolveHint = true;
            float z = Vector3.Dot(center - cam.position, cam.rotation * Vector3.forward);
            if (z > 0.08f && z < 1.2f)
            {
                float since = Time.time - _lastDepthTime;
                _depthPrior = !_hasDepthPrior || since > 2f ? z
                    : Mathf.Lerp(_depthPrior, z, 1f - Mathf.Exp(-since / Mathf.Max(_depthPriorSeconds, 1e-3f)));
                _hasDepthPrior = true;
                _lastDepthTime = Time.time;
            }
        }

        // Cum luc giac tren mu ban tay: 1 = thay (tia qua tam cum), 0 = tim ma khong thay, -1 = khong biet
        Vector3 dorsalDir = Vector3.zero;
        if (!_useDorsalTiles) dorsal = -1;
        else if (dorsal == 1)
        {
            if (_streamer.TryGetFrameRay(fid, dorsalUV, out Ray dr)) dorsalDir = dr.direction;
            else dorsal = -1;
        }

        // Giai tiep tu dap an truoc chi khi dap an do con TOT (ket qua kem lam diem xuat phat de bi ket o dang sai)
        bool warm = Time.time - _lastGoodTime <= _warmSeconds;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool ok = _fit.Solve(cam.position, cam.rotation, _dirs, _w,
                             _hasDepthPrior ? _depthPrior : 0f, _useQuestRotation && _hasRotPrior ? _rotPrior : (Quaternion?)null,
                             warm, dorsal, dorsalDir);
        SolveMs = (float)sw.Elapsed.TotalMilliseconds;
        if (!ok) return;
        // Moc "tay that luc chup": ket qua tho + luc phoi sang (ghi ca khi bo loc bo ket qua nay)
        RawWrist = _fit.Position;
        if (_streamer.TryGetFrameExposureTime(fid, out float exposure))
        {
            RawExposureTime = exposure;
            ExposureAgeMs = (Time.realtimeSinceStartup - exposure) * 1000f;
        }
        else RawExposureTime = ExposureAgeMs = 0f;
        if (!_hasFilter || Time.time - _lastGoodTime > _holdSeconds)
        {
            // Bat dau lai (lan dau / sau khi mat tay): chi lay ket qua TOT, khong thi tay giu nguyen dang dang hien
            LastTrust = ResultTrust();
            WristTrust = LastTrust;
            if (LastTrust < _minGoodTrust) return;
            ResetFilter(Time.time, _hasFilter);
        }
        else if (_trustRejectsNotSlows) FilterStepGated(Time.time);
        else FilterStep(Time.time);
        if (LastTrust >= _minGoodTrust) _lastGoodTime = Time.time;
        SolvedFrameId = fid;
    }

    /// <summary>Muc tin theo sai so anh (1 = tot, 0 = qua kem).</summary>
    private float ErrTrust() => Mathf.Clamp01((_errBad - _fit.ImageError) / Mathf.Max(_errBad - _errGood, 1e-4f));

    /// <summary>Do tin ket qua vua giai theo sai so anh + do tin cay diem (nhu FilterStep, khong co phan chong nhay huong).</summary>
    private float ResultTrust()
    {
        float w = ErrTrust();
        if (_trustRejectsNotSlows) return w * Mathf.Max(PalmConfTrust(), 0.3f);
        return Mathf.Max(w * Mathf.Max(ConfTrust(0, 8), 0.3f), _minTrust);
    }

    /// <summary>Bat dau lai bo loc tu ket qua giai hien tai (lan dau, hoac sau khi mat tay lau).</summary>
    /// <param name="keepUnsureFingers">Mat tay lau roi thay lai: ngon nao model khong chac thi GIU dang cu (tron theo do tin cay)
    /// thay vi lay thang ket qua -- luc 2 ngon sap cham ket qua ve thua (cach nhau > 1 s), lay thang thi ngon cai nhay 20-80 do.</param>
    private void ResetFilter(float now, bool keepUnsureFingers = false)
    {
        _fP = _fit.Position;
        _fR = _fit.Rotation;
        if (keepUnsureFingers)
        {
            float wT = _thumbFrozen ? 0f : ConfTrust(1, 4), wI = _indexFrozen ? 0f : ConfTrust(5, 8);
            for (int k = 0; k < 8; k++) _fA[k] = Mathf.Lerp(_fA[k], _fit.Angles[k], k < 4 ? wT : wI);
        }
        else System.Array.Copy(_fit.Angles, _fA, 8);
        _rawPrevR = _fit.Rotation;
        _rawPrevTime = now;
        _fSpeedP = _fSpeedR = 0f;
        System.Array.Clear(_fSpeedA, 0, 8);
        _fVel = _fAngVel = Vector3.zero;
        System.Array.Clear(_fVelA, 0, 8);
        _fTime = _fTimeW = _fTimeT = _fTimeI = now;
        _hasFilter = true;
    }

    /// <summary>Loc One-Euro 1 buoc cho lan giai moi: tan so cat = _minCutoff + beta * toc do (da lam muot),
    /// nen tay dung yen thi loc manh (it rung), tay chay thi gan nhu theo ngay (it tre).
    /// Che do CU: toc do bam nhan voi muc tin (xem _trustRejectsNotSlows).</summary>
    private void FilterStep(float now)
    {
        float te = Mathf.Max(now - _fTime, 1e-3f);
        float ad = OneEuroAlpha(te, _speedCutoff);
        Vector3 p = _fit.Position;
        Quaternion r = _fit.Rotation;

        // Do tin cay ket qua nay: sai so anh, nhay huong dot ngot khong duoc xac nhan, do tin cay diem cua model.
        float w = ErrTrust();
        bool confirmed = Quaternion.Angle(r, _rawPrevR) < _confirmDegrees && now - _rawPrevTime < 0.2f;
        if (Quaternion.Angle(r, _fR) > _jumpDegrees && !confirmed) w *= 0.2f;
        float wFingers = w;
        w *= Mathf.Max(ConfTrust(0, 8), 0.3f);
        float wT = _thumbFrozen ? 0f : Mathf.Max(wFingers * ConfTrust(1, 4), _minTrust);
        float wI = _indexFrozen ? 0f : Mathf.Max(wFingers * ConfTrust(5, 8), _minTrust);
        w = Mathf.Max(w, _minTrust);
        _rawPrevR = r;
        _rawPrevTime = now;
        LastTrust = WristTrust = w;
        ThumbTrust = wT;
        IndexTrust = wI;

        _fSpeedP = Mathf.Lerp(_fSpeedP, Vector3.Distance(p, _fP) / te, ad * w);
        _fSpeedR = Mathf.Lerp(_fSpeedR, Quaternion.Angle(r, _fR) / te, ad * w);
        Vector3 newP = Vector3.Lerp(_fP, p, OneEuroAlpha(te, _minCutoff + _betaPosition * _fSpeedP) * w);
        _fVel = Vector3.Lerp(_fVel, (newP - _fP) / te, 0.5f * w);
        _fP = newP;
        Quaternion newR = Quaternion.Slerp(_fR, r, OneEuroAlpha(te, _minCutoff + _betaRotation * _fSpeedR) * w);
        _fAngVel = Vector3.Lerp(_fAngVel, AngularVelocity(_fR, newR, te), 0.5f * w);
        _fR = newR;
        for (int k = 0; k < 8; k++)
        {
            float x = _fit.Angles[k], wk = k < 4 ? wT : wI;
            if (wk <= 0f) { _fVelA[k] = 0f; continue; } // ngon dang giu dang
            _fSpeedA[k] = Mathf.Lerp(_fSpeedA[k], Mathf.Abs(x - _fA[k]) / te, ad * wk);
            float newA = Mathf.Lerp(_fA[k], x, OneEuroAlpha(te, _minCutoff + _betaAngle * _fSpeedA[k]) * wk);
            _fVelA[k] = Mathf.Lerp(_fVelA[k], (newA - _fA[k]) / te, 0.5f * wk);
            _fA[k] = newA;
        }
        _fTime = _fTimeW = _fTimeT = _fTimeI = now;
    }

    /// <summary>Loc One-Euro, che do MOI: muc tin chi dung de NHAN / BO ket qua. Co tay, ngon cai, ngon tro xet rieng:
    /// tin duoi _rejectBelow (hoac co tay nhay huong khong duoc xac nhan, ngon dang giu dang) -> giu nguyen, van toc 0;
    /// duoc nhan -> bam voi toc do One-Euro nhan max(muc tin, _acceptFloor). Thoi gian te tinh tu lan NHAN gan nhat
    /// cua tung phan, nen sau vai ket qua bi bo, ket qua tot dau tien keo tay ve dung cho ngay.</summary>
    private void FilterStepGated(float now)
    {
        Vector3 p = _fit.Position;
        Quaternion r = _fit.Rotation;
        float err = ErrTrust();
        bool confirmed = Quaternion.Angle(r, _rawPrevR) < _confirmDegrees && now - _rawPrevTime < 0.2f;
        bool jump = Quaternion.Angle(r, _fR) > _jumpDegrees && !confirmed;
        _rawPrevR = r;
        _rawPrevTime = now;

        float wristTrust = err * Mathf.Max(PalmConfTrust(), 0.3f);
        float tT = _thumbFrozen ? 0f : err * ConfTrust(1, 4);
        float tI = _indexFrozen ? 0f : err * ConfTrust(5, 8);
        LastTrust = WristTrust = wristTrust; // "thay tay" xet truoc luat nhay huong (nhu che do cu)
        ThumbTrust = tT;
        IndexTrust = tI;

        float aW = jump ? 0f : Accept(wristTrust);
        if (aW > 0f)
        {
            float te = Mathf.Max(now - _fTimeW, 1e-3f);
            float ad = OneEuroAlpha(te, _speedCutoff);
            _fSpeedP = Mathf.Lerp(_fSpeedP, Vector3.Distance(p, _fP) / te, ad);
            _fSpeedR = Mathf.Lerp(_fSpeedR, Quaternion.Angle(r, _fR) / te, ad);
            Vector3 newP = Vector3.Lerp(_fP, p, OneEuroAlpha(te, _minCutoff + _betaPosition * _fSpeedP) * aW);
            _fVel = Vector3.Lerp(_fVel, (newP - _fP) / te, 0.5f);
            _fP = newP;
            Quaternion newR = Quaternion.Slerp(_fR, r, OneEuroAlpha(te, _minCutoff + _betaRotation * _fSpeedR) * aW);
            _fAngVel = Vector3.Lerp(_fAngVel, AngularVelocity(_fR, newR, te), 0.5f);
            _fR = newR;
            _fTimeW = now;
        }
        else _fVel = _fAngVel = Vector3.zero; // khong ngoai suy theo van toc cu khi dang bo ket qua

        FilterFingerGated(0, Accept(tT), ref _fTimeT, now);
        FilterFingerGated(4, Accept(tI), ref _fTimeI, now);
        _fTime = now;
    }

    /// <summary>4 goc 1 ngon (bat dau tu first) theo luat nhan / bo cua FilterStepGated.</summary>
    private void FilterFingerGated(int first, float accept, ref float lastTime, float now)
    {
        if (accept <= 0f)
        {
            for (int k = first; k < first + 4; k++) _fVelA[k] = 0f;
            return;
        }
        float te = Mathf.Max(now - lastTime, 1e-3f);
        float ad = OneEuroAlpha(te, _speedCutoff);
        for (int k = first; k < first + 4; k++)
        {
            float x = _fit.Angles[k];
            _fSpeedA[k] = Mathf.Lerp(_fSpeedA[k], Mathf.Abs(x - _fA[k]) / te, ad);
            float newA = Mathf.Lerp(_fA[k], x, OneEuroAlpha(te, _minCutoff + _betaAngle * _fSpeedA[k]) * accept);
            _fVelA[k] = Mathf.Lerp(_fVelA[k], (newA - _fA[k]) / te, 0.5f);
            _fA[k] = newA;
        }
        lastTime = now;
    }

    /// <summary>Muc tin -> he so bam: duoi _rejectBelow = 0 (bo), con lai it nhat _acceptFloor.</summary>
    private float Accept(float trust) => trust >= _rejectBelow ? Mathf.Max(trust, _acceptFloor) : 0f;

    private static Vector3 AngularVelocity(Quaternion from, Quaternion to, float te)
    {
        (to * Quaternion.Inverse(from)).ToAngleAxis(out float dAng, out Vector3 axis);
        if (dAng > 180f) dAng -= 360f;
        return float.IsFinite(axis.x) ? axis * (dAng / te) : Vector3.zero;
    }

    /// <summary>Do tin cay (0..1) theo trung binh do tin cay cac diem anh [from..to] cua ket qua vua giai (model Python).
    /// Diem sat mep / ngoai anh tinh la 0 (_rejectEdgePoints).</summary>
    private float ConfTrust(int from, int to)
    {
        float sum = 0f;
        for (int i = from; i <= to; i++) sum += _confIn[i];
        return ConfToTrust(sum / (to - from + 1));
    }

    /// <summary>Do tin cay 3 diem long ban tay (co tay, goc ngon cai, goc ngon tro) -- quyet dinh co tay.</summary>
    private float PalmConfTrust()
    {
        float sum = 0f;
        foreach (int i in PalmPoints) sum += _confIn[i];
        return ConfToTrust(sum / PalmPoints.Length);
    }

    private float ConfToTrust(float meanConf) =>
        Mathf.Clamp01((meanConf - _confLow) / Mathf.Max(_confHigh - _confLow, 1e-4f));

    private static float OneEuroAlpha(float te, float cutoff)
    {
        float r = 2f * Mathf.PI * cutoff * te;
        return r / (r + 1f);
    }
}
