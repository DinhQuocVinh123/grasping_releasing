using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Dents the mesh of a SOFT physics object (PhysicsPinchGrabbable with a soft depth) under the thumb and index fingertips, and lets
/// it spring back slowly when they leave, like foam.
///
/// PhysicsPinchGrabbable already lets the virtual fingertip sink into the surface (FingertipSurfaceConstraint places it there); this
/// script only makes the surface follow: every frame, for each fingertip touching the object, the vertices of the touched face around
/// the contact are pushed in by the fingertip's depth, with a bell-shaped falloff (a round dent the size of a finger pad).
///
/// The object is a box: on Awake the MeshFilter gets a subdivided unit cube (Unity's cube has only 24 vertices, which cannot dent), so
/// keep a BoxCollider of size 1 and a UNIFORM scale (x = y = z), like the PhysicsCube.
/// </summary>
[DefaultExecutionOrder(300)] // after FingertipSurfaceConstraint (200) has placed the virtual fingertips this frame
[RequireComponent(typeof(MeshFilter), typeof(PhysicsPinchGrabbable))]
public class SoftBodyDent : MonoBehaviour
{
    [Tooltip("Mesh subdivisions per box edge (vertices per face = (n + 1)^2).")]
    [Range(4, 32)]
    [SerializeField] private int _segments = 14;
    [Tooltip("Fingertip pad radius (m): the size of the dent, and how far the skin lies from the XRHand_*Tip point.")]
    [SerializeField] private float _padRadius = 0.008f;
    [Tooltip("Width of the dent's falloff, in pad radii (larger = a wider, shallower bowl around the fingertip).")]
    [SerializeField] private float _spread = 1.3f;
    [Tooltip("Time constant (s) of the surface following a fingertip pressing in.")]
    [SerializeField] private float _pressSeconds = 0.02f;
    [Tooltip("Time constant (s) of the surface springing back when the fingertip moves out (foam comes back slowly).")]
    [SerializeField] private float _recoverSeconds = 0.15f;

    /// <summary>Deepest dent this frame (m).</summary>
    public float DentMeters { get; private set; }

    private PhysicsPinchGrabbable _body;
    private Mesh _mesh;
    private Vector3[] _rest, _shown, _target;
    private float[] _shownDepth, _targetDepth;
    private readonly List<Transform> _tips = new List<Transform>();
    private bool _dirty;

    private void Awake() => Init();
    private void LateUpdate() => Step(Time.deltaTime);

    /// <summary>Builds the subdivided mesh (once). Public so the Editor lab can run it outside Play mode.</summary>
    public void Init()
    {
        if (_mesh != null) return;
        _body = GetComponent<PhysicsPinchGrabbable>();
        _mesh = BuildBox(_segments);
        GetComponent<MeshFilter>().sharedMesh = _mesh;
        _rest = _mesh.vertices;
        _shown = (Vector3[])_rest.Clone();
        _target = new Vector3[_rest.Length];
        _shownDepth = new float[_rest.Length];
        _targetDepth = new float[_rest.Length];
    }

