using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;

public static class Tests
{
    static int pass, fail;
    static void Check(bool c, string m) { if (c) pass++; else { fail++; Console.WriteLine("FAIL: " + m); } }
    static bool Near(float a, float b, float t) => Math.Abs(a - b) <= t;

    static Transform Bone(Transform parent, string name, Vector3 pos, Quaternion rot)
    {
        var go = new GameObject(name); go.transform.SetParent(parent); go.transform.position = pos; go.transform.rotation = rot; return go.transform;
    }
    static void Call(object o, string m) => o.GetType().GetMethod(m, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Invoke(o, null);
    static T Field<T>(object o, string f) => (T)o.GetType().GetField(f, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
    static void SetField(object o, string f, object v) => o.GetType().GetField(f, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(o, v);

    public static int Main(string[] args)
    {
        // ---- 2. pad frame and torque conventions ----
        // Index, identity rotation, finger along +Z: rig flexes about +X, so the pad faces -Y.
        var fr = GloveEsp32Protocol.ComputePadFrame(new Pose(new Vector3(0, 0, 0.07f), Quaternion.identity),
                                                    new Pose(new Vector3(0, 0, 0.09f), Quaternion.identity), false, false, false);
        Check(Near(fr.N.y, -1, 1e-5f) && Near(fr.U.x, 1, 1e-5f) && Near(fr.V.z, 1, 1e-5f), $"index frame N={fr.N} U={fr.U} V={fr.V}");
        // Flexing about +X really does move the tip toward N (the definition of the pad side).
        Vector3 tip = new Vector3(0, 0, 0.02f), moved = Quaternion.AngleAxis(10, Vector3.right) * tip;
        Check(Vector3.Dot(moved - tip, fr.N) > 0, "positive rotation about the flex axis must move the tip toward N");
        var flipped = GloveEsp32Protocol.ComputePadFrame(new Pose(new Vector3(0, 0, 0.07f), Quaternion.identity),
                                                         new Pose(new Vector3(0, 0, 0.09f), Quaternion.identity), false, true, false);
        Check(Near(flipped.N.y, 1, 1e-5f) && Near(flipped.U.x, 1, 1e-5f) && Near(flipped.FlexAxis.x, -1, 1e-5f), "flip: N and flex axis reverse, U does not");
        var mirrored = GloveEsp32Protocol.ComputePadFrame(new Pose(new Vector3(0, 0, 0.07f), Quaternion.identity),
                                                          new Pose(new Vector3(0, 0, 0.09f), Quaternion.identity), false, false, true);
        Check(Near(mirrored.N.y, -1, 1e-5f) && Near(mirrored.U.x, -1, 1e-5f), "mirror: only U reverses");
        // Object under the index pushes it up (+Y): extension torque about the MCP = r * F.
        float tau = GloveEsp32Protocol.ExtensionTorque(Vector3.zero, Vector3.right, new Vector3(0, 0, 0.09f), new Vector3(0, 2, 0));
        Check(Near(tau, 0.18f, 1e-5f), "extension torque " + tau);
        tau = GloveEsp32Protocol.ExtensionTorque(Vector3.zero, Vector3.right, new Vector3(0, 0, 0.09f), new Vector3(0, -2, 0));
        Check(tau == 0f, "a force that flexes the finger must give 0 extension torque, got " + tau);

        // ---- 3. the component, end to end against the Python board simulator ----
        var glove = new GameObject("StaticHandModel_Right");
        var haptics = glove.AddComponent<HapticRenderer>();
        var solver = glove.AddComponent<ImageHandSolver>();
        var matlab = glove.AddComponent<GloveForceOutput>();
        var root = glove.transform;
        // index straight along +Z, pad facing -Y
        Bone(root, "XRHand_IndexProximal", new Vector3(0, 0, 0), Quaternion.identity);
        Bone(root, "XRHand_IndexIntermediate", new Vector3(0, 0, 0.045f), Quaternion.identity);
        Bone(root, "XRHand_IndexDistal", new Vector3(0, 0, 0.07f), Quaternion.identity);
        Bone(root, "XRHand_IndexTip", new Vector3(0, 0, 0.09f), Quaternion.identity);
        // thumb below it, pad facing +Y: rotate 90 deg about +Z so local +Y (thumb flex axis) points -X
        var q = Quaternion.AngleAxis(90, Vector3.forward);
        Bone(root, "XRHand_ThumbMetacarpal", new Vector3(0, -0.03f, 0), q);
        Bone(root, "XRHand_ThumbProximal", new Vector3(0, -0.03f, 0.03f), q);
        Bone(root, "XRHand_ThumbDistal", new Vector3(0, -0.03f, 0.065f), q);
        Bone(root, "XRHand_ThumbTip", new Vector3(0, -0.03f, 0.09f), q);
        // Ball between them: pushes the index up 2 N, tilted 20 deg toward +X; pushes the thumb down 1.5 N.
        float a = 20 * Mathf.Deg2Rad;
        haptics.State[1] = new HapticRenderer.FingerState { touching = true, pressure01 = 0.5f, forceNewton = new Vector3(2 * Mathf.Sin(a), 2 * Mathf.Cos(a), 0) };
        haptics.State[0] = new HapticRenderer.FingerState { touching = true, pressure01 = 0.4f, forceNewton = new Vector3(0, -1.5f, 0) };

        var outp = glove.AddComponent<GloveForceEsp32Output>();
        SetField(outp, "_dorsalGain", 1f);
        SetField(outp, "_padForceGain", 1f);   // check the physical torque; the gain is a plain multiplier
        Call(outp, "Awake");
        Call(outp, "OnEnable");
        Check(!matlab.enabled, "GloveForceOutput should have been switched off");

        float maxWhileOff = 0f, backAfter = 0f;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int frames = 0;
        while (sw.ElapsedMilliseconds < 2500)
        {
            Time.frameCount++; Time.unscaledTime = sw.ElapsedMilliseconds / 1000f;
            long ms = sw.ElapsedMilliseconds;
            haptics.enabled = !(ms > 1000 && ms < 1500);              // renderer switched off for a while
            if (ms > 1700) solver.Weight = 0f;                          // then tracking lost
            Call(outp, "LateUpdate"); frames++;
            var now = Field<GloveForceEsp32Output.FingerOutput[]>(outp, "_out");
            if (ms > 1050 && ms < 1450) maxWhileOff = Math.Max(maxWhileOff, Math.Max(now[0].pressN, now[1].pressN) + now[1].dorsalTorqueNm);
            if (ms > 1550 && ms < 1650) backAfter = Math.Max(backAfter, now[1].pressN);
            Thread.Sleep(14);
        }
        var o = Field<GloveForceEsp32Output.FingerOutput[]>(outp, "_out");
        var boards = Field<GloveForceEsp32Output.BoardStatus[]>(outp, "_boards");
        Console.WriteLine($"frames={frames}  thumb board {boards[0].ip} online={boards[0].online} rtt={boards[0].rttMs:F2}ms   index board {boards[1].ip} online={boards[1].online} rtt={boards[1].rttMs:F2}ms");
        Check(boards[0].online && boards[1].online, "both simulated boards should be discovered");
        Check(boards[1].rttMs > 0 && boards[1].rttMs < 50, "RTT to the relay on localhost: " + boards[1].rttMs);
        Check(Field<float>(outp, "_gate") < 0.01f, "gate should have faded to 0 after tracking loss");
        Check(maxWhileOff == 0f, "a disabled HapticRenderer must send zero, not its last force: " + maxWhileOff);
        Check(backAfter > 1f, "force should come back when the renderer is re-enabled: " + backAfter);
        Call(outp, "OnDisable");

        Console.WriteLine($"{pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
