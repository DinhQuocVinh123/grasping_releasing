using System;
using System.Net.Sockets;
using Meta.XR;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Biến thể "live" của GloveDatasetCollector: thay vì ghi từng mẫu training
/// khi bấm nút, script này LIÊN TỤC gửi khung hình từ camera Passthrough
/// thật của Quest sang máy tính (qua 1 kết nối TCP giữ mở), để
/// run_glove_quest_stream.py chạy model AI nhận diện tay đeo găng và gửi
/// ngược lại dữ liệu ngón tay/pinch qua UDP vào FingerUDPReceiver.
///
/// Không cần điện thoại/webcam rời -- camera duy nhất là của kính Quest.
/// Máy tính vẫn là nơi chạy model AI (kính không đủ mạnh để chạy real-time).
///
/// Cách dùng:
/// 1. Trên máy tính, chạy trước: python run_glove_quest_stream.py --original
///    (script sẽ in ra IP máy này và cổng đang lắng nghe, mặc định 5007).
/// 2. Gắn script này vào 1 GameObject bất kỳ trong scene.
/// 3. Kéo PassthroughCameraAccess (đã bật, đang chạy) vào field _passthroughCamera.
/// 4. Điền đúng IP của máy tính vào _pcIpAddress (cổng nhận hình mặc định 5007).
/// 5. FingerUDPReceiver bên kính sẽ tự nhận dữ liệu ngón tay/pinch --
///    run_glove_quest_stream.py tự phát hiện IP của kính từ kết nối TCP,
///    không cần điền IP kính vào máy tính.
/// </summary>
public class GloveLiveStreamer : MonoBehaviour
{
    [Header("Nguồn camera")]
    [Tooltip("Component PassthroughCameraAccess đang chạy (đã Enable).")]
    [SerializeField] private PassthroughCameraAccess _passthroughCamera;

    [Header("Tự tìm máy tính (khỏi phải điền IP + build lại mỗi lần đổi mạng)")]
    [Tooltip("BAT: kinh tu lang nghe tin hieu ma may tinh phat ra moi giay, tu biet IP -- doi mang bao nhieu lan cung khong can sua gi.\n\n" +
             "Viec do tim CHI xay ra luc ket noi, sau do nam ngoai duong truyen du lieu: khong them do tre cho moi khung hinh. " +
             "Chi mat toi da ~1 giay cho luc moi mo app.\n\n" +
             "TAT: dung IP dien tay o o _pcIpAddress ben duoi (dung khi mang chan goi tin broadcast, vd mot so mang cong ty).")]
    [SerializeField] private bool _useAutoDiscovery = true;
    [Tooltip("Cong UDP de nghe tin hieu tu may tinh -- phai khop --discovery-port ben run_glove_quest_stream.py (mac dinh 5008).")]
    [SerializeField] private int _discoveryPort = 5008;

    [Header("Gửi liên tục về máy tính (chạy run_glove_quest_stream.py)")]
    [Tooltip("Địa chỉ IP của máy tính đang chạy run_glove_quest_stream.py. Chỉ dùng khi TẮT auto-discovery.")]
    [SerializeField] private string _pcIpAddress = "10.42.0.166";
    [SerializeField] private int _pcPort = 5007;

