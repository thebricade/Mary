using UnityEditor;
using UnityEngine;

public static class CreateFPCPrefab
{
    const string PrefabPath = "Assets/Prefab/FPC.prefab";

    // Builds the prefab automatically the first time the project loads without one.
    [InitializeOnLoadMethod]
    static void CreateIfMissing()
    {
        EditorApplication.delayCall += () =>
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
                Create();
        };
    }

    [MenuItem("Mary/Create FPC Prefab")]
    static void Create()
    {
        var root = new GameObject("FPC");
        root.tag = "Player";

        var cc = root.AddComponent<CharacterController>();
        cc.height = 1.8f;
        cc.radius = 0.35f;
        cc.center = new Vector3(0f, 0.9f, 0f);
        cc.stepOffset = 0.35f;

        var holder = new GameObject("CameraHolder");
        holder.transform.SetParent(root.transform, false);
        holder.transform.localPosition = new Vector3(0f, 1.6f, 0f);

        var camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        camGo.transform.SetParent(holder.transform, false);
        camGo.AddComponent<Camera>().nearClipPlane = 0.05f;
        camGo.AddComponent<AudioListener>();

        var fpc = root.AddComponent<FirstPersonController>();
        fpc.SetCameraHolder(holder.transform);

        if (!AssetDatabase.IsValidFolder("Assets/Prefab"))
            AssetDatabase.CreateFolder("Assets", "Prefab");
        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets();
        Debug.Log("Created " + PrefabPath);
    }
}
