using UnityEngine;

/// <summary>
/// Shows force as FINGERTIP COLOUR: the virtual thumb / index tip goes from light red to deep red as the
/// finger presses harder; nothing is shown without contact. The cue sits right at the contact point, so the
/// user does not have to look elsewhere. Colouring the fingers is one of the visual cues Prachyabrued &amp;
/// Borst designed and evaluated for grasping in VR (IEEE TVCG 2016).
///
/// Each fingertip gets a transparent coloured cap that follows the VIRTUAL fingertip (after
/// FingertipSurfaceConstraint has kept the finger on the object's surface).
///
/// Put it on the same object as that hand's HapticRenderer and add it to the renderer's Outputs
/// (like any other IHapticGlove output).
/// </summary>
[DefaultExecutionOrder(350)] // after FingertipSurfaceConstraint (200): place the cap on the fingertip already pushed to the surface
public class FingertipColorCue : MonoBehaviour, IHapticGlove
{
    [Tooltip("Root of the VIRTUAL hand (e.g. OVRHandVisualLeft, StaticHandModel_Right). Tips are found by the names XRHand_ThumbTip / XRHand_IndexTip, in the ACTIVE skeleton.")]
    [SerializeField] private Transform _handRoot;
    [Tooltip("A TRANSPARENT material (URP Unlit, Surface = Transparent). The script sets the colour via _BaseColor.")]
    [SerializeField] private Material _material;
    [Tooltip("Cap radius (m). Slightly larger than the fingertip so it covers it.")]
    [SerializeField] private float _radius = 0.011f;
    [Tooltip("Just touching (force near 0): light, faint red.")]
    [SerializeField] private Color _lightColor = new Color(1f, 0.55f, 0.55f, 0.35f);
    [Tooltip("Maximum force (1): deep, solid red.")]
    [SerializeField] private Color _deepColor = new Color(0.7f, 0f, 0f, 0.9f);
    [Tooltip("Every event (touch, grab...) flashes briefly, so it is visible even when the force is very small.")]
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
            // MUST be removed IMMEDIATELY (DestroyImmediate), not with Destroy: Destroy only runs at the end of
            // the frame, and meanwhile FirstPersonLocomotor (in the camera rig) probes for the ground at start-up,
            // finds this collider, takes it for the floor and turns gravity on; when the collider disappears the
            // player falls forever.
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
                : 0f; // 1 = just flashed, 0 = over
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

    /// <summary>Finds the tip again if the skeleton in use was deactivated: HandVisual can switch between
    /// two skeletons (OpenXR / Oculus) at run time.</summary>
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
