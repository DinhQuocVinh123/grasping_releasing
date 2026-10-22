using System;
using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.Input;
using UnityEngine;

/// <summary>
/// Records how the Quest tracks the BARE hand (how far each joint rotates, which joints move together) so the
/// glove hand (ImageHandSolver) can learn natural hand poses. One CSV row per tracked frame, with the position
/// and rotation of the 26 OpenXR joints RELATIVE TO THE WRIST, independent of the displayed hand model.
///
/// File: Application.persistentDataPath/hand_pose_*.csv. Copy with: adb pull /sdcard/Android/data/[package]/files/
/// Turn _record OFF when done: each frame writes about 2 KB.
/// </summary>
[DefaultExecutionOrder(1000)]
public sealed class HandPoseRecorder : MonoBehaviour
{
    [Tooltip("ON = record. Only frames where the Quest is tracking the hand are written.")]
    [SerializeField] private bool _record = true;
    [Tooltip("HandVisuals to record (one 'hand' column each). Empty = find the left hand (OVRHandVisualLeft) and the right glove hand (raw Quest data).")]
    [SerializeField] private HandVisual[] _hands;

    private System.IO.StreamWriter _file;
    private int _rows;
    private readonly System.Text.StringBuilder _sb = new System.Text.StringBuilder(4096);

    private void Start()
    {
        if (_hands != null && _hands.Length > 0) return;
        var list = new List<HandVisual>();
        foreach (var hv in FindObjectsByType<HandVisual>(FindObjectsInactive.Exclude))
        {
            // The displayed Quest left hand (OVRHandVisualLeft) and the glove hand (has FingerUDPReceiver -> Hand = raw Quest data)
            if (hv.name == "OVRHandVisualLeft" || hv.GetComponent<FingerUDPReceiver>() != null) list.Add(hv);
        }
        _hands = list.ToArray();
        Debug.Log($"[HandPoseRecorder] Recording {_hands.Length} hand(s)", this);
    }

    private void LateUpdate()
    {
        if (!_record || _hands == null) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var hv in _hands)
        {
            IHand hand = hv != null ? hv.Hand : null;
            if (hand == null || !hand.IsTrackedDataValid) continue;
            if (!hand.GetJointPosesFromWrist(out ReadOnlyHandJointPoses poses)) continue;
            if (!hand.GetRootPose(out Pose root)) continue;
            if (_file == null) Open(poses.Count);

            _sb.Clear();
            _sb.Append(Time.time.ToString("F4", inv)).Append(',')
               .Append(hand.Handedness == Handedness.Left ? 'L' : 'R').Append(',')
               .Append(hand.IsHighConfidence ? 1 : 0).Append(',')
               .Append(hand.Scale.ToString("F4", inv));
            Append(root.position, root.rotation, inv);
            Transform head = Camera.main != null ? Camera.main.transform : null;
            Append(head != null ? head.position : Vector3.zero, head != null ? head.rotation : Quaternion.identity, inv);
            for (int i = 0; i < poses.Count; i++) Append(poses[i].position, poses[i].rotation, inv);
            _file.WriteLine(_sb.ToString());
            if (++_rows % 120 == 0) _file.Flush();
        }
    }

    private void Append(Vector3 p, Quaternion q, IFormatProvider inv)
    {
        foreach (float v in new[] { p.x, p.y, p.z }) _sb.Append(',').Append(v.ToString("F5", inv));
        foreach (float v in new[] { q.x, q.y, q.z, q.w }) _sb.Append(',').Append(v.ToString("F5", inv));
    }

    private void Open(int joints)
    {
        string path = System.IO.Path.Combine(Application.persistentDataPath,
            "hand_pose_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
        _file = new System.IO.StreamWriter(path, false, new System.Text.UTF8Encoding(false));
        var head = new System.Text.StringBuilder("t,hand,conf,scale,rx,ry,rz,rqx,rqy,rqz,rqw,hx,hy,hz,hqx,hqy,hqz,hqw");
        for (int i = 0; i < joints; i++)
        {
            string n = ((HandJointId)i).ToString();
            head.Append($",{n}_x,{n}_y,{n}_z,{n}_qx,{n}_qy,{n}_qz,{n}_qw");
        }
        _file.WriteLine(head.ToString());
        Debug.Log($"[HandPoseRecorder] Writing to {path}", this);
    }

    private void OnDisable()
    {
        _file?.Dispose();
        _file = null;
    }
}
