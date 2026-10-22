using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Oculus.Interaction;
using UnityEngine;

/// <summary>
/// Tay gang ao: nhan diem anh tu Python (run_glove_quest_stream.py, UDP 5005) va dung tay ao TU ANH.
///
/// Duong du lieu:
///   camera passthrough -> GloveLiveStreamer gui anh (kem so thu tu fid) -> Python RTMPose tim 9 diem
///   (co tay, ngon cai, ngon tro) + cum mieng luc giac tren mu ban tay -> UDP ve day ->
///   ImageHandSolver doi moi diem thanh 1 tia tu camera LUC CHUP va khop ban tay ao vao cac tia.
///
/// HandVisual (Quest) van ghi tu the tay moi khung; script nay ghi de ngay sau do. Quest chi con gop
/// chieu sau tay (da lam muot) va la tay du phong khi Python mat tay lau.
/// 3 ngon giua / ap ut / ut luon nam (model gang moi khong hoc 3 ngon nay).
///
/// Gan cung GameObject voi HandFingerRig (StaticHandModel_Right). Object nay la con cua
/// CenterEyeAnchor nhung KHONG gan theo dau: co tay duoc dat thang trong the gioi.
/// </summary>
[RequireComponent(typeof(HandFingerRig))]
public class FingerUDPReceiver : MonoBehaviour
{
    [Header("Mạng")]
    [SerializeField] private int _port = 5005;

    [Tooltip("Component 'Hand Visual' cung object -- dung tay NGAY SAU khi HandVisual ghi xong khop (event WhenHandVisualUpdated). " +
             "Bo trong van chay (LateUpdate) nhung khong dam bao ghi de sau Quest.")]
    [SerializeField] private HandVisual _handVisual;
    [Tooltip("Tay trai hay phai -- de biet phia nao la long ban tay khi tu tim truc gap ngon.")]
    [SerializeField] private bool _leftHand = false;
    [Tooltip("BAT de chan doan: moi khung ghi 1 dong CSV vao Application.persistentDataPath/glove_diag_*.csv. " +
             "Lay ve bang: adb pull /sdcard/Android/data/<ten goi>/files/ . Nho TAT sau khi chan doan xong.")]
    [SerializeField] private bool _logDiagnostics = false;

    [Header("Mất dữ liệu")]
    [Tooltip("Khong nhan duoc goi tin nao trong so giay nay (Python tat, mat mang...) -> coi nhu mat tay.")]
    [SerializeField] private float _dataTimeoutSeconds = 0.5f;
    [Tooltip("Dang CAM vat ma Python mat tay: giu tu the cuoi toi da so giay nay (thay vi tra ve tay Quest, vat tuot khoi tay).")]
    [SerializeField] private float _holdPoseWhileGraspingSeconds = 1.5f;

    [Header("3 ngon giua / ap ut / ut")]
    [Tooltip("BAT: 3 ngon giua/ap ut/ut LUON nam lai -- chi ngon cai + tro theo anh.")]
    [SerializeField] private bool _keepOtherFingersClosed = true;
    [Tooltip("Goc nam cua 3 ngon do (do): gap khop goc / giua / dau.")]
    [SerializeField] private Vector3 _closedFingerAngles = new Vector3(75f, 95f, 60f);

    // Ngon cai(0) va tro(1) do ImageHandSolver dung; 3 ngon con lai luon nam.
    private const int NumLiveTrackedFingers = 2;

    private HandFingerRig _rig;
    private Transform[][] _fingerJoints; // [0]=cai, [1]=tro, [2]=giua, [3]=ap ut, [4]=ut
    private FingerChainFitter[] _fitters; // dong hoc tung ngon (ImageHandSolver dung ngon cai + tro)
    private Transform _wrist;
    private ImageHandSolver _imageSolver;

