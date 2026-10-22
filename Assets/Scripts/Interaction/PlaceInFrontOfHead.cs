using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Places this object in front of the user, then leaves it FIXED in the world (it does not follow the head).
/// Used as the "home" (_home) of the squeezable objects: the object rests here, follows the hand while held,
/// and flies back here when released.
///
/// This has to happen at run time because the headset puts the origin on the FLOOR (Floor Level): in the
/// Editor we cannot know how tall the user is or where they stand.
///
/// Re-place: B button (right controller), or R while running in the Editor.
/// </summary>
public class PlaceInFrontOfHead : MonoBehaviour
{
    [Tooltip("The user's head (CenterEyeAnchor).")]
    [SerializeField] private Transform _head;
    [Tooltip("Position relative to the head (m): x = right, y = up, z = forward (along the horizontal view direction).")]
    [SerializeField] private Vector3 _offset = new Vector3(0f, -0.15f, 0.35f);
    [Tooltip("Wait this long after the app starts before the first placement (the headset needs a few frames to know the head pose).")]
    [SerializeField] private float _startDelay = 0.5f;
    [SerializeField] private bool _recenterWithButton = true;
    [Tooltip("Objects moved to their new home INSTANTLY (not flying back) on every re-place.")]
    [SerializeField] private SquishyPinchable[] _snapObjects;
    [Tooltip("Physics objects (PhysicsPinchGrabbable) put back at their home on every re-place.")]
    [SerializeField] private PhysicsPinchGrabbable[] _snapBodies;

    private IEnumerator Start()
    {
        yield return new WaitForSeconds(_startDelay);
        Place();
    }

    private void Update()
    {
        if (!_recenterWithButton) return;
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch) ||
            (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame))
        {
            Place();
        }
    }

    public void Place()
    {
        if (_head == null) return;

        // Horizontal view direction only: looking down while pressing must not put the object on the floor
        Vector3 forward = Vector3.ProjectOnPlane(_head.forward, Vector3.up);
        if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
        forward.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, forward);

        transform.SetPositionAndRotation(
            _head.position + right * _offset.x + Vector3.up * _offset.y + forward * _offset.z,
            Quaternion.LookRotation(forward, Vector3.up));

        if (_snapObjects != null)
            foreach (var obj in _snapObjects)
                if (obj != null) obj.ResetState();
        if (_snapBodies != null)
            foreach (var body in _snapBodies)
                if (body != null) body.ResetToHome();
    }
}
