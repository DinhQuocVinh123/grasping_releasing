using UnityEngine;

/// <summary>
/// Nhac nguoi dung giu tay TRONG khung camera mau. Camera nhin hep hon tam nhin trong kinh, nen nguoi dung
/// van thay tay minh va hay de tay lech ra mep ma khong biet -> Python mat diem, tay ao vat veo.
///
/// Vi tri tay tren anh lay tu (theo thu tu):
///   1. 9 diem Python gui ve -- chinh xac, ke ca diem model doan nam NGOAI anh;
///   2. Python khong thay tay: tam tay theo Quest chieu len anh (GloveLiveStreamer; camera tracking cua Quest
///      nhin rong hon camera mau);
///   3. ca hai mat: vi tri cuoi cung, giu them _forgetSeconds.
///
/// Muc nhac:
///   - tay o vung giua anh: khong hien gi;
///   - tay vao dai mep (_edgeBand): tay gang ao nga mau cam + mo dan, mui ten o mep khung chi vao giua;
///   - tay ra ngoai khung: hien them khung chu nhat mo = vung camera nhin thay.
/// Tat component nay = khong nhac (de so sanh trong thi nghiem).
///
/// Gan cung object voi FingerUDPReceiver cua tay gang (StaticHandModel_Right).
/// </summary>
[DefaultExecutionOrder(400)]
public class CameraFrameCue : MonoBehaviour
{
    [Header("Nguồn")]
    [Tooltip("De trong = tim tren cung object.")]
    [SerializeField] private FingerUDPReceiver _receiver;
    [Tooltip("De trong = tu tim trong scene.")]
    [SerializeField] private GloveLiveStreamer _streamer;
    [Tooltip("Mat nguoi xem (CenterEyeAnchor). De trong = Camera.main.")]
    [SerializeField] private Transform _head;

    [Header("Vùng an toàn (theo tỉ lệ ảnh)")]
    [Tooltip("Tay cach mep anh it hon khoang nay (phan cua chieu rong / cao anh) thi bat dau nhac; cham mep = nhac manh nhat.")]
    [Range(0.02f, 0.3f)]
    [SerializeField] private float _edgeBand = 0.12f;
    [Tooltip("Python khong gui anh moi co diem trong bay lau (giay) thi dung vi tri tay theo Quest.")]
    [SerializeField] private float _pointsTimeout = 0.3f;
    [Tooltip("Ca 2 nguon deu mat tay: giu vi tri cuoi bay lau (giay) roi tat nhac (vd tay da buong xuong).")]
    [SerializeField] private float _forgetSeconds = 1.5f;
    [Tooltip("Nhac hien / tat muot trong khoang nay (giay).")]
    [SerializeField] private float _fadeSeconds = 0.15f;

    [Header("Tay găng ảo")]
    [Tooltip("Renderer tay gang (material OculusHand). De trong = SkinnedMeshRenderer dang bat dau tien duoi object nay.")]
    [SerializeField] private Renderer _handRenderer;
    [SerializeField] private Color _cueColor = new Color(1f, 0.5f, 0.1f);
    [Tooltip("Mau tay nga sang _cueColor toi muc nay (0..1) khi nhac manh nhat.")]
    [Range(0f, 1f)]
    [SerializeField] private float _maxTint = 0.8f;
    [Tooltip("Do dac cua tay khi nhac manh nhat (material OculusHand goc: 0.79).")]
    [Range(0f, 1f)]
    [SerializeField] private float _minOpacity = 0.35f;

    [Header("Mũi tên + khung camera")]
    [Tooltip("Material TRONG SUOT (URP Unlit, Surface = Transparent) -- dung chung FingertipCue.mat. Mau dat qua _BaseColor.")]
    [SerializeField] private Material _material;
    [Tooltip("Mui ten va khung ve cach camera bay nhieu met (theo truc nhin cua camera).")]
    [SerializeField] private float _distance = 0.45f;
    [Tooltip("Chieu dai mui ten (met).")]
    [SerializeField] private float _arrowLength = 0.05f;
    [Tooltip("Do day net khung (met).")]
    [SerializeField] private float _lineWidth = 0.003f;
    [SerializeField] private Color _arrowColor = new Color(1f, 0.55f, 0.1f, 0.9f);
    [SerializeField] private Color _frameColor = new Color(1f, 0.55f, 0.1f, 0.5f);