    [Header("Tốc độ gửi")]
    [Tooltip("Khoảng cách tối thiểu giữa 2 lần gửi khung hình (giây). 0.05 = tối đa ~20 FPS -- " +
             "không cần cao hơn tốc độ xử lý thực tế của máy tính, WiFi/GPU mới là điểm nghẽn.")]
    [SerializeField] private float _sendIntervalSeconds = 0.05f;
    [Tooltip("Chất lượng nén JPEG (0-100). Thấp hơn = gửi nhanh hơn, đỡ nghẽn WiFi, nhưng model có thể kém chính xác hơn chút.")]
    [SerializeField] private int _jpegQuality = 80;
    [Tooltip("Thu nho anh truoc khi nen (luong phu): 1 = anh goc, 2 = moi chieu 1/2 (it diem anh hon 4 lan). Nen JPEG, " +
             "WiFi va giai nen ben may tinh nhanh hon ~3 lan. glove_latency 20/10 11:11: 3 khau nay ~75 ms/anh voi anh goc. " +
             "Model gan nhu khong doi: lech trung vi 0.007, p90 diem lech nhat 0.021 long ban tay (test_downscale_accuracy.py, " +
             "ban ghi 20261020_112908). Can run_glove_quest_stream.py ban 20/10 (phong lai anh); can bat _sendHandHint.")]
    [SerializeField, Range(1, 4)] private int _downscale = 2;


    [Header("Gỡ lỗi -- dùng CHUNG giá trị đã xác nhận đúng ở GloveDatasetCollector")]
    [Tooltip("Nếu ảnh nhận được trên máy tính bị lộn ngược trên-dưới, đảo giá trị này. " +
             "Dùng đúng giá trị bạn đã xác nhận hoạt động đúng với GloveDatasetCollector trên cùng kính này.")]
    [SerializeField] private bool _imageIsBottomUp = true;

    [Header("Gợi ý vị trí tay cho máy tính (dùng cổ tay Quest vẫn bám được)")]
    [Tooltip("BAT: moi anh gui kem vi tri + kich thuoc ban tay gang TREN ANH (chieu tu vi tri Quest bam duoc) va so thu tu anh. " +
             "Python dung lam khung tim dau tien thay vi do mo ca khung hinh -- 58% so lan mat dau la do khung tim khong om duoc tay. " +
             "Python cu (khong biet phan nay) van chay binh thuong.")]
    [SerializeField] private bool _sendHandHint = true;
    [Tooltip("HandVisual cua tay gang -- de biet Quest co dang bam tay khong. De trong = tu tim theo FingerUDPReceiver.")]
    [SerializeField] private Oculus.Interaction.HandVisual _gloveHandVisual;
    [Tooltip("Tam ban tay (XRHand_MiddleProximal) va co tay (XRHand_Wrist) cua tay gang. De trong = tu tim.")]
    [SerializeField] private Transform _handCenter;
    [SerializeField] private Transform _handWrist;
    private FingerUDPReceiver _gloveReceiver;

    [Header("Feedback (tuỳ chọn -- kéo 1 TextMeshPro vào đây để xem trạng thái ngay trong kính)")]
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

    // --- Nen + gui tren LUONG PHU ---------------------------------------------
    // Truoc day moi lan gui (20 lan/giay) luong chinh phai chep 4.9 MB, lat anh,
    // tao texture va NEN JPEG -- mat ~35-45 ms moi lan tren Quest, khien ca ung
    // dung tut tu 72 xuong ~34 khung/giay (do bang glove_diag ngay 2026-09-26:
    // cu ~55 ms lai co 1 khung cham 36-60 ms, dung nhip gui anh). Gio luong chinh
    // chi chep diem anh vao bo dem dung lai; luong phu lat anh, nen JPEG
    // (ImageConversion.EncodeArrayToJPG dung duoc ngoai luong chinh) va gui.
    private System.Threading.Thread _worker;
    private readonly System.Threading.AutoResetEvent _frameReady = new System.Threading.AutoResetEvent(false);
    private volatile bool _workerRunning;
    private volatile bool _workerBusy;        // dang nen/gui khung truoc -> bo qua khung nay
    private bool _readbackPending;            // dang doi GPU tra anh (AsyncGPUReadback) -> chua chup anh moi
    private float _lastStatusTime = -999f, _readbackStartTime;
    private int _readbackId;                  // anh GPU tra ve tre (sau khi da thoi doi) thi bo
    private volatile bool _connectionBroken;  // luong phu bao mat ket noi, luong chinh dong lai
    private Color32[] _flipBuffer;            // chi luong phu dung
    private Color32[] _scaledBuffer;          // anh da thu nho (chi luong phu dung)

