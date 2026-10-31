using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// "Virtual test room": replays a tracker recording (run_glove_quest_stream.py --record: JPEG frames + keypoints.jsonl + log.csv)
/// through the headset's own code (ImageHandSolver + HandImageFit, the scene's settings) and writes, per frame, the shown virtual
/// hand as a skinned mesh in the camera frame, so tools/analysis/replay_lab_render.py can draw it over the camera image.
///
/// The camera stands at the origin looking along +Z (recordings have no headset pose). The Quest's hand-depth hint is rebuilt from
/// the recorded hint (hand centre in pixels + palm length in pixels): depth = f * palm / size.
///
/// Batch mode (Unity closed):
///   Unity.exe -batchmode -quit -projectPath . -executeMethod GloveReplayLab.Run -logFile lab.log
///     -labRec "20261029_185058;20261029_163121" -labOut out_dir [-labEvery 5 (0 = no meshes)] [-labFrom 1] [-labTo 99999]
///     [-labSets "base|deep:DepthPriorWeight=0.5;_errBad=0.3"]   (one run per set and recording, into out_dir/set/recording)
/// </summary>
public static class GloveReplayLab
{
    // Right passthrough camera at 1280 x 1280 (Meta camera API, 29/10)
    public static float Fx = 866f, Fy = 866f, Cx = 635.5f, Cy = 637.0f, Width = 1280f, Height = 1280f;
    /// <summary>Quest palm length (wrist -> middle finger base, m) used to turn the hint size back into a depth.</summary>
    public static float HintPalm = 0.095f;
    // Second (left) passthrough camera, from the stereo recordings' own calibration (29/10 16:31, 3307 point pairs): its pose in the
    // main camera's frame (m; x right, y up, z forward) and its principal point. UseSecondView = solve with both cameras' points.
    public static bool UseSecondView = false;
    public static float Cam2X = -0.0638f, Cam2Y = -0.0002f, Cam2Z = -0.0014f;
    public static float Cam2QX = 0.00031f, Cam2QY = 0.004f, Cam2QZ = -0.00309f;
    public static float Cx2 = 642.1f, Cy2 = 640.5f;
    private const float DisplayHz = 72f;

    private static string Arg(string name, string fallback)
    {
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return fallback;
    }

    public static void Run()
    {
        string recs = Arg("-labRec", null), outRoot = Arg("-labOut", null), sets = Arg("-labSets", "base");
        int every = int.Parse(Arg("-labEvery", "5")), from = int.Parse(Arg("-labFrom", "1")), to = int.Parse(Arg("-labTo", "999999"));
        var report = new StringBuilder();
        bool failed = false;
        if (recs == null || outRoot == null) report.Append("thieu -labRec / -labOut");
        else
        {
            SaveDefaults();
            foreach (string set in sets.Split('|'))
            {
                string name = set.Split(':')[0];
                string assignments = set.Contains(":") ? set.Substring(set.IndexOf(':') + 1) : "";
                foreach (string rec in recs.Split(';'))
                {
                    string dir = Directory.Exists(rec) ? rec : Path.Combine(RecordingsRoot, rec);
                    try
                    {
                        EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity"); // fresh component state per run
                        RestoreDefaults();
                        Apply(assignments);
                        string outDir = Path.Combine(outRoot, name, Path.GetFileName(dir));
                        report.Append(name).Append(" | ").Append(Replay(dir, outDir, every, from, to)).Append('\n');
                    }
                    catch (System.Exception e)
                    {
                        report.Append("LOI ").Append(name).Append(' ').Append(rec).Append(": ").Append(e).Append('\n');
                        failed = true;
                    }
                }
            }
        }
        Debug.Log("[GloveReplayLab]\n" + report);
        if (Application.isBatchMode) EditorApplication.Exit(failed ? 1 : 0);
    }

    public static string RecordingsRoot = "D:/untitled folder 2/VR/recordings";
    private static readonly Dictionary<string, object> Defaults = new Dictionary<string, object>();
    private const System.Reflection.BindingFlags StaticPublic = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;

