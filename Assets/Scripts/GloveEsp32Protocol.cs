using UnityEngine;

/// <summary>
/// The contract between Unity and the two ESP32 boards (firmware/glove_finger/config.h).
/// Change a number here, change it there. Nothing in this file touches the scene, so it can
/// be tested outside Play mode.
///
/// Unity -> each ESP32, port 5011, 14 float32 little-endian (56 bytes). Both boards get the
/// same packet; each reads only its own finger (FINGER_ID in config.h).
///   0 sequence | 1 Unity time (s) | 2 tracking trust 0..1
///   3..6   thumb:  contact 0/1, pressing force P (N), tilt u (N), tilt v (N)
///   7..10  index:  contact 0/1, pressing force P (N), tilt u (N), tilt v (N)
///   11 thumb dorsal EXTENSION torque (N*m, >= 0) | 12 index dorsal extension torque
///   13 protocol version (boards drop packets whose version does not match)
///
/// Each ESP32 -> Unity, port 5012, 16 float32 (64 bytes). Broadcast until a command
/// arrives, unicast to the sender after that.
///   0 echoed sequence | 1 ESP32 time (s) | 2 finger id (0 thumb, 1 index) | 3 fault bits (Fault*)
///   4 age of the echoed command when this packet left (s) | 5 trust echo
///   6..13  target kPa, measured kPa for channels A, B, C, dorsal
///   14 pressing force the board is rendering (N) | 15 protocol version
/// </summary>
public static class GloveEsp32Protocol
{
    public const int Version = 2;
    public const int CommandPort = 5011;
    public const int TelemetryPort = 5012;

    // ---- command packet ----------------------------------------------------
    public const int CmdFloats = 14;
    public const int CmdBytes = CmdFloats * 4;
    public const int CmdSeq = 0, CmdTime = 1, CmdTrust = 2;
    public const int CmdThumbBase = 3, CmdIndexBase = 7; // +0 contact, +1 P, +2 tilt u, +3 tilt v
    public const int CmdThumbTorque = 11, CmdIndexTorque = 12;
    public const int CmdVersion = 13;

    // ---- telemetry packet --------------------------------------------------
    public const int TlmFloats = 16;
    public const int TlmBytes = TlmFloats * 4;
    public const int TlmEchoSeq = 0, TlmEspTime = 1, TlmFinger = 2, TlmFaults = 3;
    public const int TlmEchoAge = 4, TlmTrust = 5, TlmChannelBase = 6, TlmPressForce = 14, TlmVersion = 15;
    public const int ChannelsPerBoard = 4; // A, B, C, dorsal
    public static readonly string[] ChannelNames = { "A", "B", "C", "dorsal" };

    // ---- fault bits (FAULT_* in config.h) ----------------------------------
    public const int FaultStallMask = 0x0F;    // bit 0..3: that channel stalled
    public const int FaultLinkLost = 1 << 4;   // no valid command within COMMAND_TIMEOUT_MS
    public const int FaultOverPressure = 1 << 5;
    public const int FaultSensor = 1 << 6;     // a pressure reading out of its plausible range
    public const int FaultEStop = 1 << 7;      // pressed, or a normally-closed button not wired
    public const int FaultTravel = 1 << 8;     // forward travel budget used up
    // bit 9 unused
    public const int FaultStallLatched = 1 << 10; // a channel is latched off: serial 'r' on the board

    // ---- chamber layout: a preview of what the firmware will do ------------
    // Angles from the pad's +u axis toward +v. A sits toward the fingertip, C on the +u
    // side, B on the -u side. Must match CHAMBER_ANGLE_DEG in config.h.
    public static readonly float[] ChamberAngleDeg = { 90f, 210f, 330f };
    public const float ChamberRadius = 0.006f;     // CHAMBER_RADIUS_M
    public const float TiltFullScaleDeg = 30f;     // TILT_FULL_SCALE_DEG
    public const float ChamberForceMax = 1.6f / 3f; // CHAMBER_INV_FORCE_MAX_N: one actuator's maximum
    public static readonly bool ConservativeWorkspace = true; // CONSERVATIVE_WORKSPACE

