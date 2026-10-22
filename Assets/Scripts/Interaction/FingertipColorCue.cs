using UnityEngine;

/// <summary>
/// Bao luc bang MAU DAU NGON: dau ngon cai / ngon tro cua tay AO chuyen tu
/// do nhat sang do dam khi an cang chat; khong cham thi khong hien gi.
/// Thay cho 2 thanh luc (HapticBarsDisplay): tin hieu nam ngay cho tiep xuc,
/// khong phai liec sang cho khac. To mau ngon tay la mot trong cac tin hieu
/// thi giac Prachyabrued &amp; Borst da thiet ke va danh gia cho cam nam trong
/// VR (IEEE TVCG 2016).
///
/// Moi dau ngon co 1 "chop" mau trong suot bam theo dau ngon AO (sau khi
/// FingertipSurfaceConstraint da giu ngon tren be mat vat).
///
/// Gan cung object voi HapticRenderer cua ban tay do va keo vao o Outputs
/// cua no (giong cac dau ra IHapticGlove khac).
/// </summary>
[DefaultExecutionOrder(350)] // sau FingertipSurfaceConstraint (200): dat chop dung cho dau ngon da duoc day ra be mat
public class FingertipColorCue : MonoBehaviour, IHapticGlove
{
    [Tooltip("Goc cua ban tay AO (vd OVRHandVisualLeft, StaticHandModel_Right). Dau ngon duoc tim theo ten XRHand_ThumbTip / XRHand_IndexTip, lay bo xuong dang BAT.")]
    [SerializeField] private Transform _handRoot;
    [Tooltip("Material TRONG SUOT (URP Unlit, Surface = Transparent). Mau do script dat qua _BaseColor.")]
    [SerializeField] private Material _material;
    [Tooltip("Ban kinh chop mau (met). Lon hon da ngon mot chut de trum len dau ngon.")]
    [SerializeField] private float _radius = 0.011f;
    [Tooltip("Luc vua cham (gan 0): do nhat, mo.")]
    [SerializeField] private Color _lightColor = new Color(1f, 0.55f, 0.55f, 0.35f);
    [Tooltip("Luc toi da (1): do dam, ro.")]
    [SerializeField] private Color _deepColor = new Color(0.7f, 0f, 0f, 0.9f);
    [Tooltip("Moi su kien (cham, cam...) loe sang thoang qua de van thay duoc ca khi luc rat nho.")]
    [SerializeField] private float _flashSeconds = 0.15f;

    private static readonly string[] TipNames = { "XRHand_ThumbTip", "XRHand_IndexTip" };
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    private readonly float[] _pressure = new float[2];
    private readonly float[] _flashStart = { -1f, -1f };
    private readonly Transform[] _tips = new Transform[2];
    private readonly Renderer[] _caps = new Renderer[2];
    private MaterialPropertyBlock _block;

    public void SetPressure(HapticFinger finger, float pressure01) => _pressure[(int)finger] = pressure01;

    public void PlayTransient(HapticFinger finger, HapticEventType type, HapticTransient transient)
    {
        _flashStart[(int)finger] = Time.time;
    }

    private void Awake()
    {
        _block = new MaterialPropertyBlock();
        for (int f = 0; f < 2; f++)
        {
            var cap = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            cap.name = "ColorCue_" + TipNames[f];
            // PHAI xoa NGAY (DestroyImmediate), khong dung Destroy: Destroy chi
            // chay cuoi khung hinh, trong luc do FirstPersonLocomotor (trong
            // camera rig) do mat dat luc khoi dong, THAY collider nay, tuong la
            // co san -> bat trong luc; collider bien mat -> nguoi choi roi mai.
            DestroyImmediate(cap.GetComponent<Collider>());
            cap.transform.SetParent(transform, false);
            var renderer = cap.GetComponent<Renderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.enabled = false;
            _caps[f] = renderer;
        }
    }

    private void LateUpdate()
    {
        for (int f = 0; f < 2; f++)
        {
            Renderer cap = _caps[f];
            Transform tip = FindTip(f);

            float flash = _flashStart[f] >= 0f
                ? 1f - Mathf.Clamp01((Time.time - _flashStart[f]) / Mathf.Max(_flashSeconds, 1e-3f))
                : 0f; // 1 = vua loe, 0 = het
            bool show = tip != null && (_pressure[f] > 0.001f || flash > 0f);
            cap.enabled = show;
            if (!show) continue;

            cap.transform.SetPositionAndRotation(tip.position, tip.rotation);
            cap.transform.localScale = Vector3.one * (2f * _radius / Mathf.Max(transform.lossyScale.x, 1e-6f));

            Color color = Color.Lerp(_lightColor, _deepColor, _pressure[f]);
            color = Color.Lerp(color, Color.white, 0.6f * flash);
            color.a = Mathf.Max(color.a, _lightColor.a * flash);
            cap.GetPropertyBlock(_block);
            _block.SetColor(BaseColorId, color);
            cap.SetPropertyBlock(_block);
        }
    }

    /// <summary>Tim lai neu bo xuong dang dung bi tat -- HandVisual co the doi
    /// giua 2 bo xuong (OpenXR / Oculus) luc chay.</summary>
    private Transform FindTip(int f)
    {
        if (_tips[f] != null && _tips[f].gameObject.activeInHierarchy) return _tips[f];
        _tips[f] = _handRoot != null ? FindActiveDeepChild(_handRoot, TipNames[f]) : null;
        return _tips[f];
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
