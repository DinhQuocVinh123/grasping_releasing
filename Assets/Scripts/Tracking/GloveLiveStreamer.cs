using System;
using System.Net.Sockets;
using Meta.XR;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Streams the Quest's real passthrough camera to the PC CONTINUOUSLY (over one TCP connection kept open), so
/// run_glove_quest_stream.py can run the glove-hand AI model and send the finger points back over UDP to
/// FingerUDPReceiver.
///
/// No separate phone or webcam: the only camera is the headset's. The PC runs the model (the headset is not
/// powerful enough to run it in real time alongside VR rendering).
///
/// Per image: GPU -> CPU readback (AsyncGPUReadback, non-blocking) -> a worker thread downscales, JPEG-encodes and
/// sends it with a 24-byte header (frame id + hand position hint). The camera pose at capture time is kept per frame
/// id, so results that come back later can be turned into rays from where the camera WAS (TryGetFrameRay).
///
/// How to use:
/// 1. On the PC, start first: python run_glove_quest_stream.py --original
///    (it prints this PC's IP and the listening port, 5007 by default).
/// 2. Put this script on any GameObject in the scene.
/// 3. Assign a running (enabled) PassthroughCameraAccess to _passthroughCamera.
/// 4. With auto-discovery off, enter the PC's IP in _pcIpAddress (image port 5007 by default).
/// 5. FingerUDPReceiver on the headset receives the finger data by itself: run_glove_quest_stream.py finds the
///    headset's IP from the TCP connection, so the headset IP never has to be entered on the PC.
/// </summary>
public class GloveLiveStreamer : MonoBehaviour
{
    [Header("Camera source")]
    [Tooltip("A running (enabled) PassthroughCameraAccess component.")]
    [SerializeField] private PassthroughCameraAccess _passthroughCamera;

    [Header("Find the PC automatically (no IP to enter or rebuild when the network changes)")]
    [Tooltip("ON: the headset listens for the beacon the PC broadcasts every second and learns its IP, however often the network changes.\n\n" +
             "The search ONLY happens while connecting and is outside the data path: no extra latency per frame. " +
             "At most ~1 second of waiting when the app starts.\n\n" +
             "OFF: use the IP entered in _pcIpAddress below (for networks that block broadcast packets, e.g. some company networks).")]
    [SerializeField] private bool _useAutoDiscovery = true;
    [Tooltip("UDP port to listen for the PC's beacon; must match --discovery-port of run_glove_quest_stream.py (default 5008).")]
    [SerializeField] private int _discoveryPort = 5008;

    [Header("Continuous streaming to the PC (running run_glove_quest_stream.py)")]
    [Tooltip("IP address of the PC running run_glove_quest_stream.py. Only used with auto-discovery OFF.")]
    [SerializeField] private string _pcIpAddress = "10.42.0.166";
    [SerializeField] private int _pcPort = 5007;

    [Header("Send rate")]
    [Tooltip("Minimum time between two sent frames (s). 0.05 = at most ~20 FPS; " +
             "no need to exceed what the PC can actually process -- Wi-Fi / GPU are the bottleneck.")]
    [SerializeField] private float _sendIntervalSeconds = 0.05f;
    [Tooltip("JPEG quality (0-100). Lower = faster to send, less Wi-Fi congestion, but the model may be slightly less accurate.")]
    [SerializeField] private int _jpegQuality = 80;
    [Tooltip("Downscale the image before encoding (worker thread): 1 = original, 2 = half in each direction (4x fewer pixels). JPEG encoding, " +
             "Wi-Fi and decoding on the PC become ~3x faster (those three stages took ~75 ms per image at full size). " +
             "The model barely changes: median offset 0.007, p90 of the worst point 0.021 palm lengths (test_downscale_accuracy.py). " +
             "Needs a run_glove_quest_stream.py that scales the image back up, and _sendHandHint on.")]
    [SerializeField, Range(1, 4)] private int _downscale = 2;


    [Header("Debug")]
    [Tooltip("If the image arrives upside down on the PC, flip this value.")]
    [SerializeField] private bool _imageIsBottomUp = true;

