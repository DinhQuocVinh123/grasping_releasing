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

    [Tooltip("Also re-place when the headset is put on (app started with the headset in hand) and when the user recenters " +
             "the view (hold the Meta button), so the objects are always in front of the user.")]
    [SerializeField] private bool _replaceOnMountAndRecenter = true;
    [Tooltip("Give up waiting for a head pose after this long (s) and place anyway.")]
    [SerializeField] private float _maxWaitForHead = 5f;

    private IEnumerator Start()
    {
        yield return new WaitForSeconds(_startDelay);
        // The head pose may still be the origin for a while after start: wait until the headset reports a real pose.
        float waited = 0f;
        while (_head != null && _head.localPosition.sqrMagnitude < 1e-6f && waited < _maxWaitForHead)
        {
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
        Place();
    }

    private OVRDisplay _display; // OVRManager.display only exists once OVRManager has woken up: subscribed lazily in Update

    private void OnEnable()
    {
        if (_replaceOnMountAndRecenter) OVRManager.HMDMounted += OnMountedOrRecentered;
    }

    private void OnDisable()
    {
        OVRManager.HMDMounted -= OnMountedOrRecentered;
        if (_display != null) _display.RecenteredPose -= OnMountedOrRecentered;
        _display = null;
    }

    private void OnMountedOrRecentered()
    {
        if (isActiveAndEnabled) StartCoroutine(PlaceNextFrames());
    }

    // The new head pose arrives a frame or two after the event.
    private IEnumerator PlaceNextFrames()
    {
        yield return null;
        yield return null;
        Place();
    }

    private void Update()
    {
        if (_replaceOnMountAndRecenter && _display == null && OVRManager.display != null)
        {
            _display = OVRManager.display;
            _display.RecenteredPose += OnMountedOrRecentered;
        }
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
