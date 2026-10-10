using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Sends the glove's force targets out over UDP. Replaces GloveForceOutput (text to MATLAB).
/// Current setup: the PC relay (tools/glove_serial_bridge.py) receives them and forwards
/// them to the Arduino Mega over USB. The relay answers like two boards (thumb and index),
/// so the same script also drives Wi-Fi boards if you ever go back to those.
///
/// Per finger, every frame:
///   1. Force vector F: HapticRenderer.forceNewton -- the god-object force, along the surface
///      normal, magnitude from how deep the REAL fingertip sits in the object.
///   2. Pad frame (N, U, V) from the REAL fingertip orientation, i.e. before
///      FingertipSurfaceConstraint bends the visible finger onto the surface
///      (FingertipSurfaceConstraint.TrackedPose).
///   3. Pressing force P = max(0, -F.N): what the three chambers push with in total.
///      Tilt (F.U, F.V): the part of F lying in the pad plane. Non-zero exactly when the
///      surface normal and the pad normal disagree; the board turns its ANGLE into a shift of
///      the centre of pressure across the three chambers.
///   4. Dorsal torque: the extension torque F exerts about the finger's MCP joint.
///
/// Runs after HapticRenderer (150, force for this frame) and FingertipSurfaceConstraint
/// (200, real-pose cache for this frame). Put it on the glove hand, next to HapticRenderer.
///
/// SAFETY: a packet goes out every frame even with nothing touched -- it is the heartbeat.
/// Each board returns every actuator to rest (no force) if no valid packet arrives for 200 ms.
/// </summary>
[DefaultExecutionOrder(210)]
public class GloveForceEsp32Output : MonoBehaviour
{
    [Header("Sources (empty = find on this object)")]
    [SerializeField] private HapticRenderer _haptics;
    [Tooltip("Gates force on tracking: when the image solver loses the glove, the hand falls back to " +
             "Quest tracking, which cannot see gloved fingers -- force from that pose is noise. Empty = no gate.")]
    [SerializeField] private ImageHandSolver _solver;

    [Header("Boards")]
    [Tooltip("Leave empty: the PC relay announces itself (as both boards) and is found automatically. " +
             "Fill in the PC's IP in both fields only if discovery is blocked on your network.")]
    [SerializeField] private string _thumbBoardIp = "";
    [SerializeField] private string _indexBoardIp = "";
    [Tooltip("0 = send every rendered frame (recommended). Otherwise a cap in Hz (never below 20).")]
    [SerializeField] private float _maxSendRateHz = 0f;

    [Header("Pad frame (check with gizmos in Play mode)")]
    [Tooltip("Tick if the yellow pad-normal gizmo on the THUMB points out of the nail instead of the pad. " +
             "Also reverses the thumb's dorsal-torque sign, which shares the same flexion axis.")]
    [SerializeField] private bool _flipThumb = false;
    [SerializeField] private bool _flipIndex = false;
    [Tooltip("Tick if chambers B and C are swapped on the thumb module. Red gizmo (+U) must point at chamber C.")]
    [SerializeField] private bool _mirrorThumbU = false;
    [SerializeField] private bool _mirrorIndexU = false;

    [Header("Dorsal torque")]
    [SerializeField] private bool _thumbDorsal = true;
    [SerializeField] private bool _indexDorsal = true;
    [Tooltip("Which joint of the chain the dorsal actuator acts about. Thumb chain: 0 Metacarpal (CMC), 1 Proximal (MCP), 2 Distal (IP).")]
    [Range(0, 2)] [SerializeField] private int _thumbDorsalJoint = 1;
    [Tooltip("Index chain: 0 Proximal (MCP), 1 Intermediate (PIP), 2 Distal (DIP).")]
    [Range(0, 2)] [SerializeField] private int _indexDorsalJoint = 0;

    [Header("Scaling and limits")]
    [Tooltip("Multiplies the virtual force before anything else. 1 = the newtons HapticRenderer computes.")]
    [SerializeField] private float _forceGain = 1f;
    [Tooltip("Scales the fingertip pressing force and tilt (both, so the tilt ANGLE is unchanged) to fit the module. " +
             "Default 0.4 = 1.6 N module maximum / 4 N virtual maximum. Full tilt fits without saturating only up to " +
             "P = 0.8 N; above that the board scales all three actuators down together, keeping the shift.")]
    [SerializeField] private float _padForceGain = 0.4f;
    [Tooltip("Upper bound on |F| per finger, N. Guards against a bad pose producing a huge depth.")]
    [SerializeField] private float _maxForceNewton = 6f;
    [Tooltip("Upper bound on the dorsal torque, N*m.")]
    [SerializeField] private float _maxDorsalTorqueNm = 0.5f;
    [Tooltip("Scales the dorsal torque before it is sent, so the virtual range fits the actuator. " +
             "Set it to (actuator max force x DORSAL_MOMENT_ARM_M) / (largest virtual torque you want rendered); " +
             "Default: 2.2 N x 0.012 m / 0.36 N*m = 0.073 (2.2 N actuator, 12 mm placeholder arm, 4 N at ~9 cm). " +
             "Recompute once the moment arm is measured. 1 = physical torque, which saturates a 2.2 N actuator almost at once.")]
    [SerializeField] private float _dorsalGain = 0.073f;