    // 2 o anh xoay vong: luong phu nen/gui o nay trong khi GPU tra anh ke tiep vao o kia -> doc anh (1-3 khung)
    // chong len thoi gian nen, van giu ~18 anh/giay. Chi 1 o: anh ve cham hon 1 chut la lo nhip gui, con 8-9 anh/giay
    // (glove_diag 04/10 15:21, ban doc anh khong chan dau tien).
    private sealed class FrameSlot
    {
        public Color32[] Pixels;
        public readonly byte[] Header = new byte[24];
        public int HeaderLength, Width, Height, FrameId;
        public int Downscale = 1; // he so thu nho ghi trong phan dau goi tin (byte 21)
    }
    private readonly FrameSlot[] _slots = { new FrameSlot(), new FrameSlot() };
    private volatile FrameSlot _workerSlot;   // o luong phu dang nen/gui
    private FrameSlot _queuedSlot;            // o da co anh, cho luong phu ranh
    private float _mainThreadMs, _encodeMs, _sendMs; // do thoi gian tung buoc (hien trong status)

    // --- So thu tu anh + huong camera luc chup ---------------------------------
    // Python gui kem "fid" (so thu tu anh) trong ket qua, nen FingerUDPReceiver
    // tra duoc huong camera DUNG LUC CHUP anh do (TryGetFramePose) va dung ngon
    // theo truc cua CAMERA thay vi truc cua mat.
    private const int PoseHistory = 64;
    private readonly int[] _poseIds = new int[PoseHistory];
    private readonly Pose[] _poses = new Pose[PoseHistory];
    // Thoi diem chup tung anh (Time.realtimeSinceStartup) -- de do "tuoi" ket qua Python khi ve toi Unity (chan doan do tre)
    private readonly float[] _captureTimes = new float[PoseHistory];
    // Thoi diem camera PHOI SANG anh do (PassthroughCameraAccess.Timestamp, doi sang Time.realtimeSinceStartup) --
    // som hon _captureTimes mot doan (camera xu ly + doi Unity lay anh). NaN = khong doc duoc.
    private readonly float[] _exposureTimes = new float[PoseHistory];
    /// <summary>Anh moi nhat da phoi sang cach luc lay bao nhieu ms (camera -> Unity), -1 = khong doc duoc.</summary>
    public float LastCameraAgeMs { get; private set; } = -1f;
    private int _frameId;

    // --- Do tre tung khau tren kinh (20/10) -------------------------------------
    // Moc thoi gian cua tung anh theo ClockNow (dung duoc o MOI luong, khac Time.realtimeSinceStartup chi dung o luong
    // chinh): xin anh GPU, GPU tra anh, luong phu bat dau nen, nen xong, gui xong. FingerUDPReceiver ghi glove_latency_*.csv.
    /// <summary>Dong ho (giay) dung chung cho cac moc do tre -- goi duoc tu luong phu.</summary>
    public static double ClockNow => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    public struct FrameTimings
    {
        public double Capture, Readback, EncodeStart, EncodeEnd, Sent;
        public float CameraAgeMs; // phoi sang -> xin anh, -1 = khong doc duoc
        public int JpegBytes;
    }
    private readonly FrameTimings[] _timings = new FrameTimings[PoseHistory];

    /// <summary>Moc thoi gian cua anh so frameId tren kinh (neu con trong bo nho gan day). Moc chua toi = 0.</summary>
    public bool TryGetFrameTimings(int frameId, out FrameTimings timings)
    {
        int slot = ((frameId % PoseHistory) + PoseHistory) % PoseHistory;
        timings = _timings[slot];
        return frameId > 0 && _poseIds[slot] == frameId;
    }
    private readonly byte[] _header = new byte[24]; // BuildHeader ghi o day, roi chep sang o anh

