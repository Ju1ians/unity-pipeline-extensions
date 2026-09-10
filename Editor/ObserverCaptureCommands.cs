using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("UnityPipeline.Extensions.Tests.Editor")]

namespace UnityPipeline.Extensions.Editor
{
    public static class ObserverCaptureCommands
    {
        internal const string Prefix = "__PipelineObserver_";
        static bool busy;
        static string runtimeSession;
        static ObserverCaptureCommands()
        {
            EditorApplication.playModeStateChanged += state => {
                if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.EnteredEditMode) runtimeSession = null;
            };
        }

        [CliCommand("capture_observer", "Observe an existing Play session using disposable cameras. No Play controls, saving, or real-camera changes. 1-4 views render synchronously into a PNG contact sheet (same simulation frame). Requires GPU; SRP must support StandardRequest. Excludes overlay UI, camera scripts and camera stacks. Rendering may invoke project render callbacks; this is not a sandbox.", Tags = new[] { "capture", "extensions" })]
        public static object Capture(
            [CliArg("views_json", "JSON array of 1-4 objects: position:[x,y,z] plus rotation:[x,y,z] OR look_at:[x,y,z]; alternatively target:[x,y,z], distance, yaw, pitch. Optional fov (10-150). World coordinates; orbit angles in degrees. No scene-object lookup.")] string viewsJson,
            [CliArg("width", "Each tile width, 64-1024 pixels.")] int width = 640,
            [CliArg("height", "Each tile height, 64-1024 pixels.")] int height = 360,
            [CliArg("review_task_id", "Optional Gateway task for workspace/submission evidence. Not interpreted by Unity.")] string reviewTaskId = "")
        {
            if (!EditorApplication.isPlaying || !Application.isPlaying || EditorApplication.isCompiling || EditorApplication.isUpdating)
                return Error("OBSERVER_RUNTIME_REQUIRED", "An authorized user/Builder/Reviewer must establish a stable Play session. Observation never starts or stops Play Mode.");
            if (busy) return Error("OBSERVER_BUSY", "Another observer capture is active.");
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                return Error("OBSERVER_GPU_UNAVAILABLE", "A graphics device is required.");
            busy = true;
            if (runtimeSession == null) runtimeSession = Guid.NewGuid().ToString("N");
            try { return CaptureCore(viewsJson, width, height); }
            catch (Exception e) { return Error("OBSERVER_CAPTURE_FAILED", e.Message); }
            finally { busy = false; }
        }