    [Header("Tracking gate")]
    [SerializeField] private bool _gateOnTracking = true;
    [Tooltip("Also scale by the solver's fit quality (LastTrust), ramping from x to y. Off = only fade out when the solver has no fresh solution.")]
    [SerializeField] private bool _useFitQuality = false;
    [SerializeField] private Vector2 _fitQualityRamp = new Vector2(0.2f, 0.6f);
    [Tooltip("Gate fades in/out over this many seconds -- no step in force when tracking drops.")]
    [SerializeField] private float _gateFadeSeconds = 0.15f;

    [Header("Logging")]
    [Tooltip("Writes glove_esp32_cmd_*.csv (every packet sent) and glove_esp32_tlm_*.csv (every telemetry packet) " +
             "to persistentDataPath. adb pull /sdcard/Android/data/<package>/files/")]
    [SerializeField] private bool _logCsv = false;
    [Tooltip("Print one line per interval to the Unity / Android Logcat console (tag GLOVEF): tracking gate, and per finger " +
             "touching, depth in the object, force, P and torque. Filter Logcat on GLOVEF to watch it live. 0 = off.")]
    [SerializeField] private float _logcatHz = 5f;

    // ---- read-only state, visible in the inspector ---------------------------

    [Serializable]
    public struct FingerOutput
    {
        public bool touching;
        [Tooltip("|F| after gain, clamp and tracking gate (N).")]
        public float forceN;
        [Tooltip("Pressing force sent to the board (N).")]
        public float pressN;
        [Tooltip("In-plane part of F in pad coordinates (N).")]
        public Vector2 tiltN;
        [Tooltip("Angle between the pad normal and the surface normal (deg).")]
        public float tiltDeg;
        [Tooltip("-N.F/|F| while touching: 1 = pad square on the surface, below 0 = pad faces AWAY (flip it).")]
        public float facing;
        public float dorsalTorqueNm;
        [Tooltip("Preview of the board's split across chambers A, B, C (N).")]
        public Vector3 chamberN;
    }

    [Serializable]
    public class BoardStatus
    {
        public string ip = "";
        public bool online;
        [Tooltip("Network round trip, ms (the board's telemetry wait already subtracted).")]
        public float rttMs;
        public int faults;
        public string faultText = "";
        public float renderingPressN;
        [Tooltip("Target kPa: A, B, C, dorsal. Open-loop firmware (no sensors): 1 = on, 0 = off.")]
        public Vector4 targetKPa;
        [Tooltip("Measured kPa: A, B, C, dorsal. Open-loop firmware (no sensors): syringe position, % of the ON stroke.")]
        public Vector4 measuredKPa;
    }

    [Header("Read-only (Play mode)")]
    [Tooltip("Where packets go right now. Empty = no relay address yet: nothing is being sent.")]
    [SerializeField] private string _sendingTo = "";
    [Tooltip("Packets sent since Play. Should climb ~50 per second.")]
    [SerializeField] private int _packetsSent;
    [SerializeField] private float _trust = 1f;
    [SerializeField] private float _gate = 1f;
    [SerializeField] private FingerOutput[] _out = new FingerOutput[2];
    [SerializeField] private BoardStatus[] _boards = { new BoardStatus(), new BoardStatus() };

    public FingerOutput GetOutput(HapticFinger finger) => _out[(int)finger];
    public bool BoardOnline(HapticFinger finger) => _boards[(int)finger].online;
    public uint LastSequence => _seq;

    // ---- internals -----------------------------------------------------------

    private static readonly string[][] ChainNames =
    {
        new[] { "XRHand_ThumbMetacarpal", "XRHand_ThumbProximal", "XRHand_ThumbDistal", "XRHand_ThumbTip" },
        new[] { "XRHand_IndexProximal", "XRHand_IndexIntermediate", "XRHand_IndexDistal", "XRHand_IndexTip" },
    };
    private readonly Transform[][] _chains = new Transform[2][];
    private readonly GloveEsp32Protocol.PadFrame[] _frames = new GloveEsp32Protocol.PadFrame[2];
    private readonly Vector3[] _forceWorld = new Vector3[2];
    private readonly float[] _split = new float[3];

