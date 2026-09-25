using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Thi nghiem PHAN BIET DO CUNG: moi luot co 2 qua bong trong giong het nhau,
/// 1 qua CHUAN va 1 qua SO SANH (cung hon hoac mem hon); nguoi thu bop ca 2
/// roi chon qua CUNG HON. Khong bao dung/sai sau moi luot (dung chuan
/// thi nghiem tam-vat ly -- "method of constant stimuli").
///
/// Chay lai dung bai nay o cac dieu kien khac nhau (chi hinh anh / hinh anh +
/// rung tay cam / hinh anh + gang) roi so ti le chon dung -> biet haptic co
/// giup phan biet do cung tot hon khong. Y tuong tu cac nghien cuu
/// pseudo-haptics (vd Weiss et al., CHI 2023).
///
/// Dieu khien (tay cam PHAI, hoac ban phim khi chay trong Editor):
///   A / Space        : bat dau (va sang luot sau khi xong)
///   B / R            : dat lai vi tri 2 qua bong truoc mat
///   Can analog trai/phai, hoac mui ten trai/phai : chon qua ben TRAI / PHAI cung hon
///
/// Ghi 2 file CSV (xem ExperimentCsvWriter): moi luot 1 dong, moi su kien
/// cham/cam/tha 1 dong.
/// </summary>
[DefaultExecutionOrder(400)] // sau SquishyPinchable (100) va HapticRenderer (150): doc so lieu da cap nhat
public class StiffnessDiscriminationExperiment : MonoBehaviour
{
    [Serializable]
    public struct StiffnessLevel
    {
        [Tooltip("Do lun toi da, theo phan cua ban kinh. Nho = cung.")]
        [Range(0.02f, 0.6f)] public float maxIndentFraction;
        [Tooltip("Luc (0..1) khi lun den muc toi da. Lon = cung.")]
        [Range(0f, 1f)] public float pressureAtMaxIndent;

        public StiffnessLevel(float maxIndentFraction, float pressureAtMaxIndent)
        {
            this.maxIndentFraction = maxIndentFraction;
            this.pressureAtMaxIndent = pressureAtMaxIndent;
        }

        /// <summary>Cung hon = lun it hon (so sanh chinh), neu bang nhau thi luc lon hon.</summary>
        public bool IsStifferThan(StiffnessLevel other) =>
            maxIndentFraction != other.maxIndentFraction
                ? maxIndentFraction < other.maxIndentFraction
                : pressureAtMaxIndent > other.pressureAtMaxIndent;
    }

    [Header("Người thử & điều kiện (ghi vào tên file và từng dòng)")]
    [SerializeField] private string _participantId = "P01";
    [Tooltip("Ten dieu kien, vd 'visual', 'visual+vibration', 'visual+glove'.")]
    [SerializeField] private string _conditionLabel = "visual+vibration";
    [Tooltip("Bat/tat cac dau ra haptic ben duoi cho dieu kien nay (TAT = chi co hinh anh).")]
    [SerializeField] private bool _hapticsEnabled = true;
    [SerializeField] private MonoBehaviour[] _hapticOutputs;

    [Header("Vật và hiển thị")]
    [Tooltip("Qua ben TRAI (tu goc nhin nguoi thu).")]
    [SerializeField] private SquishyPinchable _leftBall;
    [Tooltip("Qua ben PHAI.")]
    [SerializeField] private SquishyPinchable _rightBall;
    [SerializeField] private HapticRenderer _hapticRenderer;
    [SerializeField] private Transform _head;
    [SerializeField] private TMP_Text _instructions;
    [Tooltip("An cac object nay trong luc chay thi nghiem (vd qua bong thu khac).")]
    [SerializeField] private GameObject[] _hideWhileRunning;
    [Tooltip("Vi tri cap bong so voi dau luc dat lai: y = cao/thap, z = xa.")]
    [SerializeField] private Vector3 _placementOffset = new Vector3(0f, -0.15f, 0.35f);