    /// <summary>One frame: dents the mesh under the fingertips touching it, springs back elsewhere.</summary>
    public void Step(float deltaTime)
    {
        Init();
        float scale = Mathf.Max(Mathf.Abs(transform.lossyScale.x), 1e-6f);
        float r = _padRadius / scale;              // pad radius in local units (unit cube: -0.5 .. 0.5)
        float sigma = Mathf.Max(_spread * r * 0.6f, 1e-4f);
        float inv2s2 = 1f / (2f * sigma * sigma);
        float maxDepth = _body.SoftDepth > 0f ? _body.SoftDepth / scale : 0.5f * r;
        for (int i = 0; i < _rest.Length; i++) { _target[i] = Vector3.zero; _targetDepth[i] = 0f; }

        bool any = false;
        _body.GetFingertips(_tips);
        foreach (Transform tip in _tips)
        {
            Vector3 p = transform.InverseTransformPoint(tip.position);
            // Closest point on the (rest) box surface and the face it is on
            // (the face with the largest signed distance: outside = the face it sticks out of most, inside = the nearest face,
            // as PhysicsPinchGrabbable.FaceOf)
            int axis = 0;
            float best = float.MinValue;
            for (int a = 0; a < 3; a++)
            {
                float d = Mathf.Abs(p[a]) - 0.5f;
                if (d > best) { best = d; axis = a; }
            }
            Vector3 c = new Vector3(Mathf.Clamp(p.x, -0.5f, 0.5f), Mathf.Clamp(p.y, -0.5f, 0.5f), Mathf.Clamp(p.z, -0.5f, 0.5f));
            c[axis] = (p[axis] >= 0f ? 1f : -1f) * 0.5f;
            Vector3 n = Vector3.zero;
            n[axis] = Mathf.Sign(c[axis]);         // outward normal of the touched face
            float skinGap = Vector3.Dot(p - c, n) - r; // + = skin outside the surface
            float depth = Mathf.Min(-skinGap, maxDepth);
            if (depth <= 0f) continue;
            any = true;
            for (int i = 0; i < _rest.Length; i++)
            {
                Vector3 v = _rest[i];
                float off = Vector3.Dot(v - c, n);                       // 0 on the touched face
                if (off < -2f * r) continue;                             // other faces (behind the touched one) stay
                Vector3 lat = v - c - n * off;
                float w = Mathf.Exp(-lat.sqrMagnitude * inv2s2) * Mathf.Exp(-off * off * inv2s2);
                float push = depth * w;
                if (push > _targetDepth[i]) { _targetDepth[i] = push; _target[i] = -n * push; }
            }
        }

        if (!any && !_dirty) { DentMeters = 0f; return; }
        float dt = Mathf.Max(deltaTime, 1e-4f);
        float aIn = 1f - Mathf.Exp(-dt / Mathf.Max(_pressSeconds, 1e-3f)), aOut = 1f - Mathf.Exp(-dt / Mathf.Max(_recoverSeconds, 1e-3f));
        float deepest = 0f;
        bool moving = false;
        for (int i = 0; i < _rest.Length; i++)
        {
            float k = _targetDepth[i] > _shownDepth[i] ? aIn : aOut;
            _shownDepth[i] = Mathf.Lerp(_shownDepth[i], _targetDepth[i], k);
            Vector3 dir = _targetDepth[i] > 0f ? _target[i] / _targetDepth[i] : (_shown[i] - _rest[i]).normalized;
            _shown[i] = _rest[i] + dir * _shownDepth[i];
            deepest = Mathf.Max(deepest, _shownDepth[i]);
            moving |= _shownDepth[i] > 1e-6f;
        }
        DentMeters = deepest * scale;
        if (!moving)
        {
            System.Array.Copy(_rest, _shown, _rest.Length);
            System.Array.Clear(_shownDepth, 0, _shownDepth.Length);
        }
        _mesh.vertices = _shown;
        _mesh.RecalculateNormals();
        _mesh.RecalculateBounds();
        _dirty = moving;
    }

    /// <summary>A unit cube (-0.5 .. 0.5) with n x n quads per face; each face has its own vertices (sharp edges).</summary>
    private static Mesh BuildBox(int n)
    {
        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();
        for (int axis = 0; axis < 3; axis++)
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 normal = Vector3.zero; normal[axis] = side;
                Vector3 u = Vector3.zero, v = Vector3.zero;
                u[(axis + 1) % 3] = 1f; v[(axis + 2) % 3] = 1f;
                if (side < 0) u = -u; // keep the winding facing outwards
                int start = verts.Count;
                for (int j = 0; j <= n; j++)
                    for (int i = 0; i <= n; i++)
                    {
                        float a = (float)i / n - 0.5f, b = (float)j / n - 0.5f;
                        verts.Add(normal * 0.5f + u * a + v * b);
                        uvs.Add(new Vector2((float)i / n, (float)j / n));
                    }
                for (int j = 0; j < n; j++)
                    for (int i = 0; i < n; i++)
                    {
                        int q = start + j * (n + 1) + i;
                        // Unity front face: cross(b - a, c - a) points to the viewer -> (u, v) order gives the outward normal
                        tris.Add(q); tris.Add(q + 1); tris.Add(q + n + 1);
                        tris.Add(q + 1); tris.Add(q + n + 2); tris.Add(q + n + 1);
                    }
            }
        var mesh = new Mesh { name = "SoftBox" };
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh.MarkDynamic();
        return mesh;
    }
}