    // --- Du lieu UDP (luong nhan ghi, luong chinh doc trong _lock) ---
    private readonly object _lock = new object();
    private volatile bool _dataValid;
    private volatile bool _packetArrived;
    private int _latestFrameId;
    private readonly Vector2[] _latestUV = new Vector2[HandImageFit.Points];
    private readonly float[] _latestConf = new float[HandImageFit.Points];
    private bool _latestPixelValid;
    private int _latestPixelFid;
    private Vector2 _latestDorsalUV;
    private int _latestDorsal = -1; // -1 Python khong bao, 0 khong thay cum luc giac, 1 thay
    private float _latestDorsalArea;

    private UdpClient _udpClient;
    private Thread _receiveThread;
    private volatile bool _running;

    private float _lastDataTime = -999f;
    private float _lastTrustedTime = -999f;
    private bool _packetThisFrame;
    private Transform _thumbTipCache;

    // Tu the tay THEO QUEST (truoc khi ghi de) -- GloveLiveStreamer dung lam goi y cho Python,
    // ImageHandSolver dung lam chieu sau goi y / tay du phong.
    private Vector3 _rawCenter, _rawWrist;
    private Quaternion _rawWristRot = Quaternion.identity;
    private bool _hasRaw;
    private Quaternion _alignedWristRot;
    private Vector3 _alignedWristPos;
    private bool _hasAligned;

    private System.IO.StreamWriter _diag;
    private readonly System.Text.StringBuilder _diagLine = new System.Text.StringBuilder(1400); // dung lai moi khung
    private GloveLiveStreamer _streamer;
    private FingertipSurfaceConstraint _constraint;
    private GloveWristStabilizer _wristStabilizer;

    // --- Do tre tung khau (20/10): moi anh co ket qua 1 dong glove_latency_*.csv (cung _logDiagnostics) ---
    // Luong nhan ghi luc goi tin toi + phan "tm:" Python gui kem; luong chinh ghep voi moc thoi gian tren kinh
    // (GloveLiveStreamer.TryGetFrameTimings) va luc ImageHandSolver lay ket qua. tools/latency_breakdown.py doc file nay.
    private struct PacketTiming { public int Fid; public double Arrive; public string Py; }
    private readonly System.Collections.Concurrent.ConcurrentQueue<PacketTiming> _timingQueue =
        new System.Collections.Concurrent.ConcurrentQueue<PacketTiming>();
    private readonly System.Collections.Generic.List<PacketTiming> _pendingTimings = new System.Collections.Generic.List<PacketTiming>();
    private const int PickHistory = 64;
    private readonly int[] _pickFid = new int[PickHistory];
    private readonly double[] _pickTime = new double[PickHistory];
    private int _lastPickFid;
    private const int PyTimingFields = 11; // so truong trong "tm:" (run_glove_quest_stream.py)
    private System.IO.StreamWriter _latencyLog;

    private void Awake()
    {
        _rig = GetComponent<HandFingerRig>();
        _fingerJoints = new[]
        {
            new[] { _rig.thumbMetacarpal, _rig.thumbProximal, _rig.thumbDistal },
            new[] { _rig.indexProximal, _rig.indexIntermediate, _rig.indexDistal },
            new[] { _rig.middleProximal, _rig.middleIntermediate, _rig.middleDistal },
            new[] { _rig.ringProximal, _rig.ringIntermediate, _rig.ringDistal },
            new[] { _rig.pinkyProximal, _rig.pinkyIntermediate, _rig.pinkyDistal },
        };
        CreateFitters();
    }

