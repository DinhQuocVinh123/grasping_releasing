using UnityEngine;

/// <summary>
/// Holds references to the finger bones of the virtual hand model, found automatically by name
/// (no manual drag-and-drop needed). It does not move anything itself: FingerUDPReceiver and
/// HandImageFitBench read these bones to pose the glove hand.
///
/// Put it on the hand model's GameObject (StaticHandModel_Right).
/// </summary>
public class HandFingerRig : MonoBehaviour
{
    [Header("Thumb (3 bones)")]
    public Transform thumbMetacarpal;
    public Transform thumbProximal;
    public Transform thumbDistal;

    [Header("Index (3 moving joints)")]
    public Transform indexProximal;
    public Transform indexIntermediate;
    public Transform indexDistal;

    [Header("Middle")]
    public Transform middleProximal;
    public Transform middleIntermediate;
    public Transform middleDistal;

    [Header("Ring")]
    public Transform ringProximal;
    public Transform ringIntermediate;
    public Transform ringDistal;

    [Header("Little")]
    public Transform pinkyProximal;
    public Transform pinkyIntermediate;
    public Transform pinkyDistal;

    private void Awake()
    {
        AutoFindMissingBones();
    }

    /// <summary>Finds any bone not assigned by hand, using its exact name in the model.</summary>
    [ContextMenu("Auto Find Bones")]
    public void AutoFindMissingBones()
    {
        thumbMetacarpal   = thumbMetacarpal   ? thumbMetacarpal   : FindDeepChild(transform, "XRHand_ThumbMetacarpal");
        thumbProximal     = thumbProximal     ? thumbProximal     : FindDeepChild(transform, "XRHand_ThumbProximal");
        thumbDistal       = thumbDistal       ? thumbDistal       : FindDeepChild(transform, "XRHand_ThumbDistal");

        indexProximal     = indexProximal     ? indexProximal     : FindDeepChild(transform, "XRHand_IndexProximal");
        indexIntermediate = indexIntermediate ? indexIntermediate : FindDeepChild(transform, "XRHand_IndexIntermediate");
        indexDistal       = indexDistal       ? indexDistal       : FindDeepChild(transform, "XRHand_IndexDistal");

        middleProximal     = middleProximal     ? middleProximal     : FindDeepChild(transform, "XRHand_MiddleProximal");
        middleIntermediate = middleIntermediate ? middleIntermediate : FindDeepChild(transform, "XRHand_MiddleIntermediate");
        middleDistal       = middleDistal       ? middleDistal       : FindDeepChild(transform, "XRHand_MiddleDistal");

        ringProximal     = ringProximal     ? ringProximal     : FindDeepChild(transform, "XRHand_RingProximal");
        ringIntermediate = ringIntermediate ? ringIntermediate : FindDeepChild(transform, "XRHand_RingIntermediate");
        ringDistal       = ringDistal       ? ringDistal       : FindDeepChild(transform, "XRHand_RingDistal");

        // The model names the little finger "Little" (XRHand convention), not "Pinky".
        pinkyProximal     = pinkyProximal     ? pinkyProximal     : FindDeepChild(transform, "XRHand_LittleProximal");
        pinkyIntermediate = pinkyIntermediate ? pinkyIntermediate : FindDeepChild(transform, "XRHand_LittleIntermediate");
        pinkyDistal       = pinkyDistal       ? pinkyDistal       : FindDeepChild(transform, "XRHand_LittleDistal");
    }

    private static Transform FindDeepChild(Transform parent, string name)
    {
        foreach (Transform child in parent)
        {
            if (child.name == name) return child;
            Transform result = FindDeepChild(child, name);
            if (result != null) return result;
        }
        return null;
    }
}