    [Header("Hand position hint for the PC (from the Quest's wrist tracking, which still works)")]
    [Tooltip("ON: each image carries the glove hand's position + size IN THE IMAGE (projected from the Quest-tracked position) and the frame id. " +
             "Python uses it as the first search box instead of searching blindly over the whole frame (58% of hand losses were search boxes that missed the hand). " +
             "An older Python that does not know this header still works.")]
    [SerializeField] private bool _sendHandHint = true;
    [Tooltip("HandVisual of the glove hand, to know whether the Quest is tracking the hand. Empty = found via FingerUDPReceiver.")]
    [SerializeField] private Oculus.Interaction.HandVisual _gloveHandVisual;
    [Tooltip("Hand centre (XRHand_MiddleProximal) and wrist (XRHand_Wrist) of the glove hand. Empty = found automatically.")]
    [SerializeField] private Transform _handCenter;
    [SerializeField] private Transform _handWrist;
    private FingerUDPReceiver _gloveReceiver;

    [Header("Feedback (optional -- assign a TextMeshPro to see the status inside the headset)")]
    [SerializeField] private TMPro.TextMeshPro _statusText;

    private TcpClient _client;
    private NetworkStream _stream;
    private System.Net.Sockets.UdpClient _discoveryClient;
    private string _discoveredIp;
    private int _discoveredPort;
    private float _lastSendTime;
    private float _lastConnectAttemptTime;
    private int _sentCount;
    private int _failCount;
    private volatile string _lastStatus = "Chua ket noi";

    // --- Encode + send on a WORKER THREAD -----------------------------------------
    // Doing the copy (4.9 MB), flip, texture creation and JPEG encode on the main thread cost ~35-45 ms per send on the
    // Quest and dropped the whole app from 72 to ~34 frames per second. Now the main thread only copies the pixels into a
    // reused buffer; the worker thread flips, encodes (ImageConversion.EncodeArrayToJPG works off the main thread) and sends.
    private System.Threading.Thread _worker;
    private readonly System.Threading.AutoResetEvent _frameReady = new System.Threading.AutoResetEvent(false);
    private volatile bool _workerRunning;
    private volatile bool _workerBusy;        // still encoding / sending the previous frame -> skip this one
    private bool _readbackPending;            // waiting for the GPU to return the image (AsyncGPUReadback) -> no new capture yet
    private float _lastStatusTime = -999f, _readbackStartTime;
    private int _readbackId;                  // images the GPU returns late (after we stopped waiting) are dropped
    private volatile bool _connectionBroken;  // the worker reports a lost connection, the main thread closes it
    private Color32[] _flipBuffer;            // used by the worker thread only
    private Color32[] _scaledBuffer;          // downscaled image (worker thread only)

    // Two rotating image slots: the worker encodes / sends one while the GPU returns the next into the other, so the readback
    // (1-3 frames) overlaps the encoding and ~18 images/s are kept. With one slot, a slightly late image missed the send slot and
    // only 8-9 images/s got through.
    private sealed class FrameSlot
    {
        public Color32[] Pixels;
        public readonly byte[] Header = new byte[24];
        public int HeaderLength, Width, Height, FrameId;
        public int Downscale = 1; // downscale factor written in the packet header (byte 21)
    }
    private readonly FrameSlot[] _slots = { new FrameSlot(), new FrameSlot() };
    private volatile FrameSlot _workerSlot;   // slot the worker is encoding / sending
    private FrameSlot _queuedSlot;            // slot with an image, waiting for the worker to be free
    private float _mainThreadMs, _encodeMs, _sendMs; // time of each step (shown in the status)

    // --- Frame id + camera pose at capture time -----------------------------------
    // Python echoes the "fid" (frame id) in its result, so FingerUDPReceiver can look up the camera pose AT THE MOMENT that
    // image was captured (TryGetFramePose) and build the fingers along the CAMERA's axes instead of the eyes'.
    private const int PoseHistory = 64;
    private readonly int[] _poseIds = new int[PoseHistory];
    private readonly Pose[] _poses = new Pose[PoseHistory];
    // Capture time of each image (Time.realtimeSinceStartup), to measure the "age" of Python's result when it reaches Unity (latency diagnostics)
    private readonly float[] _captureTimes = new float[PoseHistory];
    // Time the camera EXPOSED that image (PassthroughCameraAccess.Timestamp, converted to Time.realtimeSinceStartup), somewhat
    // earlier than _captureTimes (camera processing + waiting for Unity to take the image). NaN = unknown.
    private readonly float[] _exposureTimes = new float[PoseHistory];
    /// <summary>How many ms before it was taken the latest image was exposed (camera -> Unity), -1 = unknown.</summary>
    public float LastCameraAgeMs { get; private set; } = -1f;
    private int _frameId;