    private readonly float[] _cmd = new float[GloveEsp32Protocol.CmdFloats];
    private readonly byte[] _cmdBytes = new byte[GloveEsp32Protocol.CmdBytes];
    private uint _seq;
    private float _nextSend;
    private float _enabledAt;
    private bool _warnedNoAddress;
    private int _sendErrors;

    private UdpClient _tx;
    private UdpClient _rx;
    private Thread _rxThread;
    private volatile bool _running;
    private readonly object _lock = new object();
    private readonly IPEndPoint[] _endpoints = new IPEndPoint[2];
    private readonly bool[] _pinned = new bool[2];
    private readonly long[] _lastRxTicks = new long[2];
    private readonly float[][] _tlm = { new float[GloveEsp32Protocol.TlmFloats], new float[GloveEsp32Protocol.TlmFloats] };
    private readonly float[] _rtt = new float[2];
    private readonly bool[] _fresh = new bool[2];

    // Send time of each recent sequence number, so an echo can be matched to its own send
    // even when the round trip is longer than the send period.
    private const int SendHistory = 256;
    private readonly uint[] _sentSeq = new uint[SendHistory];
    private readonly long[] _sentTicks = new long[SendHistory];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private string _logDir;
    private StreamWriter _cmdCsv, _tlmCsv;
    private readonly ConcurrentQueue<string> _tlmRows = new ConcurrentQueue<string>();
    private readonly StringBuilder _sb = new StringBuilder(512);

    private readonly float[] _facingAvg = new float[2];
    private readonly int[] _facingSamples = new int[2];
    private readonly bool[] _facingWarned = new bool[2];

    private void Awake()
    {
        if (_haptics == null) _haptics = GetComponent<HapticRenderer>();
        if (_solver == null) _solver = GetComponent<ImageHandSolver>();
    }

    private void OnEnable()
    {
        if (_haptics == null)
        {
            Debug.LogError($"{name}: no HapticRenderer found -- add this component next to the glove hand's HapticRenderer.", this);
            enabled = false;
            return;
        }

        // The MATLAB text output targets the old McKibben rig. Two senders to two rigs is
        // never what you want, so switch it off here (by name: no compile dependency).
        if (GetComponent("GloveForceOutput") is Behaviour matlab && matlab.enabled)
        {
            matlab.enabled = false;
            Debug.Log($"{name}: disabled GloveForceOutput (MATLAB) -- force now goes to the glove relay.", this);
        }

        _logDir = Application.persistentDataPath;
        try
        {
            _tx = new UdpClient();
            _rx = new UdpClient(AddressFamily.InterNetwork);
            _rx.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _rx.Client.Bind(new IPEndPoint(IPAddress.Any, GloveEsp32Protocol.TelemetryPort));
            _rx.EnableBroadcast = true;
            _rx.Client.ReceiveTimeout = 500;
        }
        catch (Exception e)
        {
            Debug.LogError($"{name}: could not open UDP {GloveEsp32Protocol.TelemetryPort} -- {e.Message}", this);
            CloseSockets();
            enabled = false;
            return;
        }

        lock (_lock)
        {
            _endpoints[0] = _endpoints[1] = null;
            _pinned[0] = _pinned[1] = false;
        }
        PinBoard(0, _thumbBoardIp);
        PinBoard(1, _indexBoardIp);

        _enabledAt = Time.unscaledTime;
        _warnedNoAddress = false;
        _packetsSent = 0;
        Debug.Log($"{name}: glove output running -- thumb -> {(_pinned[0] ? _endpoints[0].ToString() : "auto")}, " +
                  $"index -> {(_pinned[1] ? _endpoints[1].ToString() : "auto")}", this);

        _running = true;
        _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "GloveEsp32Telemetry" };
        _rxThread.Start(_rx);