    private static void SaveDefaults()
    {
        foreach (var t in new[] { typeof(HandImageFit), typeof(GloveReplayLab) })
            foreach (var f in t.GetFields(StaticPublic))
                if (!f.IsInitOnly && !f.IsLiteral) Defaults[t.Name + "." + f.Name] = f.GetValue(null);
    }

    private static void RestoreDefaults()
    {
        foreach (var t in new[] { typeof(HandImageFit), typeof(GloveReplayLab) })
            foreach (var f in t.GetFields(StaticPublic))
                if (Defaults.TryGetValue(t.Name + "." + f.Name, out object v)) f.SetValue(null, v);
    }

    /// <summary>"Name=value;..." -> a public static field of HandImageFit / GloveReplayLab, else a serialized field of the scene's
    /// ImageHandSolver (set before Initialize).</summary>
    private static void Apply(string assignments)
    {
        var inv = CultureInfo.InvariantCulture;
        var solver = Object.FindAnyObjectByType<ImageHandSolver>(FindObjectsInactive.Include);
        var so = new SerializedObject(solver);
        foreach (string a in assignments.Split(';'))
        {
            if (!a.Contains("=")) continue;
            string key = a.Substring(0, a.IndexOf('=')).Trim(), val = a.Substring(a.IndexOf('=') + 1).Trim();
            var field = typeof(HandImageFit).GetField(key, StaticPublic) ?? typeof(GloveReplayLab).GetField(key, StaticPublic);
            if (field != null)
            {
                if (field.FieldType == typeof(float)) field.SetValue(null, float.Parse(val, inv));
                else if (field.FieldType == typeof(int)) field.SetValue(null, int.Parse(val, inv));
                else if (field.FieldType == typeof(bool)) field.SetValue(null, bool.Parse(val));
                else if (field.FieldType == typeof(Vector3[]))
                {
                    // "PointOffset=2:0.001,0,0.004/6:..." -> element 2 = (0.001, 0, 0.004), ... (unlisted elements = 0)
                    var arr = new Vector3[((Vector3[])field.GetValue(null)).Length];
                    foreach (string e in val.Split('/'))
                    {
                        if (!e.Contains(":")) continue;
                        var xyz = e.Substring(e.IndexOf(':') + 1).Split(',');
                        arr[int.Parse(e.Substring(0, e.IndexOf(':')), inv)] =
                            new Vector3(float.Parse(xyz[0], inv), float.Parse(xyz[1], inv), float.Parse(xyz[2], inv));
                    }
                    field.SetValue(null, arr);
                }
                continue;
            }
            var prop = so.FindProperty(key);
            if (prop == null) throw new System.Exception("khong co tham so " + key);
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Float: prop.floatValue = float.Parse(val, inv); break;
                case SerializedPropertyType.Integer: prop.intValue = int.Parse(val, inv); break;
                case SerializedPropertyType.Boolean: prop.boolValue = bool.Parse(val); break;
                default: throw new System.Exception("kieu tham so khong ho tro " + key);
            }
        }
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private sealed class Rig
    {
        public FingerUDPReceiver Receiver;
        public ImageHandSolver Solver;
        public Transform Wrist;
        public FingerChainFitter[] Fitters;
        public Vector3 Closed;
        public SkinnedMeshRenderer Skin;
        public Transform[] Points; // bones at the model's 9 points (0 and 5 get HandImageFit's offsets)
    }