    // Tam tay (khop goc ngon giua) + chieu dai long ban tay THEO QUEST luc chup tung anh -- chinh
    // la cho "goi y" gui Python (vung tim tay). ImageHandSolver lay chieu sau tam tay o day lam goi y.
    private readonly Vector3[] _hintCenters = new Vector3[PoseHistory];
    private readonly float[] _hintPalms = new float[PoseHistory];
    private readonly bool[] _hintOk = new bool[PoseHistory];
    // Goi y moi nhat theo TI LE ANH (goc tren-trai) -- CameraFrameCue dung de biet tay sap ra khoi khung.
    private Vector2 _latestHintUV;
    private Vector2 _latestHintPalmUV;
    private float _latestHintTime = -999f;

    /// <summary>Huong camera luc chup anh so frameId (neu con trong bo nho gan day).</summary>
    public bool TryGetFramePose(int frameId, out Pose pose)
    {
        int slot = ((frameId % PoseHistory) + PoseHistory) % PoseHistory;
        pose = _poses[slot];
        return frameId > 0 && _poseIds[slot] == frameId;
    }

    /// <summary>Anh so frameId da chup cach day bao nhieu giay (anh WiFi + nen + Python + goi tin ve).
    /// Tinh tu luc xin anh GPU -- chua gom do tre rieng cua camera truoc luc do.</summary>
    public bool TryGetFrameAge(int frameId, out float ageSeconds)
    {
        int slot = ((frameId % PoseHistory) + PoseHistory) % PoseHistory;
        bool ok = frameId > 0 && _poseIds[slot] == frameId;
        ageSeconds = ok ? Time.realtimeSinceStartup - _captureTimes[slot] : 0f;
        return ok;
    }

    /// <summary>Thoi diem camera phoi sang anh so frameId, theo dong ho Time.realtimeSinceStartup (de so voi luc tay ao hien).</summary>
    public bool TryGetFrameExposureTime(int frameId, out float realtime)
    {
        int slot = ((frameId % PoseHistory) + PoseHistory) % PoseHistory;
        realtime = _exposureTimes[slot];
        return frameId > 0 && _poseIds[slot] == frameId && !float.IsNaN(realtime);
    }

    /// <summary>Tam tay va chieu dai long ban tay theo Quest luc chup anh frameId (diem goi y).</summary>
    public bool TryGetFrameHint(int frameId, out Vector3 center, out float palm)
    {
        int slot = ((frameId % PoseHistory) + PoseHistory) % PoseHistory;
        center = _hintCenters[slot];
        palm = _hintPalms[slot];
        return frameId > 0 && _poseIds[slot] == frameId && _hintOk[slot];
    }

    /// <summary>Tia tu camera (dung vi tri/huong LUC CHUP anh frameId) xuyen qua 1 diem tren anh.
    /// uv = (x / chieu rong, y / chieu cao) tinh tu goc TREN-trai -- dung nhu anh Python nhan duoc.</summary>
    public bool TryGetFrameRay(int frameId, Vector2 uv, out Ray ray)
    {
        ray = default;
        if (_passthroughCamera == null || !_passthroughCamera.IsPlaying || !TryGetFramePose(frameId, out Pose cam))
            return false;
        // Viewport cua Meta: goc DUOI-trai (0,0) -> lat truc y (giong BuildHeader)
        ray = _passthroughCamera.ViewportPointToRay(new Vector2(uv.x, 1f - uv.y), cam);
        return true;
    }

    /// <summary>Tam tay THEO QUEST tren anh moi nhat: uv = ti le anh tu goc TREN-trai (&lt;0 hoac &gt;1 = ngoai khung),
    /// palmUV = chieu dai long ban tay theo ti le chieu rong (x) / chieu cao (y) anh, age = goi y cu bao nhieu giay.
    /// false = chua lan nao Quest thay tay.</summary>
    public bool TryGetLatestHandHint(out Vector2 uv, out Vector2 palmUV, out float age)
    {
        uv = _latestHintUV;
        palmUV = _latestHintPalmUV;
        age = Time.time - _latestHintTime;
        return _latestHintTime > 0f;
    }