    [Header("Chỉ đọc (xem trong Play mode)")]
    [SerializeField] private string _source = "";
    [Tooltip("0 = tay o giua anh, 1 = cham / ra ngoai mep.")]
    [SerializeField] private float _level;
    [SerializeField] private bool _outside;

    /// <summary>Muc nhac hien tai 0..1 (de ghi log).</summary>
    public float Level01 => _level;
    public bool Outside => _outside;

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorTopId = Shader.PropertyToID("_ColorTop");
    private static readonly int ColorBottomId = Shader.PropertyToID("_ColorBottom");
    private static readonly int OpacityId = Shader.PropertyToID("_Opacity");
    private static readonly Vector2[] Corners = { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };

    private readonly Vector2[] _uv = new Vector2[HandImageFit.Points];
    private readonly float[] _conf = new float[HandImageFit.Points];
    private int _lastFid;
    private float _lastPointsTime = -999f;
    private float _lastKnownTime = -999f;
    private Rect _lastBox;
    private Vector2 _handCenterUV;
    private float _frameLevel;

    // Vi tri / huong camera mau so voi mat (co dinh tren kinh) -- uoc luong dan, de ve khung khong tre theo anh
    private Vector3 _offP;
    private Quaternion _offR = Quaternion.identity;
    private bool _hasOffset;
    private Quaternion _prevHead = Quaternion.identity;

    private MaterialPropertyBlock _block;
    private Color _baseTop, _baseBottom;
    private float _baseOpacity = 1f;
    private bool _tinted;
    private Renderer _arrow;
    private LineRenderer _frame;

    private void Awake()
    {
        if (_receiver == null) _receiver = GetComponent<FingerUDPReceiver>();
        if (_head == null && Camera.main != null) _head = Camera.main.transform;
        if (_handRenderer == null)
        {
            foreach (var r in GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                _handRenderer = r;
                break;
            }
        }
        _block = new MaterialPropertyBlock();
        Material hm = _handRenderer != null ? _handRenderer.sharedMaterial : null;
        _baseTop = hm != null && hm.HasProperty(ColorTopId) ? hm.GetColor(ColorTopId) : Color.gray;
        _baseBottom = hm != null && hm.HasProperty(ColorBottomId) ? hm.GetColor(ColorBottomId) : Color.gray;
        _baseOpacity = hm != null && hm.HasProperty(OpacityId) ? hm.GetFloat(OpacityId) : 1f;

        if (_material != null)
        {
            var arrow = new GameObject("CameraFrameCue_Arrow");
            arrow.transform.SetParent(transform, false);
            arrow.AddComponent<MeshFilter>().sharedMesh = BuildArrowMesh();
            _arrow = arrow.AddComponent<MeshRenderer>();
            _arrow.sharedMaterial = _material;
            _arrow.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _arrow.enabled = false;

            var frame = new GameObject("CameraFrameCue_Frame");
            frame.transform.SetParent(transform, false);
            _frame = frame.AddComponent<LineRenderer>();
            _frame.sharedMaterial = _material;
            _frame.useWorldSpace = true;
            _frame.loop = true;
            _frame.positionCount = Corners.Length;
            _frame.widthMultiplier = _lineWidth;
            _frame.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _frame.enabled = false;
        }
    }

    private void OnDisable()
    {
        if (_tinted && _handRenderer != null)
        {
            _level = 0f;
            ApplyHandTint();
        }
        if (_arrow != null) _arrow.enabled = false;
        if (_frame != null) _frame.enabled = false;
    }