    /// <summary>
    /// Mirror of chamberSplit() in ChamberSplit.h, for the inspector preview and for tests.
    /// The firmware is the one that counts.
    ///
    /// Sum of the three forces = P. The centre of pressure moves AWAY from the tilt
    /// direction (the pad sinks deeper on the far side of a tilted surface), by an amount
    /// proportional to the tilt ANGLE atan2(|tilt|, P) -- not to the tilt force, so a light
    /// touch on a slope shifts the load as far as a firm one.
    /// </summary>
    public static void ChamberSplit(float P, float tiltU, float tiltV, float[] f)
    {
        f[0] = f[1] = f[2] = 0f;
        if (!(P > 0f)) return;

        float r = ChamberRadius;
        float dx = 0f, dy = 0f;
        float tm = Mathf.Sqrt(tiltU * tiltU + tiltV * tiltV);
        if (tm > 1e-9f)
        {
            float angle = Mathf.Atan2(tm, P) * Mathf.Rad2Deg;
            float frac = Mathf.Clamp01(angle / TiltFullScaleDeg);
            float reach = (ConservativeWorkspace ? 0.5f * r : r) * frac;
            dx = -tiltU / tm * reach;
            dy = -tiltV / tm * reach;
        }

        // Actuators only pull the tactor onto the pad: f_i >= 0 needs d.p_i >= -r^2/2. Scale d back inside that triangle.
        float s = 1f;
        for (int i = 0; i < 3; i++)
        {
            float a = ChamberAngleDeg[i] * Mathf.Deg2Rad;
            float proj = dx * r * Mathf.Cos(a) + dy * r * Mathf.Sin(a);
            if (proj < -1e-12f) s = Mathf.Min(s, (r * r * 0.5f) / -proj);
        }
        dx *= s; dy *= s;

        for (int i = 0; i < 3; i++)
        {
            float a = ChamberAngleDeg[i] * Mathf.Deg2Rad;
            float proj = dx * r * Mathf.Cos(a) + dy * r * Mathf.Sin(a);
            f[i] = Mathf.Max(0f, P / 3f * (1f + 2f * proj / (r * r)));
        }

        // Per-actuator limit: scale all three together so the shift survives saturation.
        float fmax = Mathf.Max(f[0], Mathf.Max(f[1], f[2]));
        if (fmax > ChamberForceMax)
        {
            float k = ChamberForceMax / fmax;
            for (int i = 0; i < 3; i++) f[i] *= k;
        }
    }

    /// <summary>Pad frame of one fingertip, world space.</summary>
    public struct PadFrame
    {
        public Vector3 Origin; // the fingertip point HapticRenderer measures depth at
        public Vector3 N;      // out of the pad (palmar side), toward whatever the pad presses on
        public Vector3 U;      // across the pad: chamber C side
        public Vector3 V;      // along the distal bone, toward the fingertip: chamber A side
        public Vector3 FlexAxis; // + rotation about this flexes the finger (tip moves toward N)
    }

    /// <summary>
    /// Pad frame from the REAL distal-bone and tip poses.
    ///
    /// The rig flexes the four fingers about each bone's local +X and the thumb about +Y
    /// (FingertipSurfaceConstraint.FlexAxis). A positive rotation about axis a moves the tip
    /// along a x d, so the pad faces N = a x d. <paramref name="flip"/> reverses the flexion
    /// sense (pad normal AND dorsal torque together); <paramref name="mirrorU"/> swaps which
    /// side chambers B and C sit on, nothing else.
    /// </summary>
    public static PadFrame ComputePadFrame(Pose distal, Pose tip, bool thumb, bool flip, bool mirrorU)
    {
        Vector3 d = tip.position - distal.position;
        d = d.sqrMagnitude > 1e-10f ? d.normalized : distal.rotation * Vector3.forward;

        Vector3 rigAxis = distal.rotation * (thumb ? Vector3.up : Vector3.right);
        Vector3 a = Vector3.ProjectOnPlane(rigAxis, d);
        a = a.sqrMagnitude > 1e-10f ? a.normalized : Vector3.Cross(d, Vector3.up).normalized;

        Vector3 flex = flip ? -a : a;
        return new PadFrame
        {
            Origin = tip.position,
            N = Vector3.Cross(flex, d),
            U = mirrorU ? -a : a,
            V = d,
            FlexAxis = flex,
        };
    }

    /// <summary>
    /// Torque that EXTENDS the finger about a joint, for a force F applied at point p.
    /// Positive rotation about flexAxis is flexion, and the work of F over a small rotation
    /// d(theta) is d(theta) * flexAxis . (r x F) -- so that dot product is the flexing torque.
    /// The object pushes the finger open, which is negative flexing torque; the dorsal
    /// actuator renders its magnitude. Clamped at zero, not absolute-valued: a force that
    /// would flex the finger closed is not something the dorsal actuator can render: it contracts along
    /// the back of the finger like an extensor tendon, so it can only pull the finger open.
    /// </summary>
    public static float ExtensionTorque(Vector3 pivot, Vector3 flexAxis, Vector3 point, Vector3 force)
    {
        float flexing = Vector3.Dot(flexAxis, Vector3.Cross(point - pivot, force));
        return Mathf.Max(0f, -flexing);
    }
}