    private static Rig BuildRig()
    {
        if (EditorSceneManager.GetActiveScene().path != "Assets/Scenes/SampleScene.unity")
            EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
        var recv = Object.FindAnyObjectByType<FingerUDPReceiver>(FindObjectsInactive.Include);
        if (recv == null) throw new System.Exception("khong tim thay FingerUDPReceiver");
        var rig = recv.GetComponent<HandFingerRig>();
        var so = new SerializedObject(recv);
        bool left = so.FindProperty("_leftHand").boolValue;
        Transform wrist = rig.indexProximal.parent;
        while (wrist != null && !wrist.name.Contains("Wrist")) wrist = wrist.parent;
        Vector3 palmT = FingerChainFitter.PalmTarget(wrist, rig.indexProximal, rig.middleProximal, rig.pinkyProximal, left);
        Vector3 palmN = FingerChainFitter.PalmNormal(wrist, rig.indexProximal, rig.middleProximal, rig.pinkyProximal, left);
        Transform[][] chains =
        {
            new[] { rig.thumbMetacarpal, rig.thumbProximal, rig.thumbDistal },
            new[] { rig.indexProximal, rig.indexIntermediate, rig.indexDistal },
            new[] { rig.middleProximal, rig.middleIntermediate, rig.middleDistal },
            new[] { rig.ringProximal, rig.ringIntermediate, rig.ringDistal },
            new[] { rig.pinkyProximal, rig.pinkyIntermediate, rig.pinkyDistal },
        };
        var fitters = new FingerChainFitter[5];
        for (int f = 0; f < 5; f++) fitters[f] = new FingerChainFitter(chains[f], chains[f][2].GetChild(0), f == 0, palmT, palmN);
        var solver = recv.GetComponent<ImageHandSolver>();
        solver.Initialize(recv, wrist, fitters[0], fitters[1], new[] { rig.middleProximal, rig.ringProximal, rig.pinkyProximal });
        SkinnedMeshRenderer skin = null;
        foreach (var r in recv.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (r.enabled && r.gameObject.activeInHierarchy) skin = r;
        return new Rig
        {
            Receiver = recv, Solver = solver, Wrist = wrist, Fitters = fitters,
            Closed = so.FindProperty("_closedFingerAngles").vector3Value, Skin = skin,
            Points = new[] { wrist, rig.thumbMetacarpal, rig.thumbProximal, rig.thumbDistal, rig.thumbDistal.GetChild(0),
                             rig.indexProximal, rig.indexIntermediate, rig.indexDistal, rig.indexDistal.GetChild(0) },
        };
    }

    private struct Frame
    {
        public int Id;
        public double T;
        public bool Found;
        public Vector2[] Px;
        public float[] Conf;
        public bool Has2;      // the second camera's points (stereo recordings 29/10)
        public Vector2[] Px2;
        public float[] Conf2;
        public Vector2[] PxK;  // knuckles: the model's points 9, 13, 17
        public float[] ConfK;
        public bool HasHint;
        public Vector3 Hint;
    }

    private static List<Frame> LoadRecording(string dir)
    {
        var inv = CultureInfo.InvariantCulture;
        var times = new Dictionary<int, double>();
        var sent = new Dictionary<int, bool>();
        string log = Path.Combine(dir, "log.csv");
        if (File.Exists(log))
        {
            var lines = File.ReadAllLines(log);
            var head = new List<string>(lines[0].Split(','));
            int iF = head.IndexOf("frame"), iT = head.IndexOf("t"), iS = head.IndexOf("sent");
            for (int i = 1; i < lines.Length; i++)
            {
                var c = lines[i].Split(',');
                if (c.Length <= Mathf.Max(iF, iT)) continue;
                int id = int.Parse(c[iF], inv);
                times[id] = double.Parse(c[iT], inv);
                if (iS >= 0 && iS < c.Length) sent[id] = c[iS] != "" && c[iS] != "no" && c[iS] != "0";
            }
        }
        var frames = new List<Frame>();
        foreach (string line in File.ReadLines(Path.Combine(dir, "keypoints.jsonl")))
        {
            var j = MiniJson(line);
            int id = (int)(double)j["frame"];
            var f = new Frame { Id = id, T = times.TryGetValue(id, out double t) ? t : id / 15.0, Px = new Vector2[9], Conf = new float[9] };
            f.Found = j.TryGetValue("found", out object fo) && fo is bool b && b && j["kpts"] is List<object>;
            if (f.Found && sent.TryGetValue(id, out bool s) && !s) f.Found = false;
            if (f.Found)
            {
                var k = (List<object>)j["kpts"];
                var sc = (List<object>)j["scores"];
                for (int i = 0; i < 9; i++)
                {
                    var p = (List<object>)k[i];
                    f.Px[i] = new Vector2((float)(double)p[0], (float)(double)p[1]);
                    f.Conf[i] = (float)(double)sc[i];
                }
                // Middle / ring / little finger base points (9, 13, 17) when the recording has all 21 points
                f.PxK = new Vector2[HandImageFit.Knuckles]; f.ConfK = new float[HandImageFit.Knuckles];
                for (int n = 0; n < HandImageFit.Knuckles; n++)
                {
                    int i = 9 + 4 * n;
                    if (i >= k.Count || i >= sc.Count) continue;
                    var p = (List<object>)k[i];
                    f.PxK[n] = new Vector2((float)(double)p[0], (float)(double)p[1]);
                    f.ConfK[n] = (float)(double)sc[i];
                }
                if (j.TryGetValue("kpts2", out object k2o) && k2o is List<object> k2 && j.TryGetValue("scores2", out object s2o) && s2o is List<object> s2
                    && k2.Count >= 9 && s2.Count >= 9)
                {
                    f.Has2 = true; f.Px2 = new Vector2[9]; f.Conf2 = new float[9];
                    for (int i = 0; i < 9; i++)
                    {
                        var p = (List<object>)k2[i];
                        f.Px2[i] = new Vector2((float)(double)p[0], (float)(double)p[1]);
                        f.Conf2[i] = (float)(double)s2[i];
                    }
                }
            }
            if (j.TryGetValue("hint", out object h) && h is List<object> hl && hl.Count >= 3 && (double)hl[2] > 1.0)
            {
                f.HasHint = true;
                float hx = (float)(double)hl[0], hy = (float)(double)hl[1], size = (float)(double)hl[2];
                float z = Fx * HintPalm / size;
                f.Hint = new Vector3((hx - Cx) / Fx * z, -(hy - Cy) / Fy * z, z);
            }
            frames.Add(f);
        }
        frames.Sort((a, b) => a.Id.CompareTo(b.Id));
        return frames;
    }

    /// <summary>Replays one recording. Writes out/frames.csv (per image: raw + shown pose summary, the 9 model points and the 9 raw
    /// solved joints projected to pixels) and out/mesh_XXXXX.bin (shown skinned mesh vertices in camera coordinates, float32 x,y,z)
    /// every `every` images, plus out/mesh_tris.bin (int32 triangle indices, once).</summary>
    public static string Replay(string recDir, string outDir, int every, int from, int to)
    {
        Directory.CreateDirectory(outDir);
        var rig = BuildRig();
        var frames = LoadRecording(recDir);
        if (frames.Count == 0) return "ban ghi rong";
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("frame,t,found,solved,state,serr,trust,depth,zprior,switches,reseeds,px,py,pz,qx,qy,qz,qw,dpx,dpy,dpz,dqx,dqy,dqz,dqw");
        for (int i = 0; i < 9; i++) sb.Append($",u{i},v{i},c{i}");
        for (int i = 0; i < 9; i++) sb.Append($",ru{i},rv{i}");
        for (int i = 0; i < 9; i++) sb.Append($",su{i},sv{i}");
        sb.Append(",shown_err,raw_err");
        for (int i = 0; i < 9; i++) sb.Append($",rx{i},ry{i},rz{i}");
        for (int i = 0; i < 9; i++) sb.Append($",sx{i},sy{i},sz{i}");
        sb.Append(",fscale");
        for (int i = 0; i < 9; i++) sb.Append($",bx{i},by{i},bz{i}");
        sb.Append('\n');

        Matrix4x4[] bindposes = null; BoneWeight[] weights = null; Vector3[] restVerts = null, skinned = null;
        if (rig.Skin != null)
        {
            bindposes = rig.Skin.sharedMesh.bindposes;
            weights = rig.Skin.sharedMesh.boneWeights;
            restVerts = rig.Skin.sharedMesh.vertices;
            skinned = new Vector3[restVerts.Length];
        }
        if (rig.Skin != null)
            using (var w = new BinaryWriter(File.Create(Path.Combine(outDir, "mesh_tris.bin"))))
                foreach (int t in rig.Skin.sharedMesh.triangles) w.Write(t);

        var uv = new Vector2[9]; var dirs = new Vector3[9]; var conf = new float[9];
        var uv2 = new Vector2[9]; var dirs2 = new Vector3[9];
        var uvK = new Vector2[HandImageFit.Knuckles]; var dirsK = new Vector3[HandImageFit.Knuckles];
        float qw = Mathf.Sqrt(Mathf.Max(0f, 1f - Cam2QX * Cam2QX - Cam2QY * Cam2QY - Cam2QZ * Cam2QZ));
        Pose cam2 = new Pose(new Vector3(Cam2X, Cam2Y, Cam2Z), new Quaternion(Cam2QX, Cam2QY, Cam2QZ, qw));
        var dbgDirs = new Vector3[9]; var dbgW = new float[9]; var joints = new Vector3[9]; var bones = new Vector3[9]; var shownJ = new Vector3[9];
        var rawPx = new Vector2[9]; var shownPx = new Vector2[9]; var shown3 = new Vector3[9]; var shownErrs = new List<float>(); var gaps = new List<float>();
        Pose cam = new Pose(Vector3.zero, Quaternion.identity);
        double t0 = frames[0].T;
        float tDisp = 0f;
        int solved = 0, rendered = 0;
        foreach (var f in frames)
        {
            float now = (float)(f.T - t0);
            // Rendered frames up to this image (the display keeps gliding between images)
            while (tDisp + 1f / DisplayHz <= now)
            {
                tDisp += 1f / DisplayHz;
                rig.Solver.StepDisplay(tDisp, 1f / DisplayHz);
            }
            bool ok = false;
            if (f.Found)
            {
                for (int i = 0; i < 9; i++)
                {
                    uv[i] = new Vector2(f.Px[i].x / Width, f.Px[i].y / Height);
                    dirs[i] = new Vector3((f.Px[i].x - Cx) / Fx, -(f.Px[i].y - Cy) / Fy, 1f).normalized;
                    conf[i] = f.Conf[i];
                }
                if (f.PxK != null)
                {
                    for (int n = 0; n < HandImageFit.Knuckles; n++)
                    {
                        uvK[n] = new Vector2(f.PxK[n].x / Width, f.PxK[n].y / Height);
                        dirsK[n] = new Vector3((f.PxK[n].x - Cx) / Fx, -(f.PxK[n].y - Cy) / Fy, 1f).normalized;
                    }
                    rig.Solver.SetKnuckles(uvK, f.ConfK, dirsK);
                }
                if (UseSecondView && f.Has2)
                {
                    for (int i = 0; i < 9; i++)
                    {
                        uv2[i] = new Vector2(f.Px2[i].x / Width, f.Px2[i].y / Height);
                        dirs2[i] = cam2.rotation * new Vector3((f.Px2[i].x - Cx2) / Fx, -(f.Px2[i].y - Cy2) / Fy, 1f).normalized;
                    }
                    rig.Solver.SetSecondView(cam2, uv2, f.Conf2, dirs2);
                }
                rig.Solver.ApplyHandScale(); // as Drive does each frame (glove size; no left hand here)
                ok = rig.Solver.SolveImage(now, f.Id, cam, uv, conf, dirs, f.HasHint, f.Hint);
                if (ok) solved++;
            }
            // The first rendered frame after this image arrived
            tDisp += 1f / DisplayHz;
            rig.Solver.StepDisplay(tDisp, 1f / DisplayHz);
            if (f.Id < from || f.Id > to) continue;

            bool shown = rig.Solver.TryGetShownWrist(out Vector3 dp, out Quaternion dq);
            bool hasRaw = rig.Solver.TryGetDebugView(out _, dbgDirs, dbgW, joints);
            sb.Append(f.Id).Append(',').Append(now.ToString("F3", inv)).Append(',').Append(f.Found ? 1 : 0).Append(',').Append(ok ? 1 : 0)
              .Append(',').Append(rig.Solver.State);
            foreach (float v in new[] { rig.Solver.ImageError, rig.Solver.LastTrust, rig.Solver.Depth, rig.Solver.DepthPrior })
                sb.Append(',').Append(v.ToString("F4", inv));
            sb.Append(',').Append(rig.Solver.Switches).Append(',').Append(rig.Solver.Reseeds);
            Vector3 rp = rig.Solver.RawWrist;
            Quaternion rq = rig.Solver.RawRotation;
            foreach (float v in new[] { rp.x, rp.y, rp.z, rq.x, rq.y, rq.z, rq.w, dp.x, dp.y, dp.z, dq.x, dq.y, dq.z, dq.w })
                sb.Append(',').Append(v.ToString("F5", inv));
            for (int i = 0; i < 9; i++)
                sb.Append(',').Append(f.Px[i].x.ToString("F1", inv)).Append(',').Append(f.Px[i].y.ToString("F1", inv))
                  .Append(',').Append(f.Conf[i].ToString("F3", inv));
            for (int i = 0; i < 9; i++)
            {
                Vector2 q = hasRaw ? ToPixel(joints[i]) : new Vector2(-1f, -1f);
                sb.Append(',').Append(q.x.ToString("F1", inv)).Append(',').Append(q.y.ToString("F1", inv));
                rawPx[i] = q;
            }
            // The SHOWN hand at the 9 points (what the user sees, filters included)
            if (shown) WritePose(rig, dp, dq);
            if (shown) { rig.Solver.CheckRenderGap(); gaps.Add(rig.Solver.RenderGapMm); }
            for (int i = 0; i < 9; i++) shownJ[i] = rig.Points[i].position;
            float fs = rig.Solver.RawScale;
            for (int i = 0; i < 9; i++)
            {
                Vector3 w = HandImageFit.PointFromJoints(i, shownJ, rig.Wrist.rotation, fs * HandImageFit.PalmScale, fs);
                shownPx[i] = shown ? ToPixel(w) : new Vector2(-1f, -1f);
                shown3[i] = w;
                sb.Append(',').Append(shownPx[i].x.ToString("F1", inv)).Append(',').Append(shownPx[i].y.ToString("F1", inv));
            }
            float se = f.Found && shown ? PixelError(f, shownPx) : -1f, re = f.Found && hasRaw ? PixelError(f, rawPx) : -1f;
            sb.Append(',').Append(se.ToString("F4", inv)).Append(',').Append(re.ToString("F4", inv));
            // 3D (camera coordinates, m): raw solved joints, shown joints
            foreach (var arr in new[] { joints, shown3 })
                for (int i = 0; i < 9; i++)
                    sb.Append(',').Append(arr[i].x.ToString("F4", inv)).Append(',').Append(arr[i].y.ToString("F4", inv))
                      .Append(',').Append(arr[i].z.ToString("F4", inv));
            // Raw solve's virtual joints (no point offsets) + size, for tools/analysis/calibrate_glove_points.py
            sb.Append(',').Append(rig.Solver.RawScale.ToString("F4", inv));
            bool hasBones = rig.Solver.TryGetRawJoints(bones);
            for (int i = 0; i < 9; i++)
                sb.Append(',').Append(hasBones ? bones[i].x.ToString("F4", inv) : "").Append(',').Append(hasBones ? bones[i].y.ToString("F4", inv) : "")
                  .Append(',').Append(hasBones ? bones[i].z.ToString("F4", inv) : "");
            sb.Append('\n');
            if (se >= 0f) shownErrs.Add(se);

            if (shown && rig.Skin != null && every > 0 && (f.Id - from) % every == 0)
            {
                Skin(rig.Skin, bindposes, weights, restVerts, skinned);
                using (var w = new BinaryWriter(File.Create(Path.Combine(outDir, $"mesh_{f.Id:D5}.bin"))))
                {
                    foreach (var c in skinned) { w.Write(c.x); w.Write(c.y); w.Write(c.z); }
                    foreach (var b in rig.Skin.bones) { Vector3 c = b.position; w.Write(c.x); w.Write(c.y); w.Write(c.z); }
                }
                rendered++;
            }
        }
        File.WriteAllText(Path.Combine(outDir, "frames.csv"), sb.ToString());
        shownErrs.Sort();
        float Pc(float q) => shownErrs.Count > 0 ? shownErrs[Mathf.Min(shownErrs.Count - 1, (int)(q * shownErrs.Count))] : -1f;
        gaps.Sort();
        float gapMed = gaps.Count > 0 ? gaps[gaps.Count / 2] : -1f;
        return string.Format(inv, "{0}: {1} anh, {2} giai duoc, {3} khung luoi | doi cach hieu {4}, thu lai {5} | tay hien lech diem model (ban tay) med {6:F3} p90 {7:F3} p98 {8:F3} | rgap med {9:F1} mm",
            Path.GetFileName(recDir), frames.Count, solved, rendered, rig.Solver.Switches, rig.Solver.Reseeds, Pc(0.5f), Pc(0.9f), Pc(0.98f), gapMed);
    }

    /// <summary>Writes the shown pose to the bones like the headset (the Quest hand plays no part: it is given the shown wrist).</summary>
    private static void WritePose(Rig rig, Vector3 p, Quaternion q)
    {
        for (int f = 2; f < 5; f++)
        {
            rig.Fitters[f].SetAngles(0f, rig.Closed.x, rig.Closed.y, rig.Closed.z);
            rig.Fitters[f].Apply(1f);
        }
        rig.Solver.WriteBones(p, q);
    }

    /// <summary>Linear blend skinning on the CPU (what the GPU does at run time; in Edit mode the renderer does not follow the bones).
    /// Output in world coordinates.</summary>
    private static void Skin(SkinnedMeshRenderer smr, Matrix4x4[] bindposes, BoneWeight[] weights, Vector3[] rest, Vector3[] outVerts)
    {
        var bones = smr.bones;
        var m = new Matrix4x4[bones.Length];
        for (int b = 0; b < bones.Length; b++) m[b] = bones[b].localToWorldMatrix * bindposes[b];
        for (int v = 0; v < rest.Length; v++)
        {
            BoneWeight w = weights[v];
            Vector3 p = rest[v];
            outVerts[v] = m[w.boneIndex0].MultiplyPoint3x4(p) * w.weight0 + m[w.boneIndex1].MultiplyPoint3x4(p) * w.weight1 +
                          m[w.boneIndex2].MultiplyPoint3x4(p) * w.weight2 + m[w.boneIndex3].MultiplyPoint3x4(p) * w.weight3;
        }
    }

    /// <summary>Mean distance (in palm lengths on the image, |point 0 - point 5|) between the model's confident points and px.</summary>
    private static float PixelError(Frame f, Vector2[] px)
    {
        float palm = Mathf.Max(Vector2.Distance(f.Px[0], f.Px[5]), 1f), sum = 0f;
        int n = 0;
        for (int i = 0; i < 9; i++)
        {
            if (f.Conf[i] < 0.2f || px[i].x < 0f) continue;
            sum += Vector2.Distance(f.Px[i], px[i]);
            n++;
        }
        return n > 0 ? sum / n / palm : -1f;
    }

    private static Vector2 ToPixel(Vector3 c) =>
        c.z > 1e-3f ? new Vector2(Cx + c.x / c.z * Fx, Cy - c.y / c.z * Fy) : new Vector2(-1f, -1f);

    /// <summary>Prints what the lab needs to know about the scene's glove hand mesh (bones bound? deforms when baked?).</summary>
    public static void Probe()
    {
        string report;
        try
        {
            var rig = BuildRig();
            var sb = new StringBuilder();
            var s = rig.Skin;
            sb.AppendFormat("skin {0} enabled {1} bones {2} verts {3} mats {4}\n", s.name, s.enabled, s.bones.Length, s.sharedMesh.vertexCount,
                string.Join("|", System.Array.ConvertAll(s.sharedMaterials, m => m != null ? m.name + "/" + m.shader.name : "null")));
            for (int i = 0; i < Mathf.Min(s.bones.Length, 26); i++) sb.Append(s.bones[i] != null ? s.bones[i].name : "null").Append(' ');
            sb.AppendFormat("\nwrist {0} lossy {1:F3} root {2} skin pos {3} rot {4}\n", rig.Wrist.name, rig.Wrist.lossyScale.x, s.rootBone != null ? s.rootBone.name : "null",
                s.transform.position, s.transform.rotation.eulerAngles);
            foreach (var r in rig.Receiver.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                sb.AppendFormat("SMR {0} en {1} active {2} bone0 {3} path {4}\n", r.name, r.enabled, r.gameObject.activeInHierarchy,
                    r.bones.Length > 0 && r.bones[0] != null ? PathOf(r.bones[0]) : "-", PathOf(r.transform));
            sb.AppendFormat("wrist path {0}\nindexProx path {1}\n", PathOf(rig.Wrist), PathOf(rig.Fitters[1].RootBone));
            var hv = rig.Receiver.GetComponent<Oculus.Interaction.HandVisual>();
            if (hv != null)
            {
                var hso = new SerializedObject(hv);
                var it = hso.GetIterator();
                while (it.NextVisible(true))
                    if (it.propertyType == SerializedPropertyType.ObjectReference && it.objectReferenceValue != null)
                        sb.AppendFormat("  HV {0} = {1}\n", it.propertyPath, it.objectReferenceValue.name);
            }
            var mesh = new Mesh();
            s.BakeMesh(mesh, true);
            Vector3 a = mesh.bounds.center;
            Vector3 tipBefore = rig.Fitters[1].RootBone.position;
            rig.Fitters[1].SetAngles(0f, 80f, 80f, 60f);
            rig.Fitters[1].Apply(1f);
            s.BakeMesh(mesh, true);
            sb.AppendFormat("bake bounds before {0} after curl {1} (changed = mesh follows bones)\n", a, mesh.bounds.center);
            report = sb.ToString();
        }
        catch (System.Exception e) { report = "LOI: " + e; }
        Debug.Log("[GloveReplayLab] " + report);
        if (Application.isBatchMode) EditorApplication.Exit(0);
    }

    private static string PathOf(Transform t)
    {
        string p = t.name;
        for (Transform q = t.parent; q != null; q = q.parent) p = q.name + "/" + p;
        return p;
    }

    // --- Minimal JSON reader (objects, arrays, numbers, strings, true/false/null) for keypoints.jsonl ---
    private static Dictionary<string, object> MiniJson(string s)
    {
        int i = 0;
        return (Dictionary<string, object>)ParseValue(s, ref i);
    }

    private static object ParseValue(string s, ref int i)
    {
        while (char.IsWhiteSpace(s[i])) i++;
        char c = s[i];
        if (c == '{')
        {
            var d = new Dictionary<string, object>();
            i++;
            while (true)
            {
                while (char.IsWhiteSpace(s[i])) i++;
                if (s[i] == '}') { i++; return d; }
                string key = (string)ParseValue(s, ref i);
                while (s[i] != ':') i++;
                i++;
                d[key] = ParseValue(s, ref i);
                while (char.IsWhiteSpace(s[i])) i++;
                if (s[i] == ',') i++;
            }
        }
        if (c == '[')
        {
            var l = new List<object>();
            i++;
            while (true)
            {
                while (char.IsWhiteSpace(s[i])) i++;
                if (s[i] == ']') { i++; return l; }
                l.Add(ParseValue(s, ref i));
                while (char.IsWhiteSpace(s[i])) i++;
                if (s[i] == ',') i++;
            }
        }
        if (c == '"')
        {
            int j = s.IndexOf('"', i + 1);
            string v = s.Substring(i + 1, j - i - 1);
            i = j + 1;
            return v;
        }
        if (s.Substring(i).StartsWith("true")) { i += 4; return true; }
        if (s.Substring(i).StartsWith("false")) { i += 5; return false; }
        if (s.Substring(i).StartsWith("null")) { i += 4; return null; }
        int k = i;
        while (k < s.Length && "+-0123456789.eE".IndexOf(s[k]) >= 0) k++;
        double num = double.Parse(s.Substring(i, k - i), CultureInfo.InvariantCulture);
        i = k;
        return num;
    }
}