    // --- Per-stage latency on the headset ----------------------------------------
    // Timestamps of each image on ClockNow (usable from ANY thread, unlike Time.realtimeSinceStartup which is main-thread only):
    // GPU image requested, GPU returned the image, worker starts encoding, encoded, sent. FingerUDPReceiver writes glove_latency_*.csv.
    /// <summary>Shared clock (seconds) for the latency stamps; callable from a worker thread.</summary>
    public static double ClockNow => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    public struct FrameTimings
    {
        public double Capture, Readback, EncodeStart, EncodeEnd, Sent;
        public float CameraAgeMs; // exposure -> image requested, -1 = unknown
        public int JpegBytes;
    }
    private readonly FrameTimings[] _timings = new FrameTimings[PoseHistory];

    /// <summary>Headset timestamps of image frameId (if still in recent memory). Stamps not reached yet = 0.</summary>
    public bool TryGetFrameTimings(int frameId, out FrameTimings timings)
    {
        int slot = ((frameId % PoseHistory) + PoseHistory) % PoseHistory;
        timings = _timings[slot];
        return frameId > 0 && _poseIds[slot] == frameId;
    }
    private readonly byte[] _header = new byte[24]; // BuildHeader writes here, then it is copied into the image slot

    // Hand centre (middle finger base) + palm length FROM THE QUEST at the capture of each image: exactly the "hint" sent to
    // Python (the hand search area). ImageHandSolver takes the hand-centre depth from here as its depth hint.
    private readonly Vector3[] _hintCenters = new Vector3[PoseHistory];
    private readonly float[] _hintPalms = new float[PoseHistory];
    private readonly bool[] _hintOk = new bool[PoseHistory];
    // Latest hint IN IMAGE FRACTIONS (top-left origin); CameraFrameCue uses it to know the hand is about to leave the frame.
    private Vector2 _latestHintUV;
    private Vector2 _latestHintPalmUV;
    private float _latestHintTime = -999f;

    /// <summary>Camera pose when image frameId was captured (if still in recent memory).</summary>
    public bool TryGetFramePose(int frameId, out Pose pose)
    {
        int slot = ((frameId % PoseHistory) + PoseHistory) % PoseHistory;
        pose = _poses[slot];
        return frameId > 0 && _poseIds[slot] == frameId;
    }

    /// <summary>How many seconds ago image frameId was captured (Wi-Fi + encode + Python + the packet back).
    /// Counted from the GPU image request; does not include the camera's own delay before that.</summary>
    public bool TryGetFrameAge(int frameId, out float ageSeconds)
    {
        int slot = ((frameId % PoseHistory) + PoseHistory) % PoseHistory;
        bool ok = frameId > 0 && _poseIds[slot] == frameId;
        ageSeconds = ok ? Time.realtimeSinceStartup - _captureTimes[slot] : 0f;
        return ok;
    }

    /// <summary>When the camera exposed image frameId, on the Time.realtimeSinceStartup clock (to compare with when the virtual hand is shown).</summary>
    public bool TryGetFrameExposureTime(int frameId, out float realtime)
    {
        int slot = ((frameId % PoseHistory) + PoseHistory) % PoseHistory;
        realtime = _exposureTimes[slot];
        return frameId > 0 && _poseIds[slot] == frameId && !float.IsNaN(realtime);
    }

    /// <summary>Hand centre and palm length from the Quest when image frameId was captured (the hint point).</summary>
    public bool TryGetFrameHint(int frameId, out Vector3 center, out float palm)
    {
        int slot = ((frameId % PoseHistory) + PoseHistory) % PoseHistory;
        center = _hintCenters[slot];
        palm = _hintPalms[slot];
        return frameId > 0 && _poseIds[slot] == frameId && _hintOk[slot];
    }

    /// <summary>Ray from the camera (at its position / rotation WHEN image frameId was captured) through one image point.
    /// uv = (x / width, y / height) from the TOP-left corner, exactly as the image Python receives.</summary>
    public bool TryGetFrameRay(int frameId, Vector2 uv, out Ray ray)
    {
        ray = default;
        if (_passthroughCamera == null || !_passthroughCamera.IsPlaying || !TryGetFramePose(frameId, out Pose cam))
            return false;
        // Meta's viewport has (0,0) at the BOTTOM-left -> flip y (as in BuildHeader)
        ray = _passthroughCamera.ViewportPointToRay(new Vector2(uv.x, 1f - uv.y), cam);
        return true;
    }

