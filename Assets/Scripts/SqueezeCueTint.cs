using UnityEngine;

/// <summary>
/// Tin hieu thi giac khi BOP QUA MUC: vat do dan len theo muc ngon THAT da
/// an vuot qua do lun toi da cua vat.
///
/// Vi FingertipSurfaceConstraint giu ngon AO tren be mat, nguoi dung khong
/// thay ngon THAT da lot vao trong vat bao sau. Prachyabrued &amp; Borst (IEEE
/// TVCG 2016): nguoi dung THICH cach giu ngon ao tren be mat, nhung nen kem
/// them tin hieu cho biet ngon that dang o dau -- script nay la tin hieu do.
/// Tat component nay de so sanh co / khong co tin hieu.
///
/// Gan cung object voi SquishyPinchable.
/// </summary>
[DefaultExecutionOrder(300)]
[RequireComponent(typeof(SquishyPinchable))]
public class SqueezeCueTint : MonoBehaviour
{
    [SerializeField] private Color _cueColor = new Color(0.9f, 0.1f, 0.1f);
    [Tooltip("Ngon that vuot qua muc lun toi da bao nhieu (met) thi vat do HAN.")]
    [SerializeField] private float _overshootForFullCue = 0.01f;
    [Tooltip("Mau vat chi do toi muc nay (0..1) -- de van con nhan ra mau goc.")]
    [Range(0f, 1f)]
    [SerializeField] private float _maxBlend = 0.8f;

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    private SquishyPinchable _pinchable;
    private Renderer _renderer;
    private MaterialPropertyBlock _block;
    private Color _baseColor;

    /// <summary>Muc tin hieu hien tai 0..1.</summary>
    public float Cue01 { get; private set; }

    private void Awake()
    {
        _pinchable = GetComponent<SquishyPinchable>();
        _renderer = GetComponent<Renderer>();
        _block = new MaterialPropertyBlock();
        _baseColor = _renderer != null && _renderer.sharedMaterial != null && _renderer.sharedMaterial.HasProperty(BaseColorId)
            ? _renderer.sharedMaterial.GetColor(BaseColorId)
            : Color.white;
    }

    private void LateUpdate()
    {
        if (_renderer == null) return;

        Cue01 = Mathf.Clamp01(_pinchable.OvershootMeters / Mathf.Max(_overshootForFullCue, 1e-5f));
        _renderer.GetPropertyBlock(_block);
        _block.SetColor(BaseColorId, Color.Lerp(_baseColor, _cueColor, Cue01 * _maxBlend));
        _renderer.SetPropertyBlock(_block);
    }

    private void OnDisable()
    {
        if (_renderer != null) _renderer.SetPropertyBlock(null);
    }
}