        if (_logCsv) OpenCsv();
    }

    private void OnDisable()
    {
        // Last word: zero force, so the actuators go back to rest after a clean stop. The
        // boards' watchdog is the real protection; this just makes it immediate.
        for (int i = 0; i < 3; i++) SendZeros();

        _running = false;
        CloseSockets();
        _rxThread?.Join(600);
        _rxThread = null;
        DrainTelemetryRows();
        _cmdCsv?.Dispose(); _cmdCsv = null;
        _tlmCsv?.Dispose(); _tlmCsv = null;
    }

    private void OnApplicationPause(bool paused)
    {
        // Headset taken off: Unity stops calling LateUpdate. Say so now rather than in 200 ms.
        if (paused && isActiveAndEnabled) SendZeros();
    }

    private void CloseSockets()
    {
        try { _rx?.Close(); } catch { /* closing */ }
        try { _tx?.Close(); } catch { /* closing */ }
        _rx = null;
        _tx = null;
    }

    private void PinBoard(int finger, string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return;
        if (IPAddress.TryParse(ip.Trim(), out IPAddress address))
            lock (_lock)
            {
                _endpoints[finger] = new IPEndPoint(address, GloveEsp32Protocol.CommandPort);
                _pinned[finger] = true;
            }
        else
            Debug.LogWarning($"{name}: '{ip}' is not an IP address; finding the board automatically instead.", this);
    }

    // =========================================================================
    // Per frame
    // =========================================================================

    private void LateUpdate()
    {
        UpdateGate();
        ComputeFinger(HapticFinger.Thumb);
        ComputeFinger(HapticFinger.Index);
        UpdateBoardStatus();
        DrainTelemetryRows();

        if (_maxSendRateHz > 0f)
        {
            // Floor at 20 Hz: much slower and the boards' 200 ms watchdog fires between packets.
            if (Time.unscaledTime < _nextSend) return;
            _nextSend = Time.unscaledTime + 1f / Mathf.Max(_maxSendRateHz, 20f);
        }
        Send();
        LogLive();
    }

    private float _nextLiveLog;

    /// <summary>Live readout for Logcat: "GLOVEF gate 1.00 w 1.00 fit 0.82 | TH touch 1 d 4.2mm F 0.95 P 0.36 T 0.0021 face 0.98 | IX ...".</summary>
    private void LogLive()
    {
        if (_logcatHz <= 0f || Time.unscaledTime < _nextLiveLog) return;
        _nextLiveLog = Time.unscaledTime + 1f / _logcatHz;
        var inv = CultureInfo.InvariantCulture;
        _sb.Clear();
        _sb.Append("GLOVEF gate ").Append(_gate.ToString("F2", inv));
        if (_solver != null && _solver.isActiveAndEnabled)
            _sb.Append(" w ").Append((_solver.Ready ? _solver.Weight : 0f).ToString("F2", inv))
               .Append(" fit ").Append(_solver.LastTrust.ToString("F2", inv));
        for (int f = 0; f < 2; f++)
        {
            FingerOutput o = _out[f];
            float d = _haptics.GetState((HapticFinger)f).depthMeters;
            _sb.Append(f == 0 ? " | TH" : " | IX")
               .Append(" touch ").Append(o.touching ? 1 : 0)
               .Append(" d ").Append(float.IsInfinity(d) || float.IsNaN(d) ? "--" : (d * 1000f).ToString("F1", inv)).Append("mm")
               .Append(" F ").Append(o.forceN.ToString("F2", inv))
               .Append(" P ").Append(o.pressN.ToString("F2", inv))
               .Append(" T ").Append(o.dorsalTorqueNm.ToString("F4", inv))
               .Append(" face ").Append(o.facing.ToString("F2", inv));
        }
        _sb.Append(" | sent ").Append(_packetsSent);
        Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, this, "{0}", _sb.ToString());
    }

    private void UpdateGate()
    {
        float trust = 1f;
        if (_gateOnTracking && _solver != null && _solver.isActiveAndEnabled)
        {
            trust = _solver.Ready ? _solver.Weight : 0f;
            if (_useFitQuality)
                trust *= Mathf.InverseLerp(_fitQualityRamp.x, _fitQualityRamp.y, _solver.LastTrust);
        }
        _trust = Mathf.Clamp01(trust);
        float rate = Time.deltaTime / Mathf.Max(_gateFadeSeconds, 1e-3f);
        _gate = Mathf.MoveTowards(_gate, _trust, rate);
    }

    private void ComputeFinger(HapticFinger finger)
    {
        int f = (int)finger;
        bool thumb = finger == HapticFinger.Thumb;
        var o = new FingerOutput();
        Transform[] chain = Chain(f);
        HapticRenderer.FingerState s = _haptics.GetState(finger);

        if (chain == null)
        {
            _out[f] = o;
            _forceWorld[f] = Vector3.zero;
            return;
        }

        Pose tip = FingertipSurfaceConstraint.TrackedPose(chain[3]);
        Pose distal = FingertipSurfaceConstraint.TrackedPose(chain[2]);
        var frame = GloveEsp32Protocol.ComputePadFrame(distal, tip, thumb,
            thumb ? _flipThumb : _flipIndex, thumb ? _mirrorThumbU : _mirrorIndexU);
        _frames[f] = frame;

        // A disabled HapticRenderer keeps returning its last state; sending that every
        // frame would keep the board's watchdog fed with a stale force. Zero it.
        bool live = _haptics.isActiveAndEnabled;
        if (!live) s = default;
        Vector3 F = s.touching ? s.forceNewton * _forceGain : Vector3.zero;
        F = Vector3.ClampMagnitude(F, _maxForceNewton) * _gate;
        if (!IsFinite(F)) F = Vector3.zero;
        _forceWorld[f] = F;

        o.touching = s.touching;
        o.forceN = F.magnitude;
        float pn = Vector3.Dot(F, frame.N);
        o.pressN = Mathf.Max(0f, -pn) * _padForceGain;
        o.tiltN = new Vector2(Vector3.Dot(F, frame.U), Vector3.Dot(F, frame.V)) * _padForceGain;
        o.tiltDeg = o.forceN > 1e-6f ? Mathf.Atan2(o.tiltN.magnitude, -pn) * Mathf.Rad2Deg : 0f;
        o.facing = o.forceN > 1e-6f ? -pn / o.forceN : 0f;

        if (thumb ? _thumbDorsal : _indexDorsal)
        {
            int j = thumb ? _thumbDorsalJoint : _indexDorsalJoint;
            Pose joint = FingertipSurfaceConstraint.TrackedPose(chain[j]);
            Vector3 rigAxis = joint.rotation * (thumb ? Vector3.up : Vector3.right);
            Vector3 flexAxis = (thumb ? _flipThumb : _flipIndex) ? -rigAxis : rigAxis;
            float tau = GloveEsp32Protocol.ExtensionTorque(joint.position, flexAxis, tip.position, F);
            o.dorsalTorqueNm = Mathf.Min(tau * _dorsalGain, _maxDorsalTorqueNm);
        }

        GloveEsp32Protocol.ChamberSplit(o.pressN, o.tiltN.x, o.tiltN.y, _split);
        o.chamberN = new Vector3(_split[0], _split[1], _split[2]);

        CheckFacing(f, s, o);
        _out[f] = o;
    }

    /// <summary>A pad normal pointing the wrong way does not fail loudly: P is just always
    /// zero. Watch for it -- in a pinch the pad must face the object.</summary>
    private void CheckFacing(int f, HapticRenderer.FingerState s, FingerOutput o)
    {
        if (_facingWarned[f] || !s.touching || s.pressure01 < 0.15f || o.forceN < 1e-4f) return;
        _facingAvg[f] = Mathf.Lerp(_facingAvg[f], o.facing, 0.05f);
        if (++_facingSamples[f] < 60 || _facingAvg[f] > -0.3f) return;
        _facingWarned[f] = true;
        string which = f == 0 ? "Thumb" : "Index";
        Debug.LogWarning($"{name}: the {which.ToLower()} pad normal points AWAY from the object it is pressing " +
                         $"(average facing {_facingAvg[f]:F2}), so its pressing force is always zero. " +
                         $"Tick _flip{which} and check the yellow gizmo.", this);
    }

    // =========================================================================
    // Packet out
    // =========================================================================

    private void Send()
    {
        if (_tx == null) return;
        Array.Clear(_cmd, 0, _cmd.Length);
        _cmd[GloveEsp32Protocol.CmdSeq] = _seq;
        _cmd[GloveEsp32Protocol.CmdTime] = Time.unscaledTime;
        _cmd[GloveEsp32Protocol.CmdTrust] = _trust;
        WriteFinger(0, GloveEsp32Protocol.CmdThumbBase);
        WriteFinger(1, GloveEsp32Protocol.CmdIndexBase);
        _cmd[GloveEsp32Protocol.CmdThumbTorque] = _out[0].dorsalTorqueNm;
        _cmd[GloveEsp32Protocol.CmdIndexTorque] = _out[1].dorsalTorqueNm;
        _cmd[GloveEsp32Protocol.CmdVersion] = GloveEsp32Protocol.Version;

        Transmit();
        if (_cmdCsv != null) WriteCmdRow();
    }

    private void WriteFinger(int f, int at)
    {
        _cmd[at] = _out[f].touching ? 1f : 0f;
        _cmd[at + 1] = _out[f].pressN;
        _cmd[at + 2] = _out[f].tiltN.x;
        _cmd[at + 3] = _out[f].tiltN.y;
    }

    private void SendZeros()
    {
        if (_tx == null) return;
        Array.Clear(_cmd, 0, _cmd.Length);
        _cmd[GloveEsp32Protocol.CmdSeq] = _seq;
        _cmd[GloveEsp32Protocol.CmdTime] = Time.unscaledTime;
        _cmd[GloveEsp32Protocol.CmdVersion] = GloveEsp32Protocol.Version;
        Transmit();
    }

    private void Transmit()
    {
        // float32 little-endian: Quest (ARM64) and ESP32 (Xtensa) are both little-endian, so
        // the native layout BlockCopy produces is already the wire format.
        Buffer.BlockCopy(_cmd, 0, _cmdBytes, 0, _cmdBytes.Length);

        lock (_lock)
        {
            _sentSeq[_seq % SendHistory] = _seq;
            _sentTicks[_seq % SendHistory] = _clock.ElapsedTicks;
        }
        _seq++;

        string to = "";
        for (int b = 0; b < 2; b++)
        {
            IPEndPoint ep;
            lock (_lock) ep = _endpoints[b];
            if (ep == null) continue;
            if (b == 0 || to.Length == 0) to = ep.ToString();
            else if (to != ep.ToString()) to += " + " + ep;
            try { _tx.Send(_cmdBytes, _cmdBytes.Length, ep); _packetsSent++; }
            catch (Exception e)
            {
                // Wi-Fi blip: the next frame tries again, the board's watchdog covers the gap
                if (_sendErrors++ == 0) Debug.LogWarning($"{name}: send to {ep} failed -- {e.Message}", this);
            }
        }
        _sendingTo = to;

        if (to.Length == 0 && !_warnedNoAddress && Time.unscaledTime - _enabledAt > 3f)
        {
            _warnedNoAddress = true;
            Debug.LogWarning($"{name}: no relay address after 3 s, so nothing is being sent. Set _thumbBoardIp and " +
                             "_indexBoardIp (127.0.0.1 if the relay runs on this PC) BEFORE pressing Play.", this);
        }
    }

    // =========================================================================
    // Telemetry in (background thread: no Unity API here)
    // =========================================================================

    private void ReceiveLoop(object socket)
    {
        var client = (UdpClient)socket;
        var from = new IPEndPoint(IPAddress.Any, 0);
        var f = new float[GloveEsp32Protocol.TlmFloats];
        while (_running)
        {
            byte[] data;
            try { data = client.Receive(ref from); }
            catch (SocketException) { continue; }       // timeout: check _running and wait again
            catch (ObjectDisposedException) { return; } // closed by OnDisable
            catch (Exception) { if (!_running) return; continue; }

            if (data == null || data.Length < GloveEsp32Protocol.TlmBytes) continue;
            Buffer.BlockCopy(data, 0, f, 0, GloveEsp32Protocol.TlmBytes);
            if ((int)f[GloveEsp32Protocol.TlmVersion] != GloveEsp32Protocol.Version) continue;
            int finger = (int)f[GloveEsp32Protocol.TlmFinger];
            if (finger < 0 || finger > 1) continue;

            long now = _clock.ElapsedTicks;
            float rttMs = float.NaN;
            lock (_lock)
            {
                // Discovery: wherever this board's telemetry comes from is where its commands go
                // -- unless the address was pinned in the inspector.
                var ep = _endpoints[finger];
                if (!_pinned[finger] && (ep == null || !ep.Address.Equals(from.Address)))
                    _endpoints[finger] = new IPEndPoint(from.Address, GloveEsp32Protocol.CommandPort);

                uint echo = (uint)f[GloveEsp32Protocol.TlmEchoSeq];
                if (_sentSeq[echo % SendHistory] == echo && echo != 0)
                {
                    double ms = (now - _sentTicks[echo % SendHistory]) * 1000.0 / Stopwatch.Frequency;
                    // The echo waits on the board for the next telemetry slot; it reports how long.
                    ms -= f[GloveEsp32Protocol.TlmEchoAge] * 1000.0;
                    if (ms >= 0 && ms < 5000) rttMs = (float)ms;
                }
                if (!float.IsNaN(rttMs)) _rtt[finger] = rttMs;
                Array.Copy(f, _tlm[finger], f.Length);
                _lastRxTicks[finger] = now;
                _fresh[finger] = true;
            }

            if (_logCsv) _tlmRows.Enqueue(TelemetryRow(now, finger, from.Address, f, rttMs));
        }
    }

    private void UpdateBoardStatus()
    {
        long now = _clock.ElapsedTicks;
        lock (_lock)
        {
            for (int b = 0; b < 2; b++)
            {
                BoardStatus s = _boards[b];
                s.ip = _endpoints[b] != null ? _endpoints[b].Address.ToString() : "";
                bool wasOnline = s.online;
                s.online = _lastRxTicks[b] != 0 && (now - _lastRxTicks[b]) < Stopwatch.Frequency; // heard within 1 s
                if (s.online && !wasOnline) Debug.Log($"{name}: {(b == 0 ? "thumb" : "index")} board online at {s.ip}", this);
                if (!s.online && wasOnline) Debug.LogWarning($"{name}: {(b == 0 ? "thumb" : "index")} board silent for 1 s", this);
                if (!_fresh[b]) continue;
                _fresh[b] = false;

                float[] t = _tlm[b];
                s.rttMs = _rtt[b];
                s.faults = (int)t[GloveEsp32Protocol.TlmFaults];
                s.faultText = FaultText(s.faults);
                s.renderingPressN = t[GloveEsp32Protocol.TlmPressForce];
                int c = GloveEsp32Protocol.TlmChannelBase;
                s.targetKPa = new Vector4(t[c], t[c + 2], t[c + 4], t[c + 6]);
                s.measuredKPa = new Vector4(t[c + 1], t[c + 3], t[c + 5], t[c + 7]);
            }
        }
    }

    public static string FaultText(int faults)
    {
        if (faults == 0) return "";
        var sb = new StringBuilder();
        for (int c = 0; c < GloveEsp32Protocol.ChannelsPerBoard; c++)
            if ((faults & (1 << c)) != 0) sb.Append("stall-").Append(GloveEsp32Protocol.ChannelNames[c]).Append(' ');
        if ((faults & GloveEsp32Protocol.FaultLinkLost) != 0) sb.Append("link-lost ");
        if ((faults & GloveEsp32Protocol.FaultOverPressure) != 0) sb.Append("over-pressure ");
        if ((faults & GloveEsp32Protocol.FaultSensor) != 0) sb.Append("sensor ");
        if ((faults & GloveEsp32Protocol.FaultEStop) != 0) sb.Append("e-stop ");
        if ((faults & GloveEsp32Protocol.FaultTravel) != 0) sb.Append("travel-budget ");
        if ((faults & GloveEsp32Protocol.FaultStallLatched) != 0) sb.Append("stall-LATCHED ");
        return sb.ToString().TrimEnd();
    }

    // =========================================================================
    // Bones
    // =========================================================================

    /// <summary>Same bones FingertipSurfaceConstraint uses (the ACTIVE skeleton: HandVisual can
    /// switch between OpenXR and Oculus bone sets at runtime), so TrackedPose finds them.</summary>
    private Transform[] Chain(int f)
    {
        Transform[] c = _chains[f];
        if (c != null && c[0] != null && c[0].gameObject.activeInHierarchy) return c;

        c = new Transform[4];
        for (int j = 0; j < 4; j++)
        {
            c[j] = FindActiveDeepChild(transform, ChainNames[f][j]);
            if (c[j] == null) { _chains[f] = null; return null; }
        }
        _chains[f] = c;
        return c;
    }

    private static Transform FindActiveDeepChild(Transform parent, string boneName)
    {
        foreach (Transform child in parent)
        {
            if (!child.gameObject.activeInHierarchy) continue;
            if (child.name == boneName) return child;
            Transform result = FindActiveDeepChild(child, boneName);
            if (result != null) return result;
        }
        return null;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

    // =========================================================================
    // CSV
    // =========================================================================

    private void OpenCsv()
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        try
        {
            _cmdCsv = new StreamWriter(Path.Combine(_logDir, $"glove_esp32_cmd_{stamp}.csv"), false, Encoding.ASCII);
            _cmdCsv.WriteLine("t_ms,seq,unity_time,trust,gate," +
                              "th_touch,th_F,th_P,th_tu,th_tv,th_tilt_deg,th_facing,th_torque,th_A,th_B,th_C," +
                              "ix_touch,ix_F,ix_P,ix_tu,ix_tv,ix_tilt_deg,ix_facing,ix_torque,ix_A,ix_B,ix_C," +
                              "th_depth_mm,ix_depth_mm,solver_weight,fit_trust");
            _tlmCsv = new StreamWriter(Path.Combine(_logDir, $"glove_esp32_tlm_{stamp}.csv"), false, Encoding.ASCII);
            _tlmCsv.WriteLine("t_ms,finger,ip,echo_seq,esp_time,faults,rtt_ms,echo_age_ms,trust," +
                              "tgt_A,meas_A,tgt_B,meas_B,tgt_C,meas_C,tgt_dorsal,meas_dorsal,press_N");
            Debug.Log($"{name}: logging to {_logDir}/glove_esp32_*_{stamp}.csv", this);
        }
        catch (Exception e)
        {
            Debug.LogError($"{name}: could not open CSV -- {e.Message}", this);
            _cmdCsv?.Dispose(); _cmdCsv = null;
            _tlmCsv?.Dispose(); _tlmCsv = null;
        }
    }

    private void WriteCmdRow()
    {
        var inv = CultureInfo.InvariantCulture;
        _sb.Clear();
        _sb.Append((_clock.ElapsedTicks * 1000.0 / Stopwatch.Frequency).ToString("F2", inv)).Append(',')
           .Append(_seq - 1).Append(',')
           .Append(Time.unscaledTime.ToString("F4", inv)).Append(',')
           .Append(_trust.ToString("F3", inv)).Append(',')
           .Append(_gate.ToString("F3", inv));
        for (int f = 0; f < 2; f++)
        {
            FingerOutput o = _out[f];
            _sb.Append(',').Append(o.touching ? 1 : 0);
            foreach (float v in new[] { o.forceN, o.pressN, o.tiltN.x, o.tiltN.y, o.tiltDeg, o.facing, o.dorsalTorqueNm,
                                        o.chamberN.x, o.chamberN.y, o.chamberN.z })
                _sb.Append(',').Append(v.ToString("F4", inv));
        }
        // Why a force is what it is: how deep each REAL fingertip is in the object (HapticRenderer,
        // + = inside), and how much the image tracker is trusted right now.
        for (int f = 0; f < 2; f++)
        {
            float d = _haptics.GetState((HapticFinger)f).depthMeters;
            _sb.Append(',').Append(float.IsInfinity(d) || float.IsNaN(d) ? "" : (d * 1000f).ToString("F2", inv));
        }
        bool solver = _solver != null && _solver.isActiveAndEnabled;
        _sb.Append(',').Append(solver ? (_solver.Ready ? _solver.Weight : 0f).ToString("F3", inv) : "")
           .Append(',').Append(solver ? _solver.LastTrust.ToString("F3", inv) : "");
        _cmdCsv.WriteLine(_sb.ToString());
    }

    private string TelemetryRow(long ticks, int finger, IPAddress ip, float[] f, float rttMs)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(256);
        sb.Append((ticks * 1000.0 / Stopwatch.Frequency).ToString("F2", inv)).Append(',')
          .Append(finger).Append(',').Append(ip).Append(',')
          .Append(((uint)f[GloveEsp32Protocol.TlmEchoSeq]).ToString(inv)).Append(',')
          .Append(f[GloveEsp32Protocol.TlmEspTime].ToString("F3", inv)).Append(',')
          .Append(((int)f[GloveEsp32Protocol.TlmFaults]).ToString(inv)).Append(',')
          .Append(float.IsNaN(rttMs) ? "" : rttMs.ToString("F2", inv)).Append(',')
          .Append((f[GloveEsp32Protocol.TlmEchoAge] * 1000f).ToString("F2", inv)).Append(',')
          .Append(f[GloveEsp32Protocol.TlmTrust].ToString("F3", inv));
        for (int i = 0; i < GloveEsp32Protocol.ChannelsPerBoard * 2; i++)
            sb.Append(',').Append(f[GloveEsp32Protocol.TlmChannelBase + i].ToString("F2", inv));
        sb.Append(',').Append(f[GloveEsp32Protocol.TlmPressForce].ToString("F3", inv));
        return sb.ToString();
    }

    private void DrainTelemetryRows()
    {
        while (_tlmRows.TryDequeue(out string row)) _tlmCsv?.WriteLine(row);
    }

    // =========================================================================
    // Gizmos: the calibration tool. Play mode, Scene view, gizmos on.
    //   yellow = pad normal N (must point OUT OF THE PAD)    red = +U (chamber C side)
    //   green  = +V (toward fingertip, chamber A)            magenta = force, 1 cm per N
    //   white spheres = chambers A, B, C, size = commanded share
    // =========================================================================

    private void OnDrawGizmos()
    {
        if (!Application.isPlaying || _out == null) return;
        for (int f = 0; f < 2; f++)
        {
            if (_chains[f] == null) continue;
            var fr = _frames[f];
            Vector3 o = fr.Origin;
            Gizmos.color = Color.yellow; Gizmos.DrawRay(o, fr.N * 0.02f);
            Gizmos.color = Color.red; Gizmos.DrawRay(o, fr.U * 0.012f);
            Gizmos.color = Color.green; Gizmos.DrawRay(o, fr.V * 0.012f);
            Gizmos.color = Color.magenta; Gizmos.DrawRay(o, _forceWorld[f] * 0.01f);

            Vector3 share = _out[f].chamberN;
            float total = Mathf.Max(share.x + share.y + share.z, 1e-6f);
            for (int i = 0; i < 3; i++)
            {
                float a = GloveEsp32Protocol.ChamberAngleDeg[i] * Mathf.Deg2Rad;
                Vector3 p = o + (fr.U * Mathf.Cos(a) + fr.V * Mathf.Sin(a)) * GloveEsp32Protocol.ChamberRadius;
                Gizmos.color = Color.white;
                Gizmos.DrawWireSphere(p, 0.0015f);
                Gizmos.DrawSphere(p, 0.004f * share[i] / total);
            }
        }
    }
}