    [Header("Mức độ cứng")]
    [SerializeField] private StiffnessLevel _standard = new StiffnessLevel(0.25f, 0.6f);
    [SerializeField] private StiffnessLevel[] _comparisons =
    {
        new StiffnessLevel(0.10f, 0.90f),
        new StiffnessLevel(0.15f, 0.80f),
        new StiffnessLevel(0.20f, 0.70f),
        new StiffnessLevel(0.30f, 0.50f),
        new StiffnessLevel(0.35f, 0.40f),
        new StiffnessLevel(0.40f, 0.30f),
    };
    [SerializeField] private int _repeatsPerComparison = 4;
    [Tooltip("Bat buoc cham CA 2 qua truoc khi duoc tra loi.")]
    [SerializeField] private bool _requireTouchBoth = true;
    [SerializeField] private float _pauseBetweenTrials = 1f;
    [Tooltip("0 = ngau nhien moi lan. Dat so khac 0 de lap lai dung thu tu luot (vd khi so sanh dieu kien).")]
    [SerializeField] private int _randomSeed;

    private enum Phase { Waiting, Trial, Pause, Done }

    private struct Trial
    {
        public StiffnessLevel comparison;
        public bool standardOnLeft;
    }

    private class BallStats
    {
        public float maxSquish, maxOvershoot, heldSeconds;
        public int touches, grabs;
        public bool wasHeld;

        public void Reset()
        {
            maxSquish = maxOvershoot = heldSeconds = 0f;
            touches = grabs = 0;
            wasHeld = false;
        }
    }

    private readonly List<Trial> _trials = new List<Trial>();
    private readonly BallStats _leftStats = new BallStats();
    private readonly BallStats _rightStats = new BallStats();
    private readonly Dictionary<float, int[]> _correctByComparison = new Dictionary<float, int[]>();

    private Phase _phase = Phase.Waiting;
    private int _trialIndex;
    private float _trialStart;
    private float _pauseUntil;
    private string _sessionTag;
    private ExperimentCsvWriter _trialLog;
    private ExperimentCsvWriter _eventLog;

    private void Start()
    {
        foreach (var output in _hapticOutputs)
        {
            if (output != null) output.enabled = _hapticsEnabled;
        }
        if (_hapticRenderer != null) _hapticRenderer.WhenHapticEvent += OnHapticEvent;
        PlaceInFrontOfHead();
        SetBallsVisible(false);
        // Chu hien trong kinh viet KHONG DAU: font mac dinh cua TextMeshPro
        // khong co dau tieng Viet (se hien o vuong).
        Show($"THI NGHIEM DO CUNG\nNguoi thu: {_participantId}   Dieu kien: {_conditionLabel}\n\nBam A (tay cam phai) de bat dau\nBam B de dat lai vi tri bong");
    }

    private void OnDestroy()
    {
        if (_hapticRenderer != null) _hapticRenderer.WhenHapticEvent -= OnHapticEvent;
        _trialLog?.Dispose();
        _eventLog?.Dispose();
    }

    private void Update()
    {
        if (RecenterPressed()) PlaceInFrontOfHead();

        switch (_phase)
        {
            case Phase.Waiting:
                if (StartPressed()) BeginSession();
                break;

            case Phase.Trial:
                int answer = AnswerPressed(); // -1 trai, +1 phai, 0 chua
                if (answer != 0) TryAnswer(answer < 0);
                break;

            case Phase.Pause:
                if (Time.time >= _pauseUntil) BeginTrial();
                break;

            case Phase.Done:
                if (StartPressed())
                {
                    _phase = Phase.Waiting;
                    Show("Bam A de chay lai mot phien moi");
                }
                break;
        }
    }

    private void LateUpdate()
    {
        if (_phase != Phase.Trial) return;
        Sample(_leftBall, _leftStats);
        Sample(_rightBall, _rightStats);
    }

    // --- Phien / luot ----------------------------------------------------------

