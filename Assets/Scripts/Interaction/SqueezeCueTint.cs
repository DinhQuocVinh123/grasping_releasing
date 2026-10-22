using UnityEngine;

/// <summary>
/// Visual cue for SQUEEZING TOO HARD: the object turns red as the REAL fingers press past the object's
/// maximum compression.
///
/// FingertipSurfaceConstraint keeps the VIRTUAL fingers on the surface, so the user cannot see how deep the
/// real fingers have gone into the object. Prachyabrued &amp; Borst (IEEE TVCG 2016): users prefer virtual
/// fingers held on the surface, but it helps to add a cue that shows where the real fingers are. This script
/// is that cue. Disable the component to compare with and without it.
///
/// Put it on the same object as SquishyPinchable.
/// </summary>
[DefaultExecutionOrder(300)]
[RequireComponent(typeof(SquishyPinchable))]
public class SqueezeCueTint : MonoBehaviour
{
    [SerializeField] private Color _cueColor = new Color(0.9f, 0.1f, 0.1f);
    [Tooltip("How far (m) the real fingers must go past the maximum compression for the object to be fully red.")]
    [SerializeField] private float _overshootForFullCue = 0.01f;
    [Tooltip("The colour only blends up to this amount (0..1), so the original colour stays recognisable.")]
    [Range(0f, 1f)]
    [SerializeField] private float _maxBlend = 0.8f;

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    private SquishyPinchable _pinchable;
    private Renderer _renderer;
    private MaterialPropertyBlock _block;
    private Color _baseColor;

    /// <summary>Current cue level, 0..1.</summary>
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
