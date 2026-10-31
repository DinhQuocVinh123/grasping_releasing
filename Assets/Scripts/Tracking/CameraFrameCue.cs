using UnityEngine;

/// <summary>
/// Reminds the user to keep the hand INSIDE the colour camera's view. The camera sees less than the view inside
/// the headset, so users still see their hand and often let it drift to the edge without noticing -> Python loses
/// points and the virtual hand twists.
///
/// Hand position in the image, in order of preference:
///   1. The 9 points from Python: accurate, including points the model guesses OUTSIDE the image;
///   2. Python does not see the hand: the Quest's hand centre projected into the image (GloveLiveStreamer; the
///      Quest's tracking cameras see wider than the colour camera);
///   3. both lost: the last known position, kept for _forgetSeconds.
///
/// Cue levels:
///   - hand in the middle of the image: nothing shown;
///   - hand in the edge band (_edgeBand): the virtual glove hand turns orange and fades, and an arrow at the edge
///     points towards the centre;
///   - hand outside the image: a faint rectangle also shows the area the camera sees.
/// Disable this component to turn the cues off (to compare in an experiment).
///
/// Put it on the same object as the glove hand's FingerUDPReceiver (StaticHandModel_Right).
/// </summary>
[DefaultExecutionOrder(400)]
public class CameraFrameCue : MonoBehaviour
{
    [Header("Sources")]
    [Tooltip("Empty = find on the same object.")]
    [SerializeField] private FingerUDPReceiver _receiver;
    [Tooltip("Empty = find in the scene.")]
    [SerializeField] private GloveLiveStreamer _streamer;
    [Tooltip("The viewer's eyes (CenterEyeAnchor). Empty = Camera.main.")]
    [SerializeField] private Transform _head;

    [Header("Safe area (fraction of the image)")]
    [Tooltip("The cue starts when the hand is closer than this to the image edge (fraction of the image width / height); touching the edge = strongest cue.")]
    [Range(0.02f, 0.3f)]
    [SerializeField] private float _edgeBand = 0.12f;
    [Tooltip("If Python sends no new image with points for this long (s), use the Quest's hand position.")]
    [SerializeField] private float _pointsTimeout = 0.3f;
    [Tooltip("Both sources lost the hand: keep the last position this long (s), then turn the cue off (e.g. the hand was lowered).")]
    [SerializeField] private float _forgetSeconds = 1.5f;
    [Tooltip("The cue fades in / out over this time (s).")]
    [SerializeField] private float _fadeSeconds = 0.15f;

    [Header("Virtual glove hand")]
    [Tooltip("Glove hand renderer (OculusHand material). Empty = the first enabled SkinnedMeshRenderer under this object.")]
    [SerializeField] private Renderer _handRenderer;
    [SerializeField] private Color _cueColor = new Color(1f, 0.5f, 0.1f);
    [Tooltip("The hand colour blends towards _cueColor up to this amount (0..1) at the strongest cue.")]
    [Range(0f, 1f)]
    [SerializeField] private float _maxTint = 0.8f;
    [Tooltip("Hand opacity at the strongest cue (original OculusHand material: 0.79).")]
    [Range(0f, 1f)]
    [SerializeField] private float _minOpacity = 0.35f;

    [Header("Arrow + camera frame")]
    [Tooltip("A TRANSPARENT material (URP Unlit, Surface = Transparent), shared with FingertipCue.mat. Colour set via _BaseColor.")]
    [SerializeField] private Material _material;
    [Tooltip("Distance (m) from the camera, along its view axis, at which the arrow and frame are drawn.")]
    [SerializeField] private float _distance = 0.45f;
    [Tooltip("Arrow length (m).")]
    [SerializeField] private float _arrowLength = 0.05f;
    [Tooltip("Frame line width (m).")]
    [SerializeField] private float _lineWidth = 0.003f;
    [SerializeField] private Color _arrowColor = new Color(1f, 0.55f, 0.1f, 0.9f);
    [SerializeField] private Color _frameColor = new Color(1f, 0.55f, 0.1f, 0.5f);

    [Header("Read only (watch in Play mode)")]
    [SerializeField] private string _source = "";
    [Tooltip("0 = hand in the middle of the image, 1 = touching / outside the edge.")]
    [SerializeField] private float _level;
    [SerializeField] private bool _outside;

    /// <summary>Current cue level 0..1 (for logging).</summary>
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

    // Pose of the colour camera relative to the eyes (fixed on the headset), estimated over time so the frame does not lag behind the image
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
            // Hand lost at the edge (last position still in the edge band) = treat as outside
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

    /// <summary>Bounding box of the hand in the image (image fractions, TOP-left origin; may extend outside 0..1).</summary>
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
                // Use all 9 points, including guessed points outside the image (Python sends confidence 0 for them but still sends the position)
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
            // About one palm length on each side of the centre (middle finger base joint) = the whole hand
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

    /// <summary>Estimates the colour camera's pose relative to the eyes. The image was taken tens of ms earlier, so this
    /// only updates while the head is nearly still, and is smoothed; the real value is constant (the camera is fixed to the headset).</summary>
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

    /// <summary>Point on the ray through uv (image, TOP-left origin), _distance from the camera along its view axis. false = no camera yet.</summary>
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
        if (_level <= 0f && !_tinted) return;    // no cue: leave the renderer to other scripts
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
        // The arrow sits INSIDE the frame, on the side the hand drifted to, pointing to the image centre
        Vector2 toCenter = new Vector2(0.5f, 0.5f) - _handCenterUV;
        Vector2 at = new Vector2(Mathf.Clamp(_handCenterUV.x, _edgeBand, 1f - _edgeBand),
                                 Mathf.Clamp(_handCenterUV.y, _edgeBand, 1f - _edgeBand));
        Vector3 pos = default;
        bool show = _level > 0.01f && toCenter.sqrMagnitude > 1e-6f && ImagePointWorld(at, out pos);
        _arrow.enabled = show;
        if (!show) return;

        Quaternion camR = CameraRotation;
        Vector3 dir = camR * new Vector3(toCenter.x, -toCenter.y, 0f);   // image y points down
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

    /// <summary>Arrow in the XY plane, pointing along +X, length 1 (scale = real length); both faces are drawn.</summary>
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
            0, 2, 1, 0, 3, 2, 4, 6, 5,     // face seen from -Z (the viewer's side)
            0, 1, 2, 0, 2, 3, 4, 5, 6,     // back face
        };
        mesh.RecalculateBounds();
        return mesh;
    }
}