    private void BeginSession()
    {
        var rng = _randomSeed != 0 ? new System.Random(_randomSeed) : new System.Random();
        _trials.Clear();
        _correctByComparison.Clear();
        foreach (var comparison in _comparisons)
        {
            _correctByComparison[comparison.maxIndentFraction] = new int[2]; // [dung, tong]
            for (int r = 0; r < _repeatsPerComparison; r++)
            {
                _trials.Add(new Trial { comparison = comparison, standardOnLeft = rng.Next(2) == 0 });
            }
        }
        // Xao tron thu tu (Fisher-Yates)
        for (int i = _trials.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (_trials[i], _trials[j]) = (_trials[j], _trials[i]);
        }

        _sessionTag = $"{Sanitize(_participantId)}_{Sanitize(_conditionLabel)}_{DateTime.Now:yyyyMMdd_HHmmss}";
        _trialLog?.Dispose();
        _eventLog?.Dispose();
        _trialLog = new ExperimentCsvWriter(_sessionTag + "_trials.csv",
            "participant", "condition", "haptics", "trial", "standard_side",
            "standard_indent", "standard_pressure", "comparison_indent", "comparison_pressure",
            "comparison_is_stiffer", "response_side", "response_is_comparison", "correct", "response_time_s",
            "left_max_squish", "left_max_overshoot_mm", "left_touches", "left_grabs", "left_held_s",
            "right_max_squish", "right_max_overshoot_mm", "right_touches", "right_grabs", "right_held_s",
            "timestamp");
        _eventLog = new ExperimentCsvWriter(_sessionTag + "_events.csv",
            "participant", "condition", "trial", "t_s", "finger", "event", "object", "amplitude");
        Debug.Log($"[Experiment] Ghi du lieu vao {_trialLog.FilePath}", this);

        foreach (var go in _hideWhileRunning)
        {
            if (go != null) go.SetActive(false);
        }
        // Luc ung dung vua mo, kinh co the chua co vi tri dau -> dat lai o day
        PlaceInFrontOfHead();
        _trialIndex = 0;
        BeginTrial();
    }

    private void BeginTrial()
    {
        if (_trialIndex >= _trials.Count)
        {
            EndSession();
            return;
        }

        Trial trial = _trials[_trialIndex];
        Apply(_leftBall, trial.standardOnLeft ? _standard : trial.comparison);
        Apply(_rightBall, trial.standardOnLeft ? trial.comparison : _standard);
        SetBallsVisible(true);
        _leftBall.ResetState();
        _rightBall.ResetState();
        _leftStats.Reset();
        _rightStats.Reset();

        _trialStart = Time.time;
        _phase = Phase.Trial;
        Show($"Luot {_trialIndex + 1}/{_trials.Count}\nBop ca hai qua, roi gat can analog\n<- TRAI   hoac   PHAI ->\nde chon qua CUNG HON");
    }

    private void TryAnswer(bool leftChosen)
    {
        if (_requireTouchBoth && (_leftStats.touches == 0 || _rightStats.touches == 0))
        {
            Show($"Luot {_trialIndex + 1}/{_trials.Count}\nHay bop CA HAI qua truoc khi chon");
            return;
        }

        Trial trial = _trials[_trialIndex];
        bool comparisonStiffer = trial.comparison.IsStifferThan(_standard);
        bool chosenIsComparison = leftChosen != trial.standardOnLeft;
        bool correct = chosenIsComparison == comparisonStiffer;
        float responseTime = Time.time - _trialStart;

        int[] tally = _correctByComparison[trial.comparison.maxIndentFraction];
        if (correct) tally[0]++;
        tally[1]++;

        _trialLog.WriteRow(
            _participantId, _conditionLabel, _hapticsEnabled, _trialIndex + 1, trial.standardOnLeft ? "left" : "right",
            _standard.maxIndentFraction, _standard.pressureAtMaxIndent,
            trial.comparison.maxIndentFraction, trial.comparison.pressureAtMaxIndent,
            comparisonStiffer, leftChosen ? "left" : "right", chosenIsComparison, correct, responseTime,
            _leftStats.maxSquish, _leftStats.maxOvershoot * 1000f, _leftStats.touches, _leftStats.grabs, _leftStats.heldSeconds,
            _rightStats.maxSquish, _rightStats.maxOvershoot * 1000f, _rightStats.touches, _rightStats.grabs, _rightStats.heldSeconds,
            DateTime.Now.ToString("o"));

        _trialIndex++;
        SetBallsVisible(false);
        _pauseUntil = Time.time + _pauseBetweenTrials;
        _phase = Phase.Pause;
        Show("Da ghi nhan");
    }

