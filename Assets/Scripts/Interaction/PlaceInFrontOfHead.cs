using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Dat object nay truoc mat nguoi dung roi de NGUYEN DO trong the gioi ao
/// (khong di theo dau). Dung lam cho "nha" (_home) cua vat bop duoc: vat
/// nam yen o day, bi cam thi di theo tay, tha ra thi bay ve day.
///
/// Can dat luc chay vi kinh dung goc toa do o SAN (Floor Level) -- dat san
/// trong Editor thi khong biet nguoi dung cao bao nhieu, dung o dau.
///
/// Dat lai: nut B (tay cam phai), hoac phim R khi chay trong Editor.
/// </summary>
public class PlaceInFrontOfHead : MonoBehaviour
{
    [Tooltip("Dau nguoi dung (CenterEyeAnchor).")]
    [SerializeField] private Transform _head;
    [Tooltip("Vi tri so voi dau (met): x = sang phai, y = cao/thap, z = ra xa (tinh theo huong nhin ngang).")]
    [SerializeField] private Vector3 _offset = new Vector3(0f, -0.15f, 0.35f);
    [Tooltip("Doi bao lau sau khi mo ung dung moi dat lan dau (kinh can vai khung hinh moi co vi tri dau).")]
    [SerializeField] private float _startDelay = 0.5f;
    [SerializeField] private bool _recenterWithButton = true;
    [Tooltip("Cac vat duoc dua NGAY ve cho moi (khong bay tu tu) moi khi dat lai.")]
    [SerializeField] private SquishyPinchable[] _snapObjects;
    [Tooltip("Vat dung physics (PhysicsPinchGrabbable) dua ve cho cu moi khi dat lai.")]
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

        // Chi lay huong nhin NGANG: cui dau luc bam cung khong dat vat xuong dat
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