    /// <summary>Tao dong hoc tung ngon tu tu the nghi cua rig, roi khoi tao ImageHandSolver.</summary>
    private void CreateFitters()
    {
        _wrist = _rig.indexProximal != null ? _rig.indexProximal.parent : null;
        while (_wrist != null && !_wrist.name.Contains("Wrist")) _wrist = _wrist.parent;
        if (_wrist == null || _rig.middleProximal == null || _rig.pinkyProximal == null) return;

        Vector3 palmTarget = FingerChainFitter.PalmTarget(
            _wrist, _rig.indexProximal, _rig.middleProximal, _rig.pinkyProximal, _leftHand);
        Vector3 palmNormal = FingerChainFitter.PalmNormal(
            _wrist, _rig.indexProximal, _rig.middleProximal, _rig.pinkyProximal, _leftHand);

        _fitters = new FingerChainFitter[_fingerJoints.Length];
        for (int f = 0; f < _fingerJoints.Length; f++)
        {
            Transform[] chain = _fingerJoints[f];
            if (Array.IndexOf(chain, null) >= 0 || chain[2].childCount == 0) continue;
            _fitters[f] = new FingerChainFitter(chain, chain[2].GetChild(0), f == 0, palmTarget, palmNormal);
        }
        if (_fitters[0] == null || _fitters[1] == null) return;

        _imageSolver = GetComponent<ImageHandSolver>();
        if (_imageSolver == null) _imageSolver = gameObject.AddComponent<ImageHandSolver>();
        _imageSolver.Initialize(this, _wrist, _fitters, palmTarget, _closedFingerAngles);
    }

    private void OnEnable()
    {
        _running = true;
        _receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
        _receiveThread.Start();
        if (_handVisual != null) _handVisual.WhenHandVisualUpdated += ApplyHand;
    }

    private void OnDisable()
    {
        _running = false;
        _udpClient?.Close();
        _receiveThread?.Join(200);
        if (_handVisual != null) _handVisual.WhenHandVisualUpdated -= ApplyHand;
        _diag?.Dispose();
        _diag = null;
        _latencyLog?.Dispose();
        _latencyLog = null;
    }

    /// <summary>Tong goc gap (do) cua 1 ngon (0 = duoi thang) -- de uoc luong muc co cua actuator.
    /// f: 0 = cai, 1 = tro...</summary>
    public float FingerFlexionDegrees(int f)
    {
        if (_fitters == null || f < 0 || f >= _fitters.Length || _fitters[f] == null) return 0f;
        float[] a = _fitters[f].Angles;
        return a[1] + a[2] + a[3];
    }

    /// <summary>Tam ban tay (khop goc ngon giua) va co tay THEO QUEST, chua bi ghi de theo anh.
    /// GloveLiveStreamer dung de tinh goi y vi tri cho Python.</summary>
    public bool TryGetRawHandPose(out Vector3 center, out Vector3 wrist)
    {
        center = _rawCenter;
        wrist = _rawWrist;
        return _hasRaw;
    }

    /// <summary>Diem anh moi nhat (cho ImageHandSolver). false = goi tin moi nhat khong co diem hop le.
    /// dorsal: -1 Python khong bao, 0 khong thay cum luc giac tren mu ban tay, 1 thay (dorsalUV).</summary>
    public bool TryGetPixelPoints(out int frameId, Vector2[] uv, float[] conf, out int dorsal, out Vector2 dorsalUV)
    {
        lock (_lock)
        {
            frameId = _latestPixelFid;
            dorsal = _latestDorsal;
            dorsalUV = _latestDorsalUV;
            if (!_latestPixelValid || frameId <= 0) return false;
            Array.Copy(_latestUV, uv, HandImageFit.Points);
            Array.Copy(_latestConf, conf, HandImageFit.Points);
            if (frameId != _lastPickFid)
            {
                // Lan dau tay ao lay ket qua anh nay (cot pick_ms trong glove_latency)
                _lastPickFid = frameId;
                _pickFid[frameId % PickHistory] = frameId;
                _pickTime[frameId % PickHistory] = GloveLiveStreamer.ClockNow;
            }
            return true;
        }
    }

    // ===== NHAN UDP =====

    private void ReceiveLoop()
    {
        try
        {
            _udpClient = new UdpClient(_port);
            var remoteEP = new IPEndPoint(IPAddress.Any, _port);
            while (_running)
            {
                byte[] data = _udpClient.Receive(ref remoteEP); // chan toi khi co goi tin
                double arrive = GloveLiveStreamer.ClockNow;
                ParseAndStore(System.Text.Encoding.UTF8.GetString(data), arrive);
            }
        }
        catch (SocketException)
        {
            // Xay ra binh thuong khi dong socket luc OnDisable -- bo qua.
        }
    }