    private void EndSession()
    {
        _phase = Phase.Done;
        SetBallsVisible(false);
        foreach (var go in _hideWhileRunning)
        {
            if (go != null) go.SetActive(true);
        }

        var summary = new System.Text.StringBuilder("XONG! Ti le chon dung theo muc so sanh:\n");
        int correctAll = 0, totalAll = 0;
        foreach (var comparison in _comparisons)
        {
            int[] tally = _correctByComparison[comparison.maxIndentFraction];
            summary.Append($"lun {comparison.maxIndentFraction:0.00}: {tally[0]}/{tally[1]}\n");
            correctAll += tally[0];
            totalAll += tally[1];
        }
        summary.Append($"Tong: {correctAll}/{totalAll}");
        Show(summary.ToString());
        Debug.Log($"[Experiment] {summary}\nFile: {_trialLog?.FilePath}", this);

        _trialLog?.Dispose();
        _eventLog?.Dispose();
        _trialLog = _eventLog = null;
    }

    // --- Do dac -----------------------------------------------------------------

    private void Sample(SquishyPinchable ball, BallStats stats)
    {
        if (ball == null) return;
        stats.maxSquish = Mathf.Max(stats.maxSquish, ball.Squish01);
        stats.maxOvershoot = Mathf.Max(stats.maxOvershoot, ball.OvershootMeters);
        if (ball.IsHeld)
        {
            stats.heldSeconds += Time.deltaTime;
            if (!stats.wasHeld) stats.grabs++;
        }
        stats.wasHeld = ball.IsHeld;
    }

    private void OnHapticEvent(HapticFinger finger, HapticEventType type, HapticTransient transient, SquishyPinchable obj)
    {
        if (_phase != Phase.Trial) return;

        string side = obj == _leftBall ? "left" : obj == _rightBall ? "right" : obj != null ? obj.name : "";
        if (type == HapticEventType.Touch)
        {
            if (obj == _leftBall) _leftStats.touches++;
            else if (obj == _rightBall) _rightStats.touches++;
        }
        _eventLog?.WriteRow(_participantId, _conditionLabel, _trialIndex + 1, Time.time - _trialStart,
            finger.ToString(), type.ToString(), side, transient.amplitude);
    }

    // --- Tien ich ---------------------------------------------------------------

    private static void Apply(SquishyPinchable ball, StiffnessLevel level)
    {
        ball.MaxIndentFraction = level.maxIndentFraction;
        if (ball.TryGetComponent(out HapticMaterial material)) material.PressureAtMaxIndent = level.pressureAtMaxIndent;
    }

    private void SetBallsVisible(bool visible)
    {
        if (_leftBall != null) _leftBall.gameObject.SetActive(visible);
        if (_rightBall != null) _rightBall.gameObject.SetActive(visible);
    }

    /// <summary>Dat cap bong truoc mat, CO DINH trong khong gian (khong di theo
    /// dau nua) de nguoi thu vuon tay toi.</summary>
    private void PlaceInFrontOfHead()
    {
        if (_head == null) return;
        Vector3 forward = Vector3.ProjectOnPlane(_head.forward, Vector3.up);
        if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
        forward.Normalize();
        transform.SetPositionAndRotation(
            _head.position + forward * _placementOffset.z + Vector3.up * _placementOffset.y,
            Quaternion.LookRotation(forward, Vector3.up));
        if (_phase == Phase.Trial)
        {
            _leftBall.ResetState();
            _rightBall.ResetState();
        }
    }

    private void Show(string text)
    {
        if (_instructions != null) _instructions.text = text;
    }

    private static string Sanitize(string s)
    {
        foreach (char c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '-');
        return s.Replace(' ', '-').Replace('+', '-');
    }

    // --- Dieu khien -------------------------------------------------------------

    private static bool StartPressed() =>
        OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch) ||
        (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame);

    private static bool RecenterPressed() =>
        OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch) ||
        (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame);

    private static int AnswerPressed()
    {
        if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstickLeft, OVRInput.Controller.RTouch) ||
            (Keyboard.current != null && Keyboard.current.leftArrowKey.wasPressedThisFrame)) return -1;
        if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstickRight, OVRInput.Controller.RTouch) ||
            (Keyboard.current != null && Keyboard.current.rightArrowKey.wasPressedThisFrame)) return 1;
        return 0;
    }
}
