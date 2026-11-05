using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// One-off scene edits done by code (safer than editing the scene file by hand). Run from the menu or in batch mode:
///   Unity -batchmode -quit -projectPath . -executeMethod GloveSceneSetup.AddSoftCube
/// </summary>
public static class GloveSceneSetup
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string SoftMaterialPath = "Assets/Materials/SoftCube.mat";

    /// <summary>Adds "SoftCube" next to the PhysicsCube on the tray: a copy of the cube (same grab / throw / haptics set-up) that is
    /// soft (PhysicsPinchGrabbable soft depth 1.5 cm, SoftBodyDent dents its mesh, softer HapticMaterial). Running it again only
    /// re-applies the settings.</summary>
    [MenuItem("Glove/Add soft cube to the tray")]
    public static void AddSoftCube()
    {
        string report;
        try
        {
            var scene = EditorSceneManager.OpenScene(ScenePath);
            GameObject cube = Find("PhysicsCube"), cubeHome = Find("PhysicsCube_Home");
            if (cube == null || cubeHome == null) throw new System.Exception("PhysicsCube / PhysicsCube_Home not found");
            const float softSize = 0.04f;               // 4 cm (the cube is 3.5 cm)
            float trayTop = cubeHome.transform.localPosition.y - 0.5f * cube.transform.localScale.x; // cube resting height minus half size

            // Cube to the left half of the tray, the soft cube to the right half
            Vector3 hp = cubeHome.transform.localPosition;
            cubeHome.transform.localPosition = new Vector3(-0.05f, hp.y, hp.z);

            GameObject softHome = Find("SoftCube_Home");
            if (softHome == null)
            {
                softHome = new GameObject("SoftCube_Home");
                softHome.transform.SetParent(cubeHome.transform.parent, false);
            }
            softHome.transform.localPosition = new Vector3(0.05f, trayTop + 0.5f * softSize, hp.z);
            softHome.transform.localRotation = cubeHome.transform.localRotation;

            GameObject soft = Find("SoftCube");
            if (soft == null)
            {
                soft = Object.Instantiate(cube, cube.transform.parent);
                soft.name = "SoftCube";
            }
            soft.transform.localScale = Vector3.one * softSize;
            soft.transform.SetPositionAndRotation(softHome.transform.position, softHome.transform.rotation);

            var grab = soft.GetComponent<PhysicsPinchGrabbable>();
            var so = new SerializedObject(grab);
            so.FindProperty("_home").objectReferenceValue = softHome.transform;
            so.FindProperty("_softDepth").floatValue = 0.015f;
            so.ApplyModifiedPropertiesWithoutUndo();

            var haptic = soft.GetComponent<HapticMaterial>();
            if (haptic != null)
            {
                var hs = new SerializedObject(haptic);
                hs.FindProperty("_pressureAtMaxIndent").floatValue = 0.35f; // a soft spring up to the soft depth, then bottoming out
                hs.ApplyModifiedPropertiesWithoutUndo();
            }
            if (soft.GetComponent<SoftBodyDent>() == null) soft.AddComponent<SoftBodyDent>();

            // Its own colour
            var renderer = soft.GetComponent<MeshRenderer>();
            var mat = AssetDatabase.LoadAssetAtPath<Material>(SoftMaterialPath);
            if (mat == null)
            {
                mat = new Material(renderer.sharedMaterial) { name = "SoftCube" };
                AssetDatabase.CreateAsset(mat, SoftMaterialPath);
            }
            var pink = new Color(1f, 0.55f, 0.7f, 1f);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", pink);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", pink);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.15f);
            EditorUtility.SetDirty(mat);
            renderer.sharedMaterial = mat;

            // Put it back home with the cube on every re-place
            var placer = Object.FindAnyObjectByType<PlaceInFrontOfHead>(FindObjectsInactive.Include);
            if (placer != null)
            {
                var ps = new SerializedObject(placer);
                var list = ps.FindProperty("_snapBodies");
                bool has = false;
                for (int i = 0; i < list.arraySize; i++) has |= list.GetArrayElementAtIndex(i).objectReferenceValue == grab;
                if (!has)
                {
                    list.arraySize++;
                    list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = grab;
                }
                ps.ApplyModifiedPropertiesWithoutUndo();
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            report = $"SoftCube ok: home {softHome.transform.localPosition}, cube home {cubeHome.transform.localPosition}, placer {(placer != null)}";
        }
        catch (System.Exception e) { report = "LOI: " + e; }
        Debug.Log("[GloveSceneSetup] " + report);
        if (Application.isBatchMode) EditorApplication.Exit(report.StartsWith("LOI") ? 1 : 0);
    }

    private static GameObject Find(string name)
    {
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t.name == name) return t.gameObject;
        return null;
    }
}