    /// <summary>Hand centre FROM THE QUEST in the latest image: uv = image fraction from the TOP-left (&lt;0 or &gt;1 = outside the frame),
    /// palmUV = palm length as a fraction of image width (x) / height (y), age = how old the hint is (s).
    /// false = the Quest has never seen the hand.</summary>
    public bool TryGetLatestHandHint(out Vector2 uv, out Vector2 palmUV, out float age)
    {
        uv = _latestHintUV;
        palmUV = _latestHintPalmUV;
        age = Time.time - _latestHintTime;
        return _latestHintTime > 0f;
    }

    /// <summary>Camera pose when the latest image was captured.</summary>
    public bool TryGetLatestCameraPose(out Pose pose) => TryGetFramePose(_frameId, out pose);

    /// <summary>Direction of the ray through image point uv (TOP-left origin), in the CAMERA's own frame.</summary>
    public bool TryGetCameraLocalRay(Vector2 uv, out Vector3 direction)
    {
        direction = Vector3.forward;
        if (_passthroughCamera == null || !_passthroughCamera.IsPlaying) return false;
        direction = _passthroughCamera.ViewportPointToRay(new Vector2(uv.x, 1f - uv.y), new Pose(Vector3.zero, Quaternion.identity)).direction;
        return true;
    }

    /// <summary>24-byte header before the JPEG (big-endian, matching run_glove_quest_stream.py):
    /// "GLV1" | uint32 frame id | float32 hand centre x, y (pixels, top-left origin) |
    /// float32 hand size (pixels, ~palm length) | uint8 valid flag | uint8 image downscale factor (_downscale;
    /// the hint coordinates are always in ORIGINAL image pixels) | 2 spare bytes.</summary>
    private void BuildHeader(int frameId, Pose camPose, int width, int height)
    {
        float hx = 0f, hy = 0f, size = 0f;
        bool valid = false;
        if (_handCenter != null && _handWrist != null && _gloveHandVisual != null &&
            _gloveHandVisual.Hand != null && _gloveHandVisual.Hand.IsTrackedDataValid)
        {
            Vector3 center = _handCenter.position, wristPos = _handWrist.position;
            // The virtual hand may already have been moved / rotated by FingerUDPReceiver from the image -> use the position FROM
            // THE QUEST (uncorrected), so Python sees the Quest's real offset, not what is left after the correction.
            if (_gloveReceiver != null && _gloveReceiver.TryGetRawHandPose(out Vector3 rc, out Vector3 rw))
            {
                center = rc;
                wristPos = rw;
            }
            float depth = Vector3.Dot(center - camPose.position, camPose.rotation * Vector3.forward);
            if (depth > 0.05f)
            {
                // Meta's viewport has (0,0) at the BOTTOM-left -> the image Python receives is upright, origin TOP-left
                Vector2 vc = _passthroughCamera.WorldToViewportPoint(center, camPose);
                float palm = Vector3.Distance(center, wristPos);
                Vector2 vs = _passthroughCamera.WorldToViewportPoint(center + camPose.rotation * Vector3.right * palm, camPose);
                hx = vc.x * width;
                hy = (1f - vc.y) * height;
                size = Mathf.Abs(vs.x - vc.x) * width; // palm length, not shortened by tilt
                valid = hx > -width && hx < 2f * width && hy > -height && hy < 2f * height;
                if (valid)
                {
                    _latestHintUV = new Vector2(vc.x, 1f - vc.y);
                    float palmU = Mathf.Abs(vs.x - vc.x);
                    _latestHintPalmUV = new Vector2(palmU, palmU * width / Mathf.Max(height, 1));
                    _latestHintTime = Time.time;
                }
                int hs = frameId % PoseHistory;
                _hintCenters[hs] = center;
                _hintPalms[hs] = palm;
            }
        }
        _hintOk[frameId % PoseHistory] = valid;

        _header[0] = (byte)'G'; _header[1] = (byte)'L'; _header[2] = (byte)'V'; _header[3] = (byte)'1';
        WriteBigEndian(_header, 4, (uint)frameId);
        WriteBigEndian(_header, 8, BitConverter.ToUInt32(BitConverter.GetBytes(hx), 0));
        WriteBigEndian(_header, 12, BitConverter.ToUInt32(BitConverter.GetBytes(hy), 0));
        WriteBigEndian(_header, 16, BitConverter.ToUInt32(BitConverter.GetBytes(size), 0));
        _header[20] = (byte)(valid ? 1 : 0);
        // Only downscale when it divides evenly (Python scales back up = original size times the factor; hint coordinates use the original size)
        int scale = Mathf.Clamp(_downscale, 1, 4);
        _header[21] = (byte)(width % scale == 0 && height % scale == 0 ? scale : 1);
        _header[22] = _header[23] = 0;
    }

