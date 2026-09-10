using System;
using System.Collections;
using System.Linq;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityPipeline.Extensions.Editor;

namespace UnityPipeline.Extensions.Tests.Editor
{
    public class ObserverCaptureTests
    {
        const string Views = "[{\"position\":[0,0,-5],\"look_at\":[0,0,0]},{\"target\":[0,0,0],\"distance\":5,\"yaw\":90}]";
        static int Observers() => Resources.FindObjectsOfTypeAll<Camera>().Count(c => c.name.StartsWith(ObserverCaptureCommands.Prefix));
        [Test] public void RejectsInvalidAndAmbiguousViews()
        {
            foreach (var json in new[] { "[]", "[{}]", "[{\"position\":[0,0,0],\"look_at\":[0,0,0]}]", "[{\"target\":[0,0,0],\"distance\":-1}]", "[{\"save_path\":\"Assets/x\"}]" })
                Assert.Throws<ArgumentException>(() => ObserverCaptureCommands.Validate(json, 64, 64));
            Assert.Throws<ArgumentException>(() => ObserverCaptureCommands.Validate(Views, 2048, 64));
            Assert.AreEqual(2, ObserverCaptureCommands.Validate(Views, 64, 64).Count);
        }
        [Test] public void UnsavedRunnerSceneIsNotSavedToEnterPlay()
        {
            Assert.IsFalse(EditorApplication.isPlaying);
            var r = JObject.FromObject(ObserverCaptureCommands.Capture(Views));
            Assert.AreEqual("OBSERVER_START_FAILED", (string)r["error_code"]);
            Assert.IsFalse(EditorApplication.isPlaying);
            Assert.AreEqual(0, Observers());
        }
        [Test] public void RenderingFailureDestroysCameraAndRestoresTarget()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("GPU required");
            var before = RenderTexture.active;
            Assert.Throws<InvalidOperationException>(() => ObserverCaptureCommands.CaptureCore(Views, 64, 64, (c, rt) => throw new InvalidOperationException("Injected render failure")));
            Assert.AreEqual(0, Observers()); Assert.AreEqual(before, RenderTexture.active);
        }
        [UnityTest] public IEnumerator PlayCapturePreservesRealCameraAndSameFrame()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("GPU required");
            yield return new EnterPlayMode();
            var real = new GameObject("RealCamera").AddComponent<Camera>();
            real.transform.position = new Vector3(10,20,30); real.fieldOfView = 47; real.enabled = false;
            var original = EditorJsonUtility.ToJson(real); var frame = Time.frameCount; var paused = EditorApplication.isPaused;
            var assetFiles = Directory.GetFiles(Application.dataPath, "*", SearchOption.AllDirectories).OrderBy(p => p).Select(p => p + ":" + File.GetLastWriteTimeUtc(p).Ticks).ToArray();
            try
            {
                var result = JObject.FromObject(ObserverCaptureCommands.Capture(Views, 64, 64));
                Assert.IsTrue((bool)result["success"], result.ToString());
                Assert.AreEqual(2, ((JArray)result["observer_capture"]["viewpoints"]).Count);
                Assert.AreEqual(frame, Time.frameCount); Assert.AreEqual(paused, EditorApplication.isPaused);
                Assert.AreEqual(original, EditorJsonUtility.ToJson(real));
                Assert.AreEqual(new Vector3(10,20,30), real.transform.position);
                Assert.AreEqual(0, Observers());
                Assert.Greater(Convert.FromBase64String((string)result["image_base64"]).Length, 50);
                EditorApplication.isPaused = true;
                var again = JObject.FromObject(ObserverCaptureCommands.Capture(Views,64,64));
                Assert.IsTrue((bool)again["success"]);
                Assert.IsTrue(EditorApplication.isPaused);
                Assert.AreEqual((string)result["observer_capture"]["runtime_session_id"], (string)again["observer_capture"]["runtime_session_id"]);
                Assert.AreEqual(0, Observers());
                CollectionAssert.AreEqual(assetFiles, Directory.GetFiles(Application.dataPath, "*", SearchOption.AllDirectories).OrderBy(p => p).Select(p => p + ":" + File.GetLastWriteTimeUtc(p).Ticks).ToArray());
            }
            finally { EditorApplication.isPaused = paused; UnityEngine.Object.DestroyImmediate(real.gameObject); }
            yield return new ExitPlayMode();
            Assert.AreEqual(0, Observers());
        }
        [Test] public void SceneChangeRejectsEvidenceAndCleansObserver()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("GPU required");
            var original = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            UnityEngine.SceneManagement.Scene added = default;
            try
            {
                Assert.Throws<InvalidOperationException>(() => ObserverCaptureCommands.CaptureCore("[{\"position\":[0,0,-5],\"look_at\":[0,0,0]}]",64,64,(c,rt) => {
                    added = UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Additive);
                    RenderTexture.active = rt; GL.Clear(true, true, Color.black);
                }));
                Assert.AreEqual(0, Observers());
            }
            finally
            {
                UnityEngine.SceneManagement.SceneManager.SetActiveScene(original);
                if (added.IsValid()) UnityEditor.SceneManagement.EditorSceneManager.CloseScene(added, true);
            }
        }
        [UnityTest] public IEnumerator UrpObserverUsesSupportedRenderRequest()
        {
            var type = Type.GetType("UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset, Unity.RenderPipelines.Universal.Runtime");
            if (type == null || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Optional URP/GPU matrix case");
            yield return new EnterPlayMode();
            type = Type.GetType("UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset, Unity.RenderPipelines.Universal.Runtime");
            var original = GraphicsSettings.defaultRenderPipeline;
            var quality = QualitySettings.renderPipeline;
            var asset = (RenderPipelineAsset)type.GetMethod("Create", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { null });
            try
            {
                QualitySettings.renderPipeline = null;
                GraphicsSettings.defaultRenderPipeline = asset;
                yield return null;
                var result = JObject.FromObject(ObserverCaptureCommands.Capture(Views,64,64));
                Assert.IsTrue((bool)result["success"], result.ToString());
                Assert.That((string)result["observer_capture"]["render_pipeline"], Does.Contain("Universal"));
                Assert.AreEqual(0, Observers());
            }
            finally
            {
                GraphicsSettings.defaultRenderPipeline = original;
                QualitySettings.renderPipeline = quality;
                UnityEngine.Object.DestroyImmediate(asset);
            }
            yield return new ExitPlayMode();
        }
    }
}