    /// <summary>Goi tin dang "valid:1,fid:123,pv:x|y;...,pc:c;...,dv:x|y,da:0.07,tm:..." "valid" luon dung dau;
    /// "tm" (tuy chon) = thoi gian tung khau ben Python cho anh nay. arrive = luc goi tin toi (ClockNow).</summary>
    private void ParseAndStore(string msg, double arrive)
    {
        _packetArrived = true;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        lock (_lock)
        {
            string pyTimings = null;
            foreach (var part in msg.Split(','))
            {
                var kv = part.Split(':');
                if (kv.Length != 2) continue;
                string key = kv[0].Trim();
                if (key == "pv") { ParsePixelPoints(kv[1]); continue; }
                if (key == "tm") { pyTimings = kv[1]; continue; }
                if (key == "pc") { ParsePixelConf(kv[1]); continue; }
                if (key == "dv")
                {
                    var xy = kv[1].Split('|');
                    if (xy.Length == 2 &&
                        float.TryParse(xy[0], System.Globalization.NumberStyles.Float, inv, out float dx) &&
                        float.TryParse(xy[1], System.Globalization.NumberStyles.Float, inv, out float dy))
                        _latestDorsalUV = new Vector2(dx, dy);
                    continue;
                }
                if (!float.TryParse(kv[1], System.Globalization.NumberStyles.Float, inv, out float v)) continue;
                if (key == "valid")
                {
                    _dataValid = v > 0.5f;
                    _latestPixelValid = false;
                    _latestDorsal = -1;
                    continue;
                }
                if (key == "fid") { _latestFrameId = (int)v; continue; }
                if (key == "da") { _latestDorsalArea = v; _latestDorsal = v > 0f ? 1 : 0; continue; }
            }
            if (_logDiagnostics && pyTimings != null && _latestFrameId > 0)
                _timingQueue.Enqueue(new PacketTiming { Fid = _latestFrameId, Arrive = arrive, Py = pyTimings });
        }
    }