    /// <summary>Huong camera luc chup anh moi nhat.</summary>
    public bool TryGetLatestCameraPose(out Pose pose) => TryGetFramePose(_frameId, out pose);

    /// <summary>Huong tia qua diem uv tren anh (goc TREN-trai), trong toa do CUA CAMERA.</summary>
    public bool TryGetCameraLocalRay(Vector2 uv, out Vector3 direction)
    {
        direction = Vector3.forward;
        if (_passthroughCamera == null || !_passthroughCamera.IsPlaying) return false;
        direction = _passthroughCamera.ViewportPointToRay(new Vector2(uv.x, 1f - uv.y), new Pose(Vector3.zero, Quaternion.identity)).direction;
        return true;
    }

    /// <summary>Phan dau 24 byte truoc anh JPEG (big-endian, khop run_glove_quest_stream.py):
    /// "GLV1" | uint32 so thu tu anh | float32 tam tay x, y (pixel, goc tren-trai) |
    /// float32 kich thuoc tay (pixel, ~chieu dai long ban tay) | uint8 co hop le | uint8 he so thu nho anh (_downscale;
    /// toa do goi y van theo anh GOC) | 2 byte trong.</summary>
    private void BuildHeader(int frameId, Pose camPose, int width, int height)
    {
        float hx = 0f, hy = 0f, size = 0f;
        bool valid = false;
        if (_handCenter != null && _handWrist != null && _gloveHandVisual != null &&
            _gloveHandVisual.Hand != null && _gloveHandVisual.Hand.IsTrackedDataValid)
        {
            Vector3 center = _handCenter.position, wristPos = _handWrist.position;
            // Tay ao co the da bi FingerUDPReceiver dich/xoay theo anh -> dung vi tri THEO QUEST (chua
            // sua) de Python do duoc dung do lech cua Quest, khong phai phan con lai sau khi sua.
            if (_gloveReceiver != null && _gloveReceiver.TryGetRawHandPose(out Vector3 rc, out Vector3 rw))
            {
                center = rc;
                wristPos = rw;
            }
            float depth = Vector3.Dot(center - camPose.position, camPose.rotation * Vector3.forward);
            if (depth > 0.05f)
            {
                // Viewport cua Meta: goc DUOI-trai (0,0) -> anh Python nhan duoc dung chieu, goc TREN-trai
                Vector2 vc = _passthroughCamera.WorldToViewportPoint(center, camPose);
                float palm = Vector3.Distance(center, wristPos);
                Vector2 vs = _passthroughCamera.WorldToViewportPoint(center + camPose.rotation * Vector3.right * palm, camPose);
                hx = vc.x * width;
                hy = (1f - vc.y) * height;
                size = Mathf.Abs(vs.x - vc.x) * width; // chieu dai long ban tay khong bi nghieng lam ngan
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
        // Chi thu nho khi chia het (Python phong lai = co goc nhan he so -- toa do goi y tinh theo co goc)
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

    /// <summary>Thu nho anh vao bo dem dung lai, lat tren-duoi neu _imageIsBottomUp (giong nhanh anh goc). Chi luong phu goi.</summary>
    private Color32[] Downscale(Color32[] src, int w, int h, int f)
    {
        int size = (w / f) * (h / f);
        if (_scaledBuffer == null || _scaledBuffer.Length != size) _scaledBuffer = new Color32[size];
        DownscaleInto(src, w, h, f, _imageIsBottomUp, _scaledBuffer);
        return _scaledBuffer;
    }

    /// <summary>Thu nho anh f lan moi chieu: trung binh tung o f x f (nhu cv2.INTER_AREA ma test_downscale_accuracy.py da
    /// thu), flipRows = hang tren cung cua anh nguon thanh hang duoi cung. dst dai (w/f)*(h/f). Public de thu trong Editor.</summary>
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
        try { _discoveryClient?.Close(); } catch { /* bo qua */ }
        _discoveryClient = null;
    }

    private void Update()
    {
        // Nghe tin hieu tu may tinh moi khung hinh (khong chan, chi doc khi
        // that su co goi tin trong hang doi).
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
        // Anh da doc xong ma luc do luong phu con ban -> giao ngay khi ranh
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
        // Chi tinh la da gui khi THUC SU chup duoc anh moi -- con ban thi thu lai khung sau, khong bo ca nhip 0.05 s
        if (TrySendFrame()) _lastSendTime = Time.time;
        UpdateStatusText();
    }

    private void Dispatch(FrameSlot slot)
    {
        _workerSlot = slot;
        _workerBusy = true;
        _frameReady.Set();
    }

    /// <summary>Doc tin hieu "may chu o day" ma may tinh phat ra (dinh dang
    /// "GLOVE_SERVER|&lt;cong TCP&gt;"). IP cua may tinh lay THANG tu dia chi
    /// nguoi gui goi tin, nen khong can dien tay bao gio.
    ///
    /// Khong chan luong chinh: chi doc khi UdpClient.Available > 0, tuc la da
    /// co san goi tin trong hang doi -- Receive() tra ve ngay lap tuc.</summary>
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
                // Neu may tinh doi IP (doi mang), tu ngat ket noi cu de lan
                // gui ke tiep noi lai vao dia chi moi.
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
            // Dong han socket hong roi de null -- lan Update sau se tao lai tu
            // dau. Neu giu lai socket chua bind duoc, moi lan goi .Available
            // se nem loi lien tuc va khong bao gio tu phuc hoi.
            _lastStatus = "Loi do tim: " + e.Message;
            CloseDiscovery();
        }
    }

