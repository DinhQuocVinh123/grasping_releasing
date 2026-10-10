// Minimal UnityEngine stand-in: just enough API, with Unity's math semantics, to compile and
// exercise the glove scripts outside the editor.
using System;
using System.Collections.Generic;
namespace UnityEngine
{
    public class Object { public string name = "obj"; }
    public class Component : Object
    {
        public GameObject gameObject; public Transform transform => gameObject.transform;
        public T GetComponent<T>() where T : class { foreach (var c in gameObject.comps) if (c is T t) return t; return null; }
        public Component GetComponent(string type) { foreach (var c in gameObject.comps) if (c.GetType().Name == type) return c; return null; }
    }
    public class Behaviour : Component { public bool enabled = true; public bool isActiveAndEnabled => enabled && gameObject.activeInHierarchy; }
    public class MonoBehaviour : Behaviour { }
    public class GameObject : Object
    {
        public Transform transform; public List<Component> comps = new List<Component>(); public bool activeSelf = true;
        public GameObject(string n) { name = n; transform = new Transform { gameObject = this }; transform.name = n; }
        public bool activeInHierarchy => activeSelf && (transform.parent == null || transform.parent.gameObject.activeInHierarchy);
        public T AddComponent<T>() where T : Component, new() { var c = new T { gameObject = this }; c.name = name; comps.Add(c); return c; }
    }
    public class Transform : Component, IEnumerable<Transform>
    {
        public Transform parent; public List<Transform> children = new List<Transform>();
        public Vector3 position; public Quaternion rotation = Quaternion.identity;
        public void SetParent(Transform p) { parent = p; p.children.Add(this); }
        public IEnumerator<Transform> GetEnumerator() => children.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => children.GetEnumerator();
    }
    public struct Vector2 { public float x, y; public Vector2(float x, float y) { this.x = x; this.y = y; } public static Vector2 operator *(Vector2 a, float d) => new Vector2(a.x * d, a.y * d); public float magnitude => Mathf.Sqrt(x * x + y * y); }
    public struct Vector4 { public float x, y, z, w; public Vector4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; } }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public float this[int i] => i == 0 ? x : i == 1 ? y : z;
        public static Vector3 zero => new Vector3(0, 0, 0); public static Vector3 up => new Vector3(0, 1, 0);
        public static Vector3 right => new Vector3(1, 0, 0); public static Vector3 forward => new Vector3(0, 0, 1);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator -(Vector3 a) => new Vector3(-a.x, -a.y, -a.z);
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator *(float d, Vector3 a) => a * d;
        public static Vector3 operator /(Vector3 a, float d) => new Vector3(a.x / d, a.y / d, a.z / d);
        public float sqrMagnitude => x * x + y * y + z * z; public float magnitude => Mathf.Sqrt(sqrMagnitude);
        public Vector3 normalized { get { float m = magnitude; return m > 1e-5f ? this / m : zero; } }
        public void Normalize() { this = normalized; }
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        public static Vector3 ProjectOnPlane(Vector3 v, Vector3 n) { float s = n.sqrMagnitude; return s < 1e-12f ? v : v - n * (Dot(v, n) / s); }
        public static Vector3 ClampMagnitude(Vector3 v, float m) => v.sqrMagnitude > m * m ? v.normalized * m : v;
        public override string ToString() => $"({x:F4}, {y:F4}, {z:F4})";
    }
    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity => new Quaternion(0, 0, 0, 1);
        public static Quaternion AngleAxis(float deg, Vector3 axis)
        { axis = axis.normalized; float h = deg * Mathf.Deg2Rad * 0.5f, s = Mathf.Sin(h); return new Quaternion(axis.x * s, axis.y * s, axis.z * s, Mathf.Cos(h)); }
        public static Quaternion operator *(Quaternion a, Quaternion b) => new Quaternion(
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y, a.w * b.y + a.y * b.w + a.z * b.x - a.x * b.z,
            a.w * b.z + a.z * b.w + a.x * b.y - a.y * b.x, a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);
        public static Vector3 operator *(Quaternion q, Vector3 v)
        { var u = new Vector3(q.x, q.y, q.z); var t = Vector3.Cross(u, v) * 2f; return v + t * q.w + Vector3.Cross(u, t); }
    }
    public struct Pose { public Vector3 position; public Quaternion rotation; public Pose(Vector3 p, Quaternion r) { position = p; rotation = r; } public static Pose identity => new Pose(Vector3.zero, Quaternion.identity); }
    public struct Color { public static Color yellow, red, green, magenta, white; }
    public static class Gizmos { public static Color color; public static void DrawRay(Vector3 a, Vector3 b) { } public static void DrawWireSphere(Vector3 a, float r) { } public static void DrawSphere(Vector3 a, float r) { } }
    public static class Mathf
    {
        public const float Deg2Rad = (float)(Math.PI / 180), Rad2Deg = (float)(180 / Math.PI);
        public static float Sqrt(float f) => (float)Math.Sqrt(f); public static float Sin(float f) => (float)Math.Sin(f); public static float Cos(float f) => (float)Math.Cos(f);
        public static float Atan2(float y, float x) => (float)Math.Atan2(y, x); public static float Max(float a, float b) => Math.Max(a, b); public static float Min(float a, float b) => Math.Min(a, b);
        public static float Abs(float a) => Math.Abs(a);
        public static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v; public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float InverseLerp(float a, float b, float v) => a != b ? Clamp01((v - a) / (b - a)) : 0f;
        public static float MoveTowards(float c, float t, float d) => Math.Abs(t - c) <= d ? t : c + Math.Sign(t - c) * d;
    }
    public static class Time { public static float deltaTime = 1f / 72f, unscaledTime; public static int frameCount; }
    public static class Application { public static bool isPlaying = true; public static string persistentDataPath = System.IO.Path.GetTempPath(); }
    public static class Debug
    {
        public static List<string> Log_ = new List<string>();
        public static void Log(object m, Object c = null) { Log_.Add("LOG " + m); Console.WriteLine("LOG " + m); }
        public static void LogWarning(object m, Object c = null) { Log_.Add("WARN " + m); Console.WriteLine("WARN " + m); }
        public static void LogError(object m, Object c = null) { Log_.Add("ERR " + m); Console.WriteLine("ERR " + m); }
    }
    public class SerializeField : Attribute { } public class HeaderAttribute : Attribute { public HeaderAttribute(string s) { } }
    public class TooltipAttribute : Attribute { public TooltipAttribute(string s) { } } public class RangeAttribute : Attribute { public RangeAttribute(float a, float b) { } }
    public class DefaultExecutionOrder : Attribute { public DefaultExecutionOrder(int o) { } }
}