    private void LateUpdate()
    {
        if (_streamer == null) _streamer = FindAnyObjectByType<GloveLiveStreamer>();
        float dt = Time.deltaTime;
        UpdateCameraOffset(dt);

        float target = 0f;
        _outside = false;
        if (TryGetHandBox(out Rect box, out bool fromLast))
        {
            float margin = Mathf.Min(Mathf.Min(box.xMin, box.yMin), Mathf.Min(1f - box.xMax, 1f - box.yMax));
            target = Mathf.Clamp01((_edgeBand - margin) / _edgeBand);
            _handCenterUV = box.center;
            bool centerOut = _handCenterUV.x < 0f || _handCenterUV.x > 1f || _handCenterUV.y < 0f || _handCenterUV.y > 1f;
            // Mat tay o mep (vi tri cuoi con trong dai mep) = coi nhu da ra ngoai
            _outside = centerOut || (fromLast && margin < _edgeBand);
            if (_outside) target = 1f;
            else if (fromLast) target = 0f;
        }

        float rate = dt / Mathf.Max(_fadeSeconds, 1e-3f);
        _level = Mathf.MoveTowards(_level, target, rate);
        _frameLevel = Mathf.MoveTowards(_frameLevel, _outside ? 1f : 0f, rate);

        ApplyHandTint();
        UpdateArrow();
        UpdateFrame();
    }

    /// <summary>Khung bao ban tay tren anh (ti le anh, goc TREN-trai; co the vuot ra ngoai 0..1).</summary>
    private bool TryGetHandBox(out Rect box, out bool fromLast)
    {
        box = default;
        fromLast = false;
        float now = Time.time;

        if (_receiver != null && _receiver.TryGetPixelPoints(out int fid, _uv, _conf, out _, out _))
        {
            if (fid != _lastFid)
            {
                _lastFid = fid;
                _lastPointsTime = now;
            }
            if (now - _lastPointsTime <= _pointsTimeout)
            {
                // Lay ca 9 diem, ke ca diem doan nam ngoai anh (Python gui do tin 0 cho chung nhung van gui vi tri)
                Vector2 min = _uv[0], max = _uv[0];
                for (int i = 1; i < _uv.Length; i++)
                {
                    min = Vector2.Min(min, _uv[i]);
                    max = Vector2.Max(max, _uv[i]);
                }
                box = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
                return Remember(box, "python");
            }
        }

        if (_streamer != null && _streamer.TryGetLatestHandHint(out Vector2 c, out Vector2 palm, out float age) && age <= _pointsTimeout)
        {
            // ~1 long ban tay moi phia tam (khop goc ngon giua) = ca ban tay
            Vector2 half = Vector2.Max(palm, new Vector2(0.04f, 0.04f));
            box = new Rect(c - half, 2f * half);
            return Remember(box, "quest");
        }

        if (now - _lastKnownTime <= _forgetSeconds)
        {
            box = _lastBox;
            fromLast = true;
            _source = "last";
            return true;
        }
        _source = "none";
        return false;
    }

    private bool Remember(Rect box, string source)
    {
        _lastBox = box;
        _lastKnownTime = Time.time;
        _source = source;
        return true;
    }

    /// <summary>Uoc luong vi tri/huong camera mau so voi mat. Anh chup truoc vai chuc ms nen chi cap nhat luc dau
    /// gan dung yen, va lam muot -- gia tri that la hang so (camera gan cung tren kinh).</summary>
    private void UpdateCameraOffset(float dt)
    {
        if (_streamer == null || _head == null || !_streamer.TryGetLatestCameraPose(out Pose cam)) return;
        Quaternion inv = Quaternion.Inverse(_head.rotation);
        Quaternion r = inv * cam.rotation;
        Vector3 p = inv * (cam.position - _head.position);
        float headSpeed = dt > 0f ? Quaternion.Angle(_prevHead, _head.rotation) / dt : 0f;
        _prevHead = _head.rotation;
        if (!_hasOffset)
        {
            _offR = r;
            _offP = p;
            _hasOffset = true;
            return;
        }
        if (headSpeed > 30f) return;
        float a = 1f - Mathf.Exp(-dt / 1f);
        _offR = Quaternion.Slerp(_offR, r, a);
        _offP = Vector3.Lerp(_offP, p, a);
    }