    private static void WriteBigEndian(byte[] buf, int offset, uint v)
    {
        buf[offset] = (byte)(v >> 24); buf[offset + 1] = (byte)(v >> 16);
        buf[offset + 2] = (byte)(v >> 8); buf[offset + 3] = (byte)v;
    }

    private void Awake()
    {
        var gloveHand = FindAnyObjectByType<FingerUDPReceiver>();
        _gloveReceiver = gloveHand;
        if (gloveHand != null)
        {
            if (_gloveHandVisual == null) _gloveHandVisual = gloveHand.GetComponent<Oculus.Interaction.HandVisual>();
            if (_handCenter == null) _handCenter = FindDeepChild(gloveHand.transform, "XRHand_MiddleProximal");
            if (_handWrist == null) _handWrist = FindDeepChild(gloveHand.transform, "XRHand_Wrist");
        }
    }

    private static Transform FindDeepChild(Transform parent, string name)
    {
        foreach (Transform child in parent)
        {
            if (child.name == name) return child;
            Transform found = FindDeepChild(child, name);
            if (found != null) return found;
        }
        return null;
    }

    private void OnEnable()
    {
        _workerRunning = true;
        _worker = new System.Threading.Thread(WorkerLoop) { IsBackground = true, Name = "GloveFrameEncoder" };
        _worker.Start();
    }

    private void OnDisable()
    {
        _workerRunning = false;
        _frameReady.Set();
        _worker?.Join(500);
        _worker = null;
    }

    private void OnDestroy()
    {
        CloseConnection();
        CloseDiscovery();
    }

    private void OnApplicationQuit()
    {
        CloseConnection();
        CloseDiscovery();
    }

    private void WorkerLoop()
    {
        var sw = new System.Diagnostics.Stopwatch();
        while (_workerRunning)
        {
            _frameReady.WaitOne();
            if (!_workerRunning) break;
            try
            {
                FrameSlot slot = _workerSlot;
                int w = slot.Width, h = slot.Height;
                Color32[] pixels = slot.Pixels;
                int ts = slot.FrameId % PoseHistory;
                _timings[ts].EncodeStart = ClockNow;
                sw.Restart();
                if (slot.Downscale > 1)
                {
                    pixels = Downscale(pixels, w, h, slot.Downscale);
                    w /= slot.Downscale;
                    h /= slot.Downscale;
                }
                else if (_imageIsBottomUp)
                {
                    if (_flipBuffer == null || _flipBuffer.Length != pixels.Length) _flipBuffer = new Color32[pixels.Length];
                    for (int y = 0; y < h; y++) Array.Copy(pixels, y * w, _flipBuffer, (h - 1 - y) * w, w);
                    pixels = _flipBuffer;
                }
                byte[] jpg = ImageConversion.EncodeArrayToJPG(pixels,
                    UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h, 0, _jpegQuality);
                _encodeMs = (float)sw.Elapsed.TotalMilliseconds;
                _timings[ts].EncodeEnd = ClockNow;
                _timings[ts].JpegBytes = jpg.Length;

                sw.Restart();
                NetworkStream stream = _stream;
                if (stream != null)
                {
                    WriteFramed(stream, slot.Header, slot.HeaderLength, jpg);
                    _timings[ts].Sent = ClockNow;
                    _sentCount++;
                    _lastStatus = "OK";
                }
                _sendMs = (float)sw.Elapsed.TotalMilliseconds;
            }
            catch (Exception e)
            {
                _failCount++;
                _lastStatus = "Mat ket noi: " + e.Message;
                _connectionBroken = true;
            }
            finally
            {
                _workerBusy = false;
            }
        }
    }

    /// <summary>Downscales into a reused buffer, flipping top/bottom if _imageIsBottomUp (like the full-size path). Worker thread only.</summary>
    private Color32[] Downscale(Color32[] src, int w, int h, int f)
    {
        int size = (w / f) * (h / f);
        if (_scaledBuffer == null || _scaledBuffer.Length != size) _scaledBuffer = new Color32[size];
        DownscaleInto(src, w, h, f, _imageIsBottomUp, _scaledBuffer);
        return _scaledBuffer;
    }