        static object Error(string code, string message) => new { success = false, error_code = code, error = message };
        internal static JArray Validate(string json, int width, int height)
        {
            if (width < 64 || width > 1024 || height < 64 || height > 1024)
                throw new ArgumentException("Tile dimensions must be 64-1024.");
            if (json == null || json.Length > 8192) throw new ArgumentException("views_json must be at most 8192 characters.");
            var views = JArray.Parse(json);
            if (views.Count < 1 || views.Count > 4) throw new ArgumentException("Provide 1-4 viewpoints.");
            foreach (var token in views)
            {
                var view = token as JObject ?? throw new ArgumentException("Each view must be an object.");
                foreach (var p in view.Properties())
                    if (!new[] { "position", "rotation", "look_at", "target", "distance", "yaw", "pitch", "fov" }.Contains(p.Name))
                        throw new ArgumentException("Unknown view field: " + p.Name);
                Pose(view, out _, out _);
                Number(view["fov"] ?? new JValue(60), 10, 150);
            }
            return views;
        }
        static float Number(JToken value, float min, float max)
        {
            if (value == null || (value.Type != JTokenType.Float && value.Type != JTokenType.Integer)) throw new ArgumentException("Expected a numeric value.");
            var n = value.Value<float>();
            if (float.IsNaN(n) || float.IsInfinity(n) || n < min || n > max) throw new ArgumentException("Numeric value outside supported range.");
            return n;
        }
        static Vector3 Vector(JToken value)
        {
            var a = value as JArray;
            if (a == null || a.Count != 3) throw new ArgumentException("Expected [x,y,z].");
            return new Vector3(Number(a[0], -1000000, 1000000), Number(a[1], -1000000, 1000000), Number(a[2], -1000000, 1000000));
        }
        internal static void Pose(JObject view, out Vector3 position, out Quaternion rotation)
        {
            if (view["target"] != null)
            {
                if (view["position"] != null || view["rotation"] != null || view["look_at"] != null) throw new ArgumentException("Orbit and explicit pose are mutually exclusive.");
                var target = Vector(view["target"]);
                var distance = Number(view["distance"], .01f, 100000);
                rotation = Quaternion.Euler(Number(view["pitch"] ?? new JValue(0), -89, 89), Number(view["yaw"] ?? new JValue(0), -3600, 3600), 0);
                position = target - rotation * Vector3.forward * distance;
            }
            else
            {
                if (view["distance"] != null || view["yaw"] != null || view["pitch"] != null) throw new ArgumentException("Orbit requires target.");
                position = Vector(view["position"]);
                if ((view["rotation"] == null) == (view["look_at"] == null)) throw new ArgumentException("Specify rotation OR look_at.");
                if (view["rotation"] != null) rotation = Quaternion.Euler(Vector(view["rotation"]));
                else
                {
                    var direction = Vector(view["look_at"]) - position;
                    if (direction.sqrMagnitude < 0.000001f) throw new ArgumentException("look_at must differ from position.");
                    rotation = Quaternion.LookRotation(direction, Mathf.Abs(Vector3.Dot(direction.normalized, Vector3.up)) > .999f ? Vector3.forward : Vector3.up);
                }
            }
        }
        static JArray XYZ(Vector3 v) => new JArray(v.x, v.y, v.z);
        static JArray Scenes()
        {
            var result = new JArray();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                result.Add(new JObject { ["handle"] = TestSessionManager.GetSceneHandleRawData(s).ToString(), ["path"] = s.path, ["name"] = s.name, ["dirty"] = s.isDirty, ["loaded"] = s.isLoaded, ["active"] = s == SceneManager.GetActiveScene() });
            }
            return result;
        }
        internal static object CaptureCore(string json, int width, int height, Action<Camera, RenderTexture> renderOverride = null)
        {
            var views = Validate(json, width, height);
            var scenes = Scenes(); var frame = Time.frameCount; var paused = EditorApplication.isPaused; var playing = EditorApplication.isPlaying;
            var activeRT = RenderTexture.active;
            var metadata = new JArray(); var columns = Math.Min(2, views.Count); var rows = (views.Count + columns - 1) / columns;
            Texture2D sheet = null; byte[] png = null;
            try
            {
                sheet = new Texture2D(width * columns, height * rows, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
                sheet.SetPixels32(new Color32[sheet.width * sheet.height]);
                for (var i = 0; i < views.Count; i++)
                {
                    GameObject observer = null; RenderTexture target = null;
                    try
                    {
                        observer = new GameObject(Prefix + Guid.NewGuid().ToString("N")) { hideFlags = HideFlags.HideAndDontSave };
                        var camera = observer.AddComponent<Camera>(); camera.enabled = false;
                        Pose((JObject)views[i], out var position, out var rotation);
                        camera.transform.SetPositionAndRotation(position, rotation);
                        camera.fieldOfView = Number(views[i]["fov"] ?? new JValue(60), 10, 150);
                        camera.aspect = (float)width / height; camera.nearClipPlane = .01f; camera.farClipPlane = 10000;
                        camera.clearFlags = CameraClearFlags.Skybox; camera.allowHDR = false; camera.allowMSAA = false;
                        target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { hideFlags = HideFlags.HideAndDontSave };
                        if (!target.Create()) throw new InvalidOperationException("Cannot allocate observer render target.");
                        camera.targetTexture = target;
                        if (renderOverride != null) renderOverride(camera, target);
                        else if (GraphicsSettings.currentRenderPipeline == null) camera.Render();
                        else
                        {
                            var request = new RenderPipeline.StandardRequest { destination = target };
                            if (!RenderPipeline.SupportsRenderRequest(camera, request)) throw new NotSupportedException("Active render pipeline does not support StandardRequest observer rendering.");
                            RenderPipeline.SubmitRenderRequest(camera, request);
                        }
                        RenderTexture.active = target;
                        var x = i % columns * width; var y = (rows - 1 - i / columns) * height;
                        sheet.ReadPixels(new Rect(0, 0, width, height), x, y, false);
                        metadata.Add(new JObject { ["index"] = i, ["pixel_rect_bottom_left"] = new JArray(x,y,width,height), ["position"] = XYZ(position), ["rotation"] = XYZ(rotation.eulerAngles), ["fov"] = camera.fieldOfView });
                    }
                    finally
                    {
                        RenderTexture.active = activeRT;
                        try { if (observer != null) UnityEngine.Object.DestroyImmediate(observer); }
                        finally { if (target != null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); } }
                    }
                }
                sheet.Apply(false); png = sheet.EncodeToPNG();
            }
            finally { if (sheet != null) UnityEngine.Object.DestroyImmediate(sheet); RenderTexture.active = activeRT; }
            if (!JToken.DeepEquals(scenes, Scenes()) || frame != Time.frameCount || playing != EditorApplication.isPlaying || paused != EditorApplication.isPaused)
                throw new InvalidOperationException("OBSERVER_STATE_CHANGED: scene/frame/play state changed during rendering. Evidence rejected; no attempt to overwrite project state.");
            return new { success = true, image_base64 = Convert.ToBase64String(png), observer_capture = new {
                schema = "unity-pipeline-observer/v1", runtime_session_id = runtimeSession, capture_set_id = Guid.NewGuid().ToString("N"), frame, time = Time.timeAsDouble,
                playing, paused, scenes, viewpoints = metadata, cleanup_complete = true,
                render_pipeline = GraphicsSettings.currentRenderPipeline?.GetType().FullName ?? "BuiltIn",
                limitations = "Independent perspective cameras: no real-camera scripts, stacks, overlay UI or temporal history. Render callbacks may have side effects; unchanged scene flags do not prove arbitrary script state unchanged."
            }};
        }
    }
}
