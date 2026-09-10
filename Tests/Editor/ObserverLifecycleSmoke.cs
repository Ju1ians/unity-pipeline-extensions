using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityPipeline.Extensions.Editor;

[InitializeOnLoad]
public static class ObserverLifecycleSmoke
{
    const string Key = "ObserverSmoke.Id";
    static ObserverLifecycleSmoke() { EditorApplication.update += Poll; }
    public static void Run()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var camera = new GameObject("RealCamera").AddComponent<Camera>();
        camera.transform.position = new Vector3(0,0,-5);
        GameObject.CreatePrimitive(PrimitiveType.Cube);
        EditorSceneManager.SaveScene(scene, "Assets/ObserverSmoke.unity");
        var result = JObject.FromObject(ObserverCaptureCommands.Capture("[{\"position\":[0,0,-5],\"look_at\":[0,0,0]},{\"target\":[0,0,0],\"distance\":5,\"yaw\":90}]",128,128));
        if (result["observer_job"] == null) { Finish(result, 1); return; }
        SessionState.SetString(Key, (string)result["observer_job"]["capture_id"]);
    }
    static void Poll()
    {
        var id = SessionState.GetString(Key, ""); if (id == "") return;
        var result = JObject.FromObject(ObserverPlaySession.Status(id));
        if (result["observer_job"] != null) return;
        result.Remove("image_base64");
        result["stopped"] = !EditorApplication.isPlaying;
        result["dirty"] = UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty;
        Finish(result, (bool)result["success"] && !EditorApplication.isPlaying && !(bool)result["dirty"] ? 0 : 1);
    }
    static void Finish(JObject result, int exit)
    {
        SessionState.EraseString(Key);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "observer-lifecycle-smoke.json"), result.ToString());
        EditorApplication.Exit(exit);
    }
}
