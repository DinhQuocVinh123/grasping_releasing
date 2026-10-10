// Stand-ins for the grasping_releasing classes the new scripts touch, same public surface.
using UnityEngine;
public enum HapticFinger { Thumb = 0, Index = 1 }
public class HapticRenderer : MonoBehaviour
{
    [System.Serializable] public struct FingerState { public bool touching; public float depthMeters; public float pressure01; public object touchedObject; public Vector3 forceNewton; }
    public FingerState[] State = new FingerState[2];
    public FingerState GetState(HapticFinger f) => State[(int)f];
}
public class ImageHandSolver : MonoBehaviour { public float LastTrust { get; set; } = 1f; public float Weight { get; set; } = 1f; public bool Ready { get; set; } = true; }
public class GloveForceOutput : MonoBehaviour { }
public class FingertipSurfaceConstraint : MonoBehaviour
{
    public static System.Collections.Generic.Dictionary<Transform, Pose> Real = new System.Collections.Generic.Dictionary<Transform, Pose>();
    public static Pose TrackedPose(Transform bone) => Real.TryGetValue(bone, out Pose p) ? p : new Pose(bone.position, bone.rotation);
}
