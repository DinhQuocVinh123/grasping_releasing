using Oculus.Interaction;
using UnityEngine;

/// <summary>
/// Giu ngon tay AO khong xuyen vao trong vat bop duoc (SquishyPinchable).
///
/// Tay that khep lai duoc vi ngoai doi khong co vat that chan -- nhung tay
/// ao se dung lai dung tren be mat (ke ca khi be mat dang lun). Moi khung
/// hinh, SAU KHI tay ao da duoc dat dang (boi HandVisual / FingerUDPReceiver),
/// script kiem tra tung dau ngon: neu da ngon lot vao trong vat thi uon nguoc
/// cac dot ngon ra (IK kieu CCD) cho toi khi da ngon vua cham be mat.
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
    [Tooltip("So vong lap IK moi khung hinh. Nhieu hon = da ngon nam sat be mat chinh xac hon.")]
    [Range(1, 10)]
    [SerializeField] private int _iterations = 4;
    [Tooltip("Chi xu ly ngon cai + ngon tro (2 ngon dung de bop). Tat = xu ly ca 5 ngon.")]
    [SerializeField] private bool _thumbAndIndexOnly = false;

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

    public void Apply()
    {
        ConstrainedFingerCount = 0;
        System.Collections.Generic.IReadOnlyList<SquishyPinchable> objects =
            _objects != null && _objects.Length > 0 ? _objects : SquishyPinchable.Active;
        if (objects.Count == 0) return;
        EnsureChains();

        int count = _thumbAndIndexOnly ? 2 : _chains.Length;
        for (int f = 0; f < count; f++)
        {
            Transform[] chain = _chains[f];
            if (chain == null) continue;
            Transform tip = chain[chain.Length - 1];

            foreach (var obj in objects)
            {
                if (obj == null || !obj.isActiveAndEnabled) continue;
                if (obj.TryResolvePenetration(tip, out Vector3 target))
                {
                    SolveCcd(chain, target);
                    ConstrainedFingerCount++;
                    break;
                }
            }
        }
    }

    /// <summary>IK kieu CCD: lan luot xoay tung dot (tu goc ra ngoai) sao cho
    /// dau ngon chia ve phia diem can toi. Xoay dot GOC truoc de ngon "duoi ra"
    /// tu nhien nhu ngon that bi vat can, thay vi be nguoc rieng dot dau.</summary>
    private void SolveCcd(Transform[] chain, Vector3 target)
    {
        Transform tip = chain[chain.Length - 1];
        for (int iter = 0; iter < _iterations; iter++)
        {
            for (int j = 0; j < chain.Length - 1; j++)
            {
                Transform bone = chain[j];
                Vector3 toTip = tip.position - bone.position;
                Vector3 toTarget = target - bone.position;
                if (toTip.sqrMagnitude < 1e-10f || toTarget.sqrMagnitude < 1e-10f) continue;
                bone.rotation = Quaternion.FromToRotation(toTip, toTarget) * bone.rotation;
            }
            if ((tip.position - target).sqrMagnitude < 1e-7f) break; // sat trong ~0.3 mm
        }
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
