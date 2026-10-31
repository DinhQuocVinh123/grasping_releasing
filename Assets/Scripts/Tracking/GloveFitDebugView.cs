using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Debug view in the headset, to see WHERE the virtual hand differs from the model's points:
/// - a thin line (ray) from the camera through each of the model's 9 image points, as the solver receives it (wrist white,
///   thumb orange, index magenta; points the solver ignores are not drawn);
/// - a sphere on each of the 9 joints the solver chose (raw, before filtering) -- on its ray = the fit is right in the image.
/// Compare with the drawn hand: sphere off its ray = the solver is wrong; sphere on its ray but not on the glove = wrong depth or
/// the palm turned; everything on the real glove but the mesh looks off = the filtering or the eye's viewpoint.
/// Press the left thumbstick (or V on the keyboard) to show / hide.
/// </summary>
[DefaultExecutionOrder(500)] // after FingerUDPReceiver / ImageHandSolver (0): this frame's solve is done
public class GloveFitDebugView : MonoBehaviour
{
    [Tooltip("Empty = find in the scene.")]
    [SerializeField] private ImageHandSolver _solver;
    [Tooltip("Unlit material with vertex colours (Sprites/Default). Empty = created at start.")]
    [SerializeField] private Material _material;
    [SerializeField] private bool _visible = true;
    [Tooltip("How far each ray is drawn from the camera (m).")]
    [SerializeField] private float _rayLength = 0.6f;
    [SerializeField] private float _rayWidth = 0.0015f;
    [Tooltip("Diameter of the solved joint spheres (m).")]
    [SerializeField] private float _jointSize = 0.008f;
    [Tooltip("Passthrough check: a controller model drawn where the headset tracks each controller (OVRControllerPrefab; empty = off). " +
             "Hold a controller as close as the gloved hand: if even this model is off the real controller, the offset is the " +
             "passthrough at close range, not the hand solve.")]
    [SerializeField] private GameObject _controllerModel;

    private const int N = HandImageFit.Points;
    private static readonly Color Wrist = Color.white, Thumb = new Color(1f, 0.55f, 0f), Index = new Color(1f, 0.1f, 0.9f);

    private readonly LineRenderer[] _rays = new LineRenderer[N];
    private readonly Transform[] _joints = new Transform[N];
    private readonly Vector3[] _dirs = new Vector3[N], _solved = new Vector3[N];
    private readonly float[] _w = new float[N];
    private Transform _root;
    private readonly System.Collections.Generic.List<GameObject> _controllers = new System.Collections.Generic.List<GameObject>();

    private static Color PointColor(int i) => i == 0 ? Wrist : i <= 4 ? Thumb : Index;

    private void Start()
    {
        if (_solver == null) _solver = FindAnyObjectByType<ImageHandSolver>();
        if (_material == null) _material = new Material(Shader.Find("Sprites/Default"));
        // World space, not under the head: rays and joints stay where the camera saw them
        _root = new GameObject("GloveFitDebugView (objects)").transform;
        for (int i = 0; i < N; i++)
        {
            _rays[i] = MakeLine("ray " + i, PointColor(i));
            _joints[i] = MakeSphere("joint " + i, PointColor(i));
        }
        SetAll(false);

        var rig = FindAnyObjectByType<OVRCameraRig>();
        if (_controllerModel != null && rig != null)
        {
            AddController(rig.rightControllerAnchor, OVRInput.Controller.RTouch);
            AddController(rig.leftControllerAnchor, OVRInput.Controller.LTouch);
        }
    }

    private void AddController(Transform anchor, OVRInput.Controller which)
    {
        if (anchor == null) return;
        var go = Instantiate(_controllerModel, anchor, false); // Start has not run yet: the hand can still be set
        var helper = go.GetComponent<OVRControllerHelper>();
        if (helper != null) helper.m_controller = which;
        _controllers.Add(go);
    }

    private LineRenderer MakeLine(string name, Color c)
    {
        var lr = new GameObject(name).AddComponent<LineRenderer>();
        lr.transform.SetParent(_root, false);
        lr.sharedMaterial = _material;
        lr.positionCount = 2;
        lr.startWidth = lr.endWidth = _rayWidth;
        lr.startColor = lr.endColor = c;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        return lr;
    }

    private Transform MakeSphere(string name, Color c)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        Destroy(go.GetComponent<Collider>()); // must not touch the grabbable objects
        go.transform.SetParent(_root, false);
        go.transform.localScale = Vector3.one * _jointSize;
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = _material;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        // Sprites/Default takes its colour from _Color via a property block (no material copy per sphere)
        var block = new MaterialPropertyBlock();
        block.SetColor("_Color", c);
        r.SetPropertyBlock(block);
        return go.transform;
    }

    private void SetAll(bool on)
    {
        for (int i = 0; i < N; i++)
        {
            _rays[i].enabled = on;
            _joints[i].gameObject.SetActive(on);
        }
    }

    private void LateUpdate()
    {
        if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.LTouch) ||
            (Keyboard.current != null && Keyboard.current.vKey.wasPressedThisFrame))
            _visible = !_visible;
        foreach (var c in _controllers)
            if (c != null && c.activeSelf != _visible) c.SetActive(_visible);
        if (_root == null) return;
        if (!_visible || _solver == null || !_solver.TryGetDebugView(out Pose cam, _dirs, _w, _solved))
        {
            SetAll(false);
            return;
        }
        for (int i = 0; i < N; i++)
        {
            bool used = _w[i] > 0f;
            _rays[i].enabled = used;
            if (used)
            {
                _rays[i].SetPosition(0, cam.position);
                _rays[i].SetPosition(1, cam.position + _dirs[i].normalized * _rayLength);
            }
            _joints[i].gameObject.SetActive(true);
            _joints[i].position = _solved[i];
        }
    }

    private void OnDestroy()
    {
        if (_root != null) Destroy(_root.gameObject);
    }
}