    /// <summary>Chup 1 anh moi (yeu cau GPU tra anh, khong chan). true = da chup -- tinh vao nhip gui.</summary>
    private bool TrySendFrame()
    {
        if (_connectionBroken && !_workerBusy)
        {
            _connectionBroken = false;
            CloseConnection();
        }
        if (_client == null || !_client.Connected)
        {
            // Đừng thử kết nối lại mỗi frame nếu đang thất bại liên tục --
            // giới hạn 1 lần mỗi giây để không làm nghẽn Update().
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

        // Anh truoc chua doc xong tu GPU, hoac da co 1 anh doi luong phu -> chua chup them (luong phu dang nen
        // thi VAN chup: anh ve trong luc nen). GPU khong tra anh qua 1 giay (vd texture camera bi tao lai) -> thoi doi.
        if (_readbackPending && Time.unscaledTime - _readbackStartTime > 1f) _readbackPending = false;
        if (_readbackPending || _queuedSlot != null) return false;

        try
        {
            var t0 = System.Diagnostics.Stopwatch.StartNew();
            var res = _passthroughCamera.CurrentResolution;
            Texture tex = _passthroughCamera.GetTexture();
            if (tex == null || res.x <= 0 || res.y <= 0) return false;

            // So thu tu + huong camera luc chup (de dung ngon theo truc camera),
            // va goi y vi tri tay cho Python -- ghi NGAY luc chup, anh ve sau 1-3 khung.
            _frameId++;
            Pose camPose = _passthroughCamera.GetCameraPose();
            int slot = _frameId % PoseHistory;
            _poseIds[slot] = _frameId;
            _poses[slot] = camPose;
            _captureTimes[slot] = Time.realtimeSinceStartup;
            // Timestamp = luc phoi sang (micro giay, cung dong ho voi DateTime.UtcNow) -> doi sang dong ho realtimeSinceStartup.
            // Ngoai 0..2 s = hai dong ho khong cung goc -> bo (NaN), phan tich se biet.
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

            // Doc anh tu GPU KHONG CHAN: truoc day GetColors() bat luong chinh doi GPU (WaitForCompletion) moi lan gui
            // -> khung do bi lo gio. glove_diag 03/10 18:34: khung co ket qua moi (trung nhip voi khung gui) rot 43%,
            // cac khung khac ~10%; ca app chi 62-65/72 khung/giay. Sau khi doi: 72/72, khong rot khung (04/10 15:21).
            int w = res.x, h = res.y;
            _readbackPending = true;
            _readbackStartTime = Time.unscaledTime;
            int id = ++_readbackId;
            int fid = _frameId;
            // Dinh dang goc cua texture (R8G8B8A8, sRGB) -- y het GetColors(); xin doi dinh dang co the bi GPU giai sRGB -> anh toi.
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

    /// <summary>Anh da doc xong tu GPU (luong chinh, 1-3 khung sau Request): chep vao o anh luong phu KHONG dung,
    /// roi giao ngay (luong phu ranh) hoac de doi (Update giao khi ranh). Phan dau goi tin ghi tu luc chup.</summary>
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
        colors.CopyTo(slot.Pixels); // chep vao bo dem dung lai (khong tao mang moi moi lan)
        slot.Width = w;
        slot.Height = h;
        slot.FrameId = frameId;
        slot.Downscale = header != null ? Mathf.Max(1, header[21]) : 1; // khong co phan dau -> Python khong biet de phong lai
        slot.HeaderLength = header != null ? header.Length : 0;
        if (header != null) Array.Copy(header, slot.Header, header.Length);
        _mainThreadMs += (float)t0.Elapsed.TotalMilliseconds;
        if (_workerBusy) _queuedSlot = slot;
        else Dispatch(slot);
    }

    private bool TryConnect()
    {
        // Uu tien dia chi tu do tim duoc; neu chua nhan duoc tin hieu nao thi
        // quay ve IP dien tay (van chay duoc tren mang chan broadcast).
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
            _client.NoDelay = true; // gửi ngay từng khung, không gộp batch -- ưu tiên độ trễ thấp
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
        try { _stream?.Close(); } catch { /* bo qua */ }
        try { _client?.Close(); } catch { /* bo qua */ }
        _stream = null;
        _client = null;
    }

    /// <summary>[4 byte do dai (big-endian)][phan dau tuy chon][anh JPEG] -- do dai tinh ca phan dau.</summary>
    private static void WriteFramed(NetworkStream stream, byte[] header, int headerLength, byte[] data)
    {
        byte[] lenPrefix = BitConverter.GetBytes(headerLength + data.Length);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(lenPrefix); // big-endian, khớp struct.unpack('>I', ...) bên Python
        }
        stream.Write(lenPrefix, 0, lenPrefix.Length);
        if (headerLength > 0) stream.Write(header, 0, headerLength);
        stream.Write(data, 0, data.Length);
    }

    /// <summary>IP may tinh dang nhan anh (tu do tim, hoac IP dien tay) -- cac
    /// dau ra khac (vd GloveForceOutput) gui ve cung may nay.</summary>
    public string ServerIp => _useAutoDiscovery && !string.IsNullOrEmpty(_discoveredIp) ? _discoveredIp : _pcIpAddress;

    /// <summary>Thoi gian (ms) cua lan gui gan nhat: phan tren luong chinh /
    /// nen JPEG / gui qua mang -- de kiem tra luong chinh khong con bi chan.</summary>
    public Vector3 LastTimingsMs => new Vector3(_mainThreadMs, _encodeMs, _sendMs);

    private void UpdateStatusText()
    {
        // Dat lai chu TextMeshPro = dung lai ca mesh chu -> chi 2 lan/giay (truoc day moi lan gui, 18 lan/giay)
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
