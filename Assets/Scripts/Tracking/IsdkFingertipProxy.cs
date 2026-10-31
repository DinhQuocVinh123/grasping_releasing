using Oculus.Interaction;
using Oculus.Interaction.Input;
using UnityEngine;

/// <summary>
/// Reads the thumb and index fingertips from the Quest's own BARE-HAND tracking (Interaction SDK) and moves
/// two "fingertip" transforms to them, so SquishyPinchable also works with a bare hand, without the glove.
///
/// This is a separate script so that SquishyPinchable does not depend on the Meta SDK: with the glove hand,
/// SquishyPinchable reads XRHand_ThumbTip / IndexTip directly; with the bare hand it reads the two transforms
/// this script updates.
///
/// When the Quest loses the hand, the two transforms are DEACTIVATED; SquishyPinchable reads that as
/// "no hand" and lets go of the object.
/// </summary>
[DefaultExecutionOrder(50)] // before SquishyPinchable (100)
public class IsdkFingertipProxy : MonoBehaviour
{
    [Tooltip("Hand data source (e.g. SyntheticHandData under ComprehensiveInteractorsLeft). Use the same Hand that HandVisual draws, so the tips match the hand you see.")]
    [SerializeField, Interface(typeof(IHand))] private Object _hand;
    [SerializeField] private Transform _thumbTip;
    [SerializeField] private Transform _indexTip;
    [Tooltip("Optional: transform that follows the WRIST (hand root), so a held object moves with the wrist (SquishyPinchable, wrist field).")]
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