    private void ParsePixelPoints(string value)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var items = value.Split(';');
        if (items.Length < HandImageFit.Points) return;
        for (int i = 0; i < HandImageFit.Points; i++)
        {
            var xy = items[i].Split('|');
            if (xy.Length != 2 ||
                !float.TryParse(xy[0], System.Globalization.NumberStyles.Float, inv, out float u) ||
                !float.TryParse(xy[1], System.Globalization.NumberStyles.Float, inv, out float v))
                return;
            _latestUV[i] = new Vector2(u, v);
            _latestConf[i] = 1f; // Python khong gui "pc" -> tin het
        }
        _latestPixelValid = true;
        _latestPixelFid = _latestFrameId; // "fid" dung truoc "pv" trong goi tin
    }

    private void ParsePixelConf(string value)
    {
        var items = value.Split(';');
        for (int i = 0; i < HandImageFit.Points && i < items.Length; i++)
            if (float.TryParse(items[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float c))
                _latestConf[i] = c;
    }

    // ===== DUNG TAY =====

    private void Update()
    {
        _packetThisFrame = _packetArrived;
        if (_packetArrived)
        {
            _packetArrived = false;
            _lastDataTime = Time.time;
        }
    }

    // Chay moi khung du co gan _handVisual hay khong (duong bao dam neu event khong ban).
    private void LateUpdate()
    {
        ApplyHand();
        if (_logDiagnostics)
        {
            WriteDiagnostics();
            WriteLatencyLog();
        }
    }

    /// <summary>Goi ngay sau khi HandVisual ghi xong khop (va lai trong LateUpdate).</summary>
    private void ApplyHand()
    {
        if (_wrist == null) return;
        KeepOtherFingersClosed();
        if (_imageSolver == null || !_imageSolver.isActiveAndEnabled) return;

        bool questUpdated = CaptureRawPose();
        bool trusted = _dataValid && Time.time - _lastDataTime <= _dataTimeoutSeconds;
        if (trusted) _lastTrustedTime = Time.time;
        // Dang cam vat ma mat du lieu ngan: giu tu the lau hon (khong de vat tuot)
        bool holdForGrasp = !trusted && Time.time - _lastTrustedTime <= _holdPoseWhileGraspingSeconds && IsGrasping();
        _imageSolver.Drive(questUpdated, _rawWrist, _rawWristRot, trusted, holdForGrasp);
        MarkAligned();
    }

    /// <summary>Ep 3 ngon giua/ap ut/ut ve dang nam -- moi lan ghi xuong (HandVisual ghi lai ca 5 ngon moi khung).</summary>
    private void KeepOtherFingersClosed()
    {
        if (!_keepOtherFingersClosed || _fitters == null) return;
        for (int f = NumLiveTrackedFingers; f < _fitters.Length; f++)
        {
            FingerChainFitter fitter = _fitters[f];
            if (fitter == null) continue;
            fitter.SetAngles(0f, _closedFingerAngles.x, _closedFingerAngles.y, _closedFingerAngles.z);
            fitter.Apply(1f);
        }
    }

    /// <summary>Co tay khac tu the minh vua dat = Quest vua ghi -> luu tu the THEO QUEST (true).
    /// Giong nhau = khung nay da dung tay roi (ham chay 2 lan moi khung).</summary>
    private bool CaptureRawPose()
    {
        if (_hasAligned && Quaternion.Angle(_wrist.rotation, _alignedWristRot) < 0.01f &&
            (_wrist.position - _alignedWristPos).sqrMagnitude < 1e-10f) return false;
        _rawCenter = _rig.middleProximal.position;
        _rawWrist = _wrist.position;
        _rawWristRot = _wrist.rotation;
        _hasRaw = true;
        return true;
    }

    private void MarkAligned()
    {
        _alignedWristRot = _wrist.rotation;
        _alignedWristPos = _wrist.position;
        _hasAligned = true;
    }

    /// <summary>Ban tay nay dang cam vat nao do khong.</summary>
    private bool IsGrasping()
    {
        if (_holdPoseWhileGraspingSeconds <= 0f) return false;
        if (_thumbTipCache == null)
        {
            Transform distal = _rig.thumbDistal;
            if (distal == null || distal.childCount == 0) return false;
            _thumbTipCache = distal.GetChild(0);
        }
        foreach (var obj in SquishyPinchable.Active)
            if (obj != null && obj.IsHoldingTip(_thumbTipCache)) return true;
        foreach (var body in PhysicsPinchGrabbable.Active)
            if (body != null && body.IsHoldingTip(_thumbTipCache)) return true;
        return false;
    }

    // ===== CHAN DOAN =====

    /// <summary>Ghi 1 dong chan doan cho khung hien tai (xem _logDiagnostics).</summary>
    private void WriteDiagnostics()
    {
        if (_fitters == null || _wrist == null) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (_diag == null)
        {
            string path = System.IO.Path.Combine(Application.persistentDataPath,
                "glove_diag_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
            _diag = new System.IO.StreamWriter(path, false, new System.Text.UTF8Encoding(false));
            var head = new System.Text.StringBuilder("t,frame,packet,valid,held,tipgap,fid");
            head.Append(",wx,wy,wz,wqx,wqy,wqz,wqw,vx,vy,vz,vqx,vqy,vqz,vqw,rwx,rwy,rwz,rqx,rqy,rqz,rqw");
            for (int i = 0; i < HandImageFit.Points; i++) head.Append($",u{i},v{i}");
            for (int i = 0; i < HandImageFit.Points; i++) head.Append($",c{i}");
            head.Append(",dorsal,du,dv,da");
            for (int f = 0; f < NumLiveTrackedFingers; f++) for (int j = 0; j < 4; j++) head.Append($",a{f}_{j}");
            head.Append(",solv,sfid,serr,scost,sdepth,szprior,sswitch,sms,spinch,sdorsal,hscale");
            // Hieu nang: do dai khung truoc (ms), luong chinh cua lan gui anh gan nhat, rang buoc ngon + bong (khung truoc)
            head.Append(",dtms,streamms,conms,sqms");
            // Muc tin ket qua anh gan nhat (ImageHandSolver) + trang thai kep bong cua tay nay (FingertipSurfaceConstraint.PinchStatus)
            head.Append(",trust,pinchst");
            // Do tre + tay ra khoi khung (18/10): tuoi anh vua giai, co tay hien lech ket qua giai (bo loc), muc tin
            // co tay / ngon cai / ngon tro, so diem sat mep, trang thai (0 du / 1 mot phan / 2 mat), gate luc, ngon giu dang
            head.Append(",age_ms,lag_mm,lag_deg,wtrust,ttrust,itrust,edgepts,hstate,fweight,tfrozen,ifrozen");
            // Do tre tay ao so voi tay that (19/10): dong ho chung realtimeSinceStartup cho dong nay (rt), tuoi anh tinh tu luc
            // phoi sang, camera -> Unity, va co tay giai THO cua anh gan nhat kem luc phoi sang anh do (sexp; 0 = khong biet).
            // tools/latency_diag.py so (rt, wx..wz) voi (sexp, sx..sz) de tim do tre.
            head.Append(",rt,exp_age_ms,cam_ms,sexp,sx,sy,sz");
            // Co tay Quest THO (truoc GloveWristStabilizer) -- thu kha thi bu tre bang Quest (20/10): vi tri the gioi, khung
            // nay Quest co theo doi khong, do tin cay cao khong, luc nhan (realtime). tools/quest_raw_feasibility.py doc cac cot nay.
            head.Append(",qrx,qry,qrz,qvalid,qconf,qrt");
            _diag.WriteLine(head.ToString());
            Debug.Log($"[FingerUDPReceiver] Ghi chan doan vao {path}", this);
        }

        Transform view = transform.parent != null ? transform.parent : transform;
        Vector3 wp = _wrist.position, vp = view.position;
        Quaternion wq = _wrist.rotation, vq = view.rotation;
        Transform thumbTip = _rig.thumbDistal != null && _rig.thumbDistal.childCount > 0 ? _rig.thumbDistal.GetChild(0) : null;
        Transform indexTip = _rig.indexDistal != null && _rig.indexDistal.childCount > 0 ? _rig.indexDistal.GetChild(0) : null;
        float tipGap = thumbTip != null && indexTip != null ? Vector3.Distance(thumbTip.position, indexTip.position) : -1f;

        var sb = _diagLine;
        sb.Clear();
        sb.Append(Time.time.ToString("F4", inv)).Append(',').Append(Time.frameCount).Append(',')
          .Append(_packetThisFrame ? 1 : 0).Append(',').Append(_dataValid ? 1 : 0).Append(',')
          .Append(IsGrasping() ? 1 : 0).Append(',').Append(tipGap.ToString("F4", inv));
        lock (_lock)
        {
            sb.Append(',').Append(_latestPixelFid);
            foreach (float v in new[] { wp.x, wp.y, wp.z, wq.x, wq.y, wq.z, wq.w, vp.x, vp.y, vp.z, vq.x, vq.y, vq.z, vq.w,
                                        _rawWrist.x, _rawWrist.y, _rawWrist.z, _rawWristRot.x, _rawWristRot.y, _rawWristRot.z, _rawWristRot.w })
                sb.Append(',').Append(v.ToString("F5", inv));
            for (int i = 0; i < HandImageFit.Points; i++)
                sb.Append(',').Append(_latestUV[i].x.ToString("F4", inv)).Append(',').Append(_latestUV[i].y.ToString("F4", inv));
            for (int i = 0; i < HandImageFit.Points; i++) sb.Append(',').Append(_latestConf[i].ToString("F2", inv));
            sb.Append(',').Append(_latestDorsal).Append(',').Append(_latestDorsalUV.x.ToString("F4", inv))
              .Append(',').Append(_latestDorsalUV.y.ToString("F4", inv)).Append(',').Append(_latestDorsalArea.ToString("F3", inv));
        }
        for (int f = 0; f < NumLiveTrackedFingers; f++)
            for (int j = 0; j < 4; j++) sb.Append(',').Append(_fitters[f] != null ? _fitters[f].Angles[j].ToString("F2", inv) : "");
        ImageHandSolver s = _imageSolver;
        bool ok = s != null && s.Ready;
        sb.Append(',').Append(ok ? s.Weight.ToString("F3", inv) : "0").Append(',').Append(ok ? s.SolvedFrameId : 0);
        foreach (float v in new[] { ok ? s.ImageError : 0f, ok ? s.Cost : 0f, ok ? s.Depth : 0f, ok ? s.DepthPrior : 0f })
            sb.Append(',').Append(v.ToString("F4", inv));
        sb.Append(',').Append(ok ? s.Switches : 0)
          .Append(',').Append((ok ? s.SolveMs : 0f).ToString("F2", inv))
          .Append(',').Append((ok ? s.Pinch : 0f).ToString("F2", inv))
          .Append(',').Append(ok ? s.DorsalUsed : -1)
          .Append(',').Append((ok ? s.HandScale : 0f).ToString("F3", inv));
        if (_streamer == null) _streamer = FindAnyObjectByType<GloveLiveStreamer>();
        if (_constraint == null) _constraint = GetComponent<FingertipSurfaceConstraint>();
        sb.Append(',').Append((Time.unscaledDeltaTime * 1000f).ToString("F1", inv))
          .Append(',').Append((_streamer != null ? _streamer.LastTimingsMs.x : 0f).ToString("F2", inv))
          .Append(',').Append(FingertipSurfaceConstraint.FrameMs.ToString("F2", inv))
          .Append(',').Append(SquishyPinchable.FrameMs.ToString("F2", inv))
          .Append(',').Append((ok ? s.LastTrust : 0f).ToString("F2", inv))
          .Append(',').Append(_constraint != null ? _constraint.PinchStatus : -1);
        foreach (float v in new[] { ok ? s.FrameAgeMs : 0f, ok ? s.LagMm : 0f, ok ? s.LagDeg : 0f })
            sb.Append(',').Append(v.ToString("F1", inv));
        foreach (float v in new[] { ok ? s.WristTrust : 0f, ok ? s.ThumbTrust : 0f, ok ? s.IndexTrust : 0f })
            sb.Append(',').Append(v.ToString("F2", inv));
        sb.Append(',').Append(ok ? s.EdgePoints : 0)
          .Append(',').Append(ok ? s.State : 2)
          .Append(',').Append((ok ? s.ForceWeight : 0f).ToString("F2", inv))
          .Append(',').Append(ok && s.ThumbFrozen ? 1 : 0)
          .Append(',').Append(ok && s.IndexFrozen ? 1 : 0);
        Vector3 raw = ok ? s.RawWrist : Vector3.zero;
        sb.Append(',').Append(Time.realtimeSinceStartup.ToString("F4", inv))
          .Append(',').Append((ok ? s.ExposureAgeMs : 0f).ToString("F1", inv))
          .Append(',').Append((_streamer != null ? _streamer.LastCameraAgeMs : -1f).ToString("F1", inv))
          .Append(',').Append((ok ? s.RawExposureTime : 0f).ToString("F4", inv));
        foreach (float v in new[] { raw.x, raw.y, raw.z }) sb.Append(',').Append(v.ToString("F5", inv));
        if (_wristStabilizer == null && Time.frameCount % 60 == 0) _wristStabilizer = FindAnyObjectByType<GloveWristStabilizer>();
        GloveWristStabilizer q = _wristStabilizer;
        Vector3 qp = q != null ? q.RawWorldRoot.position : Vector3.zero;
        foreach (float v in new[] { qp.x, qp.y, qp.z }) sb.Append(',').Append(v.ToString("F5", inv));
        sb.Append(',').Append(q != null && q.RawValid ? 1 : 0)
          .Append(',').Append(q != null && q.RawHighConfidence ? 1 : 0)
          .Append(',').Append((q != null ? q.RawTime : 0f).ToString("F4", inv));
        _diag.WriteLine(sb);
        if (Time.frameCount % 72 == 0) _diag.Flush(); // khong mat du lieu neu ung dung bi tat ngang
    }

    /// <summary>Ghi glove_latency: moi anh co ket qua 1 dong, khi tay ao da lay ket qua do (hoac 0.5 s sau khi toi
    /// ma chua lay -- anh moi hon da ve truoc, pick_ms = -1). Moi cot la ms giua 2 moc lien tiep:
    /// phoi sang -> xin anh (cam) -> GPU tra anh (rb) -> luong phu bat dau nen (wq) -> nen xong (enc) -> gui xong (send)
    /// -> ket qua ve toi kinh (rtt; gom WiFi di + xep hang + Python + WiFi ve) -> tay ao lay (pick).</summary>
    private void WriteLatencyLog()
    {
        while (_timingQueue.TryDequeue(out PacketTiming p)) _pendingTimings.Add(p);
        if (_pendingTimings.Count == 0) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (_latencyLog == null)
        {
            string path = System.IO.Path.Combine(Application.persistentDataPath,
                "glove_latency_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
            _latencyLog = new System.IO.StreamWriter(path, false, new System.Text.UTF8Encoding(false));
            _latencyLog.WriteLine("fid,arr_s,jpeg_kb,cam_ms,rb_ms,wq_ms,enc_ms,send_ms,rtt_ms," +
                                  "py_blocked,py_wait_ms,py_xfer_ms,py_decode_ms,py_infer_ms,py_ninfer,py_other_ms,py_pc_ms," +
                                  "py_prev_cycle_ms,py_prev_tail_ms,py_dropped,pick_ms,total_ms");
            Debug.Log($"[FingerUDPReceiver] Ghi do tre tung khau vao {path}", this);
        }
        if (_streamer == null) _streamer = FindAnyObjectByType<GloveLiveStreamer>();
        double now = GloveLiveStreamer.ClockNow;
        var sb = new System.Text.StringBuilder(256);
        int keep = 0;
        for (int i = 0; i < _pendingTimings.Count; i++)
        {
            PacketTiming p = _pendingTimings[i];
            int ps = p.Fid % PickHistory;
            bool picked = _pickFid[ps] == p.Fid;
            if (!picked && now - p.Arrive < 0.5)
            {
                _pendingTimings[keep++] = p; // cho tay ao lay ket qua nay
                continue;
            }
            GloveLiveStreamer.FrameTimings t = default;
            bool ok = _streamer != null && _streamer.TryGetFrameTimings(p.Fid, out t);
            string Ms(double from, double to) => ok && from > 0 && to > 0 ? ((to - from) * 1000.0).ToString("F1", inv) : "-1";
            sb.Clear();
            sb.Append(p.Fid).Append(',').Append(p.Arrive.ToString("F4", inv))
              .Append(',').Append(ok ? (t.JpegBytes / 1024f).ToString("F1", inv) : "-1")
              .Append(',').Append(ok ? t.CameraAgeMs.ToString("F1", inv) : "-1")
              .Append(',').Append(Ms(t.Capture, t.Readback))
              .Append(',').Append(Ms(t.Readback, t.EncodeStart))
              .Append(',').Append(Ms(t.EncodeStart, t.EncodeEnd))
              .Append(',').Append(Ms(t.EncodeEnd, t.Sent))
              .Append(',').Append(Ms(t.Sent, p.Arrive));
            string[] py = p.Py.Split('|');
            for (int k = 0; k < PyTimingFields; k++) sb.Append(',').Append(k < py.Length ? py[k] : "");
            sb.Append(',').Append(picked ? Ms(p.Arrive, _pickTime[ps]) : "-1");
            sb.Append(',').Append(picked && ok && t.CameraAgeMs >= 0f
                ? (t.CameraAgeMs + (_pickTime[ps] - t.Capture) * 1000.0).ToString("F1", inv) : "-1");
            _latencyLog.WriteLine(sb);
        }
        _pendingTimings.RemoveRange(keep, _pendingTimings.Count - keep);
        if (Time.frameCount % 72 == 0) _latencyLog.Flush();
    }
}