    /// <summary>Downscales the image f times in each direction by averaging each f x f block (like cv2.INTER_AREA, which
    /// test_downscale_accuracy.py tested); flipRows = the source's top row becomes the bottom row. dst has (w/f)*(h/f) pixels.
    /// Public so it can be tested in the Editor.</summary>
    public static void DownscaleInto(Color32[] src, int w, int h, int f, bool flipRows, Color32[] dst)
    {
        int ow = w / f, oh = h / f, n = f * f, half = n / 2;
        for (int oy = 0; oy < oh; oy++)
        {
            int dst0 = (flipRows ? oh - 1 - oy : oy) * ow;
            for (int ox = 0; ox < ow; ox++)
            {
                int r = 0, g = 0, b = 0;
                for (int dy = 0; dy < f; dy++)
                {
                    int s = (oy * f + dy) * w + ox * f;
                    for (int dx = 0; dx < f; dx++)
                    {
                        Color32 c = src[s + dx];
                        r += c.r; g += c.g; b += c.b;
                    }
                }
                dst[dst0 + ox] = new Color32((byte)((r + half) / n), (byte)((g + half) / n), (byte)((b + half) / n), 255);
            }
        }
    }

    private void CloseDiscovery()
    {
        try { _discoveryClient?.Close(); } catch { /* ignore */ }
        _discoveryClient = null;
    }

    private void Update()
    {
        // Listen for the PC's beacon every frame (non-blocking: only reads when a packet is actually queued).
        if (_useAutoDiscovery) PollDiscovery();

        if (_passthroughCamera == null)
        {
            _lastStatus = "Thieu _passthroughCamera!";
            UpdateStatusText();
            return;
        }
        if (!_passthroughCamera.IsPlaying)
        {
            _lastStatus = "Cho passthrough camera san sang...";
            UpdateStatusText();
            return;
        }
        // An image finished reading while the worker was busy -> hand it over as soon as the worker is free
        if (_queuedSlot != null && !_workerBusy)
        {
            FrameSlot queued = _queuedSlot;
            _queuedSlot = null;
            Dispatch(queued);
        }
        if (Time.time - _lastSendTime < _sendIntervalSeconds)
        {
            return;
        }
        // Only count as sent when a new image was REALLY captured; if busy, retry next frame instead of skipping a whole 0.05 s slot
        if (TrySendFrame()) _lastSendTime = Time.time;
        UpdateStatusText();
    }

    private void Dispatch(FrameSlot slot)
    {
        _workerSlot = slot;
        _workerBusy = true;
        _frameReady.Set();
    }

    /// <summary>Reads the "server is here" beacon the PC broadcasts (format "GLOVE_SERVER|&lt;TCP port&gt;").
    /// The PC's IP is taken DIRECTLY from the sender address of the packet, so it never has to be entered.
    ///
    /// Does not block the main thread: only reads when UdpClient.Available > 0, i.e. a packet is already queued,
    /// so Receive() returns immediately.</summary>
    private void PollDiscovery()
    {
        try
        {
            if (_discoveryClient == null)
            {
                _discoveryClient = new System.Net.Sockets.UdpClient();
                _discoveryClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _discoveryClient.Client.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, _discoveryPort));
            }

