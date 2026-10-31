using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds the Android APK (menu Tools/Glove/Build APK, or batch mode):
///   Unity.exe -batchmode -quit -projectPath D:/vr/grasping_releasing -executeMethod GloveBuild.Build -logFile build.log
/// Writes grasping_releasing.apk to the project folder (install with adb install -r).
/// </summary>
public static class GloveBuild
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string ApkPath = "grasping_releasing.apk";

    [MenuItem("Tools/Glove/Build APK")]
    public static void Build()
    {
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = ApkPath,
            target = BuildTarget.Android,
            options = BuildOptions.None
        });
        Debug.Log($"[GloveBuild] {report.summary.result}, {report.summary.totalErrors} errors, {report.summary.totalSize / 1e6:F1} MB -> {ApkPath}");
        if (Application.isBatchMode && report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            EditorApplication.Exit(1);
    }
}