    /// <summary>Diem tren tia qua uv (anh, goc TREN-trai), cach camera _distance theo truc nhin. false = chua co camera.</summary>
    private bool ImagePointWorld(Vector2 uv, out Vector3 world)
    {
        world = default;
        if (!_hasOffset || _head == null || _streamer == null || !_streamer.TryGetCameraLocalRay(uv, out Vector3 d)) return false;
        world = CameraPosition + CameraRotation * (d * (_distance / Mathf.Max(d.z, 0.2f)));
        return true;
    }

    private Vector3 CameraPosition => _head.position + _head.rotation * _offP;
    private Quaternion CameraRotation => _head.rotation * _offR;

    private void ApplyHandTint()
    {
        if (_handRenderer == null) return;
        if (_level <= 0f && !_tinted) return;    // khong nhac: de nguyen cho cac script khac
        _handRenderer.GetPropertyBlock(_block);
        float k = _level * _maxTint;
        _block.SetColor(ColorTopId, Color.Lerp(_baseTop, _cueColor, k));
        _block.SetColor(ColorBottomId, Color.Lerp(_baseBottom, _cueColor, k));
        _block.SetFloat(OpacityId, Mathf.Lerp(_baseOpacity, _minOpacity, _level));
        _handRenderer.SetPropertyBlock(_block);
        _tinted = _level > 0f;
    }

    private void UpdateArrow()
    {
        if (_arrow == null) return;
        // Mui ten nam TRONG khung, phia tay dang lech, chi ve giua anh
        Vector2 toCenter = new Vector2(0.5f, 0.5f) - _handCenterUV;
        Vector2 at = new Vector2(Mathf.Clamp(_handCenterUV.x, _edgeBand, 1f - _edgeBand),
                                 Mathf.Clamp(_handCenterUV.y, _edgeBand, 1f - _edgeBand));
        Vector3 pos = default;
        bool show = _level > 0.01f && toCenter.sqrMagnitude > 1e-6f && ImagePointWorld(at, out pos);
        _arrow.enabled = show;
        if (!show) return;

        Quaternion camR = CameraRotation;
        Vector3 dir = camR * new Vector3(toCenter.x, -toCenter.y, 0f);   // anh: y huong xuong
        Vector3 n = (pos - _head.position).normalized;
        dir = Vector3.ProjectOnPlane(dir, n).normalized;
        _arrow.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(n, Vector3.Cross(n, dir)));
        _arrow.transform.localScale = Vector3.one * _arrowLength;
        SetColor(_arrow, _arrowColor, _level);
    }

    private void UpdateFrame()
    {
        if (_frame == null) return;
        bool show = _frameLevel > 0.01f;
        for (int i = 0; show && i < Corners.Length; i++)
        {
            if (ImagePointWorld(Corners[i], out Vector3 p)) _frame.SetPosition(i, p);
            else show = false;
        }
        _frame.enabled = show;
        if (!show) return;
        _frame.widthMultiplier = _lineWidth;
        SetColor(_frame, _frameColor, _frameLevel);
    }

    private void SetColor(Renderer r, Color c, float level)
    {
        r.GetPropertyBlock(_block);
        _block.SetColor(BaseColorId, new Color(c.r, c.g, c.b, c.a * level));
        r.SetPropertyBlock(_block);
    }

    /// <summary>Mui ten trong mat phang XY, chi theo +X, dai 1 (scale = chieu dai that); ve ca 2 mat.</summary>
    private static Mesh BuildArrowMesh()
    {
        var mesh = new Mesh { name = "CameraFrameCueArrow" };
        mesh.vertices = new[]
        {
            new Vector3(-0.5f, -0.12f, 0f), new Vector3(0.1f, -0.12f, 0f), new Vector3(0.1f, 0.12f, 0f), new Vector3(-0.5f, 0.12f, 0f),
            new Vector3(0.1f, -0.3f, 0f), new Vector3(0.5f, 0f, 0f), new Vector3(0.1f, 0.3f, 0f),
        };
        mesh.triangles = new[]
        {
            0, 2, 1, 0, 3, 2, 4, 6, 5,     // mat nhin tu -Z (phia mat nguoi xem)
            0, 1, 2, 0, 2, 3, 4, 5, 6,     // mat sau
        };
        mesh.RecalculateBounds();
        return mesh;
    }
}