            while (_discoveryClient.Available > 0)
            {
                var remote = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                byte[] data = _discoveryClient.Receive(ref remote);
                string msg = System.Text.Encoding.UTF8.GetString(data);
                if (!msg.StartsWith("GLOVE_SERVER|")) continue;

                string portText = msg.Substring("GLOVE_SERVER|".Length);
                if (!int.TryParse(portText, out int tcpPort)) continue;

                string ip = remote.Address.ToString();
                // If the PC changed IP (network change), drop the old connection so the next send reconnects to the new address.
                if (_discoveredIp != ip || _discoveredPort != tcpPort)
                {
                    _discoveredIp = ip;
                    _discoveredPort = tcpPort;
                    CloseConnection();
                }
            }
        }
        catch (Exception e)
        {
            // Close the broken socket and set it to null -- the next Update creates it again from scratch. Keeping a socket that
            // failed to bind would make every .Available call throw forever and never recover.
            _lastStatus = "Loi do tim: " + e.Message;
            CloseDiscovery();
        }
    }

    /// <summary>Captures a new image (asks the GPU for it, non-blocking). true = captured, counts towards the send rate.</summary>
    private bool TrySendFrame()
    {
        if (_connectionBroken && !_workerBusy)
        {
            _connectionBroken = false;
            CloseConnection();
        }
        if (_client == null || !_client.Connected)
        {
            // Do not try to reconnect every frame while it keeps failing: at most once per second, so Update() is not slowed down.
            if (Time.time - _lastConnectAttemptTime < 1.0f)
            {
                return false;
            }
            _lastConnectAttemptTime = Time.time;
            if (!TryConnect())
            {
                return false;
            }
        }

        // The previous image is not back from the GPU yet, or an image is already waiting for the worker -> do not capture more
        // (while the worker is encoding we DO capture: the image arrives during encoding). If the GPU does not return the image
        // within 1 second (e.g. the camera texture was recreated), stop waiting.
        if (_readbackPending && Time.unscaledTime - _readbackStartTime > 1f) _readbackPending = false;
        if (_readbackPending || _queuedSlot != null) return false;

        try
        {
            var t0 = System.Diagnostics.Stopwatch.StartNew();
            var res = _passthroughCamera.CurrentResolution;
            Texture tex = _passthroughCamera.GetTexture();
            if (tex == null || res.x <= 0 || res.y <= 0) return false;

            // Frame id + camera pose at capture time (to build the fingers along the camera's axes), and the hand position hint
            // for Python -- recorded AT CAPTURE TIME, the image arrives 1-3 frames later.
            _frameId++;
            Pose camPose = _passthroughCamera.GetCameraPose();
            int slot = _frameId % PoseHistory;
            _poseIds[slot] = _frameId;
            _poses[slot] = camPose;
            _captureTimes[slot] = Time.realtimeSinceStartup;
            // Timestamp = exposure time (microseconds, same clock as DateTime.UtcNow) -> converted to the realtimeSinceStartup clock.
            // Outside 0..2 s = the two clocks do not share an origin -> dropped (NaN), the analysis will know.
            DateTime exposure = _passthroughCamera.Timestamp;
            float camAge = exposure.Ticks > 0 ? (float)(DateTime.UtcNow - exposure).TotalSeconds : -1f;
            bool camAgeOk = camAge >= 0f && camAge < 2f;
            _exposureTimes[slot] = camAgeOk ? _captureTimes[slot] - camAge : float.NaN;
            LastCameraAgeMs = camAgeOk ? camAge * 1000f : -1f;
            _timings[slot] = new FrameTimings { Capture = ClockNow, CameraAgeMs = LastCameraAgeMs };
            byte[] header = null;
            if (_sendHandHint)
            {
                BuildHeader(_frameId, camPose, res.x, res.y);
                header = (byte[])_header.Clone();
            }

            // Read the image from the GPU WITHOUT BLOCKING: GetColors() made the main thread wait for the GPU (WaitForCompletion) on
            // every send, so that frame missed its deadline (frames with a new result dropped 43% of the time; the app ran 62-65 of
            // 72 fps). With the async readback: 72/72, no dropped frames.
            int w = res.x, h = res.y;
            _readbackPending = true;
            _readbackStartTime = Time.unscaledTime;
            int id = ++_readbackId;
            int fid = _frameId;
            // The texture's own format (R8G8B8A8, sRGB), same as GetColors(); requesting a different format can make the GPU decode sRGB -> a dark image.
            AsyncGPUReadback.Request(tex, 0, req => { if (id == _readbackId) OnReadback(req, w, h, header, fid); });
            _mainThreadMs = (float)t0.Elapsed.TotalMilliseconds;
            return true;
        }
        catch (Exception e)
        {
            _readbackPending = false;
            _failCount++;
            _lastStatus = "Loi doc camera: " + e.Message;
            return false;
        }
    }

    /// <summary>The GPU has returned the image (main thread, 1-3 frames after the request): copy it into the image slot the worker
    /// is NOT using, then hand it over at once (worker free) or queue it (Update hands it over when free). The header was built at capture time.</summary>
    private void OnReadback(AsyncGPUReadbackRequest req, int w, int h, byte[] header, int frameId)
    {
        _readbackPending = false;
        if (!isActiveAndEnabled || !_workerRunning) return;
        if (req.hasError)
        {
            _failCount++;
            _lastStatus = "Loi doc anh GPU";
            return;
        }
        _timings[frameId % PoseHistory].Readback = ClockNow;
        var t0 = System.Diagnostics.Stopwatch.StartNew();
        var colors = req.GetData<Color32>();
        if (colors.Length != w * h) return;
        FrameSlot slot = _workerBusy && _workerSlot == _slots[0] ? _slots[1] : _slots[0];
        if (_workerBusy && _workerSlot == slot) slot = slot == _slots[0] ? _slots[1] : _slots[0];
        if (slot.Pixels == null || slot.Pixels.Length != colors.Length) slot.Pixels = new Color32[colors.Length];
        colors.CopyTo(slot.Pixels); // copy into a reused buffer (no new array each time)
        slot.Width = w;
        slot.Height = h;
        slot.FrameId = frameId;
        slot.Downscale = header != null ? Mathf.Max(1, header[21]) : 1; // no header -> Python would not know to scale back up
        slot.HeaderLength = header != null ? header.Length : 0;
        if (header != null) Array.Copy(header, slot.Header, header.Length);
        _mainThreadMs += (float)t0.Elapsed.TotalMilliseconds;
        if (_workerBusy) _queuedSlot = slot;
        else Dispatch(slot);
    }

    private bool TryConnect()
    {
        // Prefer the discovered address; if no beacon has arrived yet, fall back to the manual IP (still works on networks that block broadcast).
        bool useDiscovered = _useAutoDiscovery && !string.IsNullOrEmpty(_discoveredIp);
        string targetIp = useDiscovered ? _discoveredIp : _pcIpAddress;
        int targetPort = useDiscovered ? _discoveredPort : _pcPort;

        if (string.IsNullOrEmpty(targetIp))
        {
            _lastStatus = "Dang cho tin hieu tu may tinh...";
            return false;
        }

        try
        {
            _client = new TcpClient();
            _client.NoDelay = true; // send each frame immediately, no batching -- low latency first
            _client.SendTimeout = 2000;
            _client.Connect(targetIp, targetPort);
            _stream = _client.GetStream();
            _lastStatus = $"Da ket noi {targetIp}:{targetPort}" + (useDiscovered ? " (tu tim)" : " (IP tay)");
            return true;
        }
        catch (Exception e)
        {
            _lastStatus = $"Khong ket noi duoc {targetIp}:{targetPort}: {e.Message}";
            CloseConnection();
            return false;
        }
    }

    private void CloseConnection()
    {
        try { _stream?.Close(); } catch { /* ignore */ }
        try { _client?.Close(); } catch { /* ignore */ }
        _stream = null;
        _client = null;
    }

    /// <summary>[4-byte length (big-endian)][optional header][JPEG image]; the length includes the header.</summary>
    private static void WriteFramed(NetworkStream stream, byte[] header, int headerLength, byte[] data)
    {
        byte[] lenPrefix = BitConverter.GetBytes(headerLength + data.Length);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(lenPrefix); // big-endian, matching struct.unpack('>I', ...) in Python
        }
        stream.Write(lenPrefix, 0, lenPrefix.Length);
        if (headerLength > 0) stream.Write(header, 0, headerLength);
        stream.Write(data, 0, data.Length);
    }

    /// <summary>IP of the PC receiving the images (discovered, or the manual IP); other outputs
    /// (e.g. the force relay) send to the same machine.</summary>
    public string ServerIp => _useAutoDiscovery && !string.IsNullOrEmpty(_discoveredIp) ? _discoveredIp : _pcIpAddress;

    /// <summary>Time (ms) of the latest send: main-thread part / JPEG encode / network send,
    /// to check that the main thread is no longer blocked.</summary>
    public Vector3 LastTimingsMs => new Vector3(_mainThreadMs, _encodeMs, _sendMs);

    private void UpdateStatusText()
    {
        // Setting TextMeshPro text rebuilds the whole text mesh -> only twice per second (it used to be on every send, 18 times per second)
        if (_statusText == null || Time.unscaledTime - _lastStatusTime < 0.5f) return;
        _lastStatusTime = Time.unscaledTime;
        string target = (_useAutoDiscovery && !string.IsNullOrEmpty(_discoveredIp))
            ? $"{_discoveredIp}:{_discoveredPort} (tu tim)"
            : (_useAutoDiscovery ? "dang do tim..." : $"{_pcIpAddress}:{_pcPort}");
        _statusText.text = $"Glove Live Streamer\nSent: {_sentCount}  Failed: {_failCount}\n" +
                            $"-> {target}\nLast: {_lastStatus}\n" +
                            $"ms: main {_mainThreadMs:F1} | encode {_encodeMs:F1} | send {_sendMs:F1}";
    }
}
