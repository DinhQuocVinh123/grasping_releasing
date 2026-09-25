using TMPro;
using UnityEngine;

/// <summary>
/// "Gang gia" bang HINH: 2 thanh luc (ngon cai / ngon tro) nam trong tam nhin,
/// dai ra theo luc va doi mau xanh -> do; loe trang moi khi co su kien; dong
/// chu cho biet su kien gan nhat. De NHIN thay dung tin hieu ma gang se nhan.
///
/// Cac thanh va chu duoc dung san trong scene (keo vao cac o ben duoi); script
/// chi keo dai thanh va doi mau, nen khong tao material/shader luc chay.
/// </summary>
[DefaultExecutionOrder(300)]
public class HapticBarsDisplay : MonoBehaviour, IHapticGlove
{
    [Header("Thanh lực (pivot ở giữa, kéo dài theo trục X local)")]
    [SerializeField] private Transform _thumbFill;
    [SerializeField] private Transform _indexFill;
    [Tooltip("Chieu dai thanh khi luc = 1 (don vi local cua parent).")]
    [SerializeField] private float _fullLength = 1f;
    [SerializeField] private TMP_Text _eventLabel;

    [Header("Màu")]
    [SerializeField] private Color _lowColor = new Color(0.25f, 0.85f, 0.4f);
    [SerializeField] private Color _highColor = new Color(0.95f, 0.25f, 0.2f);
    [SerializeField] private Color _flashColor = Color.white;
    [Tooltip("Thoi gian loe sang khi co su kien (giay).")]
    [SerializeField] private float _flashSeconds = 0.15f;

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    private readonly float[] _pressure = new float[2];
    private readonly float[] _flashStart = { -1f, -1f };
    private MaterialPropertyBlock _block;

    public void SetPressure(HapticFinger finger, float pressure01)
    {
        _pressure[(int)finger] = pressure01;
    }

    public void PlayTransient(HapticFinger finger, HapticEventType type, HapticTransient transient)
    {
        _flashStart[(int)finger] = Time.time;
        if (_eventLabel != null) _eventLabel.text = $"{type} {finger}  {transient.amplitude:F2}";
    }

    private void LateUpdate()
    {
        _block ??= new MaterialPropertyBlock();
        UpdateBar(_thumbFill, 0);
        UpdateBar(_indexFill, 1);
    }

    private void UpdateBar(Transform fill, int f)
    {
        if (fill == null) return;

        float flash = 1f; // 0 = vua loe, 1 = het loe
        if (_flashStart[f] >= 0f)
        {
            flash = Mathf.Clamp01((Time.time - _flashStart[f]) / Mathf.Max(_flashSeconds, 1e-3f));
        }

        // Dang loe thi hien it nhat 1 doan ngan, de van thay su kien xay ra
        // khi luc = 0 (vd luc roi tay).
        float shown = Mathf.Max(_pressure[f], flash < 1f ? 0.1f : 0.001f);

        // Keo dai tu mep trai: pivot o giua nen phai dich tam theo nua chieu dai
        float length = shown * _fullLength;
        Vector3 scale = fill.localScale;
        scale.x = length;
        fill.localScale = scale;
        Vector3 pos = fill.localPosition;
        pos.x = -_fullLength * 0.5f + length * 0.5f;
        fill.localPosition = pos;

        Color color = Color.Lerp(_lowColor, _highColor, _pressure[f]);
        if (flash < 1f) color = Color.Lerp(_flashColor, color, flash);

        if (fill.TryGetComponent(out Renderer renderer))
        {
            renderer.GetPropertyBlock(_block);
            _block.SetColor(BaseColorId, color);
            renderer.SetPropertyBlock(_block);
        }
    }
}
