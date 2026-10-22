using Oculus.Interaction;
using Oculus.Interaction.Input;
using UnityEngine;

/// <summary>
/// Doc vi tri dau ngon cai + dau ngon tro tu hand tracking GOC cua Quest
/// (Interaction SDK) roi dat 2 transform "dau ngon" theo do -- de
/// SquishyPinchable dung duoc voi TAY TRAN, khong can gang.
///
/// Tach rieng thanh script nay de SquishyPinchable khong phu thuoc Meta SDK:
/// voi tay gang, SquishyPinchable doc thang xuong XRHand_ThumbTip/IndexTip;
/// voi tay tran, no doc 2 transform ma script nay cap nhat.
///
/// Khi Quest mat dau tay, 2 transform bi TAT -- SquishyPinchable hieu la
/// "khong con tay" va tha vat ra.
/// </summary>
[DefaultExecutionOrder(50)] // truoc SquishyPinchable (100)
public class IsdkFingertipProxy : MonoBehaviour
{
    [Tooltip("Nguon du lieu tay (vd SyntheticHandData trong ComprehensiveInteractorsLeft). Dung dung Hand ma HandVisual dang ve de dau ngon khop voi tay ban nhin thay.")]
    [SerializeField, Interface(typeof(IHand))] private Object _hand;
    [SerializeField] private Transform _thumbTip;
    [SerializeField] private Transform _indexTip;
    [Tooltip("Tuy chon: transform dat theo CO TAY (goc ban tay) -- de vat dang cam di theo co tay (SquishyPinchable, truong wrist).")]
    [SerializeField] private Transform _wrist;

    private IHand Hand;

    private void Awake()
    {
        Hand = _hand as IHand;
    }

    private void LateUpdate()
    {
        bool tracked = Hand != null && Hand.IsTrackedDataValid;
        SetTip(_thumbTip, tracked, HandJointId.HandThumbTip);
        SetTip(_indexTip, tracked, HandJointId.HandIndexTip);
        if (_wrist != null)
        {
            Pose root = default;
            bool ok = tracked && Hand.GetRootPose(out root);
            if (ok) _wrist.SetPositionAndRotation(root.position, root.rotation);
            if (_wrist.gameObject.activeSelf != ok) _wrist.gameObject.SetActive(ok);
        }
    }

    private void SetTip(Transform tip, bool tracked, HandJointId joint)
    {
        if (tip == null) return;

        if (tracked && Hand.GetJointPose(joint, out Pose pose))
        {
            tip.SetPositionAndRotation(pose.position, pose.rotation);
            if (!tip.gameObject.activeSelf) tip.gameObject.SetActive(true);
        }
        else if (tip.gameObject.activeSelf)
        {
            tip.gameObject.SetActive(false);
        }
    }
}
