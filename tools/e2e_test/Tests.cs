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

        // ---- 2b. dorsal actuator length from finger flexion ----
        float straight = GloveEsp32Protocol.FlexionRad(new Vector3(0, 0, -1), Vector3.zero, new Vector3(0, 0, 1), Vector3.right);
        Check(Near(straight, 0, 1e-5f), "straight joint must give 0 flexion, got " + straight);
        Vector3 bentNext = Quaternion.AngleAxis(30, Vector3.right) * new Vector3(0, 0, 1);
        float bent = GloveEsp32Protocol.FlexionRad(new Vector3(0, 0, -1), Vector3.zero, bentNext, Vector3.right);
        Check(Near(bent, 30 * Mathf.Deg2Rad, 1e-4f), "30 deg flexion (toward the pad) must read +0.5236 rad, got " + bent);
        float hyper = GloveEsp32Protocol.FlexionRad(new Vector3(0, 0, -1), Vector3.zero,
                                                    Quaternion.AngleAxis(-10, Vector3.right) * new Vector3(0, 0, 1), Vector3.right);
        Check(Near(hyper, -10 * Mathf.Deg2Rad, 1e-4f), "hyperextension must be negative, got " + hyper);
        {
            // Index bent 0 deg at the MCP, 30 at the PIP, 20 at the DIP -> 50 deg -> L = 80 + 10 mm/rad x 0.8727 = 88.73 mm before the 80 mm limit.
            var hand = new GameObject("BentHand").transform;
            var meta = Bone(hand, "XRHand_IndexMetacarpal", new Vector3(0, 0, -0.05f), Quaternion.identity);
            var prox = Bone(meta, "XRHand_IndexProximal", Vector3.zero, Quaternion.identity);
            Vector3 pip = new Vector3(0, 0, 0.045f);
            Vector3 dip = pip + Quaternion.AngleAxis(30, Vector3.right) * new Vector3(0, 0, 0.025f);
            Vector3 tipP = dip + Quaternion.AngleAxis(50, Vector3.right) * new Vector3(0, 0, 0.02f);
            var chain = new[] { prox, Bone(prox, "XRHand_IndexIntermediate", pip, Quaternion.identity),
                                Bone(prox, "XRHand_IndexDistal", dip, Quaternion.identity), Bone(prox, "XRHand_IndexTip", tipP, Quaternion.identity) };
            var probe = new GameObject("Probe").AddComponent<GloveForceEsp32Output>();
            float flex = (float)probe.GetType().GetMethod("DorsalFlexionRad", BindingFlags.Instance | BindingFlags.NonPublic)
                                     .Invoke(probe, new object[] { chain, false });
            Check(Near(flex * Mathf.Rad2Deg, 50f, 0.05f), "index flexion MCP->DIP should be 50 deg, got " + flex * Mathf.Rad2Deg);
            float L = Field<float>(probe, "_indexDorsalRestLengthMm") + Field<float>(probe, "_dorsalLengthPerRadMm") * flex;
            Check(Near(L, 88.73f, 0.05f), "L for a 50 deg bend should be 88.73 mm (sent unclamped; the relay clamps), got " + L);
        }

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
        Check(Near(o[1].dorsalLengthMm, 80f, 0.01f) && Near(o[0].dorsalLengthMm, 80f, 0.01f),
              $"straight fingers: L should be the rest length (80 mm for both), got thumb {o[0].dorsalLengthMm} index {o[1].dorsalLengthMm}");
        Check(maxWhileOff == 0f, "a disabled HapticRenderer must send zero, not its last force: " + maxWhileOff);
        Check(backAfter > 1f, "force should come back when the renderer is re-enabled: " + backAfter);
        // Packet layout: each finger's six numbers together (contact, P, U, V, T, L), version last.
        var cmd = Field<float[]>(outp, "_cmd");
        Check(cmd.Length == 16 && cmd[15] == 3f, "16 floats, version 3 at index 15");
        Check(cmd[3] == 1f && Near(cmd[4], o[0].pressN, 1e-6f) && Near(cmd[7], o[0].dorsalTorqueNm, 1e-6f) && Near(cmd[8], o[0].dorsalLengthMm, 1e-4f),
              $"thumb block 3..8: contact {cmd[3]} P {cmd[4]} T {cmd[7]} L {cmd[8]}");
        Check(cmd[9] == 1f && Near(cmd[10], o[1].pressN, 1e-6f) && Near(cmd[11], o[1].tiltN.x, 1e-6f) &&
              Near(cmd[13], o[1].dorsalTorqueNm, 1e-6f) && Near(cmd[14], o[1].dorsalLengthMm, 1e-4f),
              $"index block 9..14: contact {cmd[9]} P {cmd[10]} U {cmd[11]} T {cmd[13]} L {cmd[14]}");
        Call(outp, "OnDisable");

        Console.WriteLine($"{pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
