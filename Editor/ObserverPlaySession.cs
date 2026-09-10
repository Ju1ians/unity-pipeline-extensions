using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;

namespace UnityPipeline.Extensions.Editor
{
    // SessionState survives both Play entry and exit domain reloads. The Editor,
    // not the HTTP client, owns stopping this session after capture or failure.
    [InitializeOnLoad]
    public static class ObserverPlaySession
    {
        const string Key = "UnityPipeline.ObserverPlaySession.v2";
        const string ImageKey = Key + ".image";
        static ObserverPlaySession() { EditorApplication.update += Tick; }
        static JObject Load()
        {
            var raw = SessionState.GetString(Key, "");
            return string.IsNullOrEmpty(raw) ? null : JObject.Parse(raw);
        }
        static void Save(JObject job) => SessionState.SetString(Key, job.ToString(Formatting.None));
        internal static bool Active => Load() is JObject j && (string)j["phase"] != "completed" && (string)j["phase"] != "failed";
        internal static object Begin(string views, int width, int height)
        {
            if (Active) return new { success = false, error_code = "OBSERVER_BUSY", error = "An automatic observation session is already running." };
            ObserverCaptureCommands.Validate(views, width, height);
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Wait for the current Editor transition or compilation to finish.");
            var scenes = ObserverCaptureCommands.Scenes(false);
            foreach (var scene in scenes)
                if ((bool)scene["dirty"] || string.IsNullOrEmpty((string)scene["path"]))
                    throw new InvalidOperationException("Save or discard unsaved scene edits before automatic capture; the capture operation never saves them.");
            if (EditorSettings.enterPlayModeOptionsEnabled && (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableSceneReload) != 0)
                throw new InvalidOperationException("Automatic restoration requires Scene Reload enabled in Enter Play Mode settings.");
            var job = new JObject { ["capture_id"] = Guid.NewGuid().ToString("N"), ["phase"] = "entering", ["views"] = views,
                ["width"] = width, ["height"] = height, ["scenes"] = scenes, ["paused"] = EditorApplication.isPaused,
                ["deadline"] = DateTime.UtcNow.AddSeconds(120).Ticks };
            SessionState.EraseString(ImageKey); Save(job);
            EditorApplication.isPaused = false;
            EditorApplication.EnterPlaymode();
            return Status((string)job["capture_id"]);
        }
        [CliCommand("capture_observer_result", "Read the result of an automatic observer capture. Read-only polling; cannot start/stop Play or change a session.", Tags = new[] { "capture", "extensions" })]
        public static object Status([CliArg("capture_id", "ID returned by capture_observer.")] string captureId)
        {
            var job = Load();
            if (job == null || captureId != (string)job["capture_id"])
                return new { success = false, error_code = "OBSERVER_NOT_FOUND", error = "Capture expired or belongs to another Editor session." };
            if ((string)job["phase"] == "completed")
                return new { success = true, image_base64 = SessionState.GetString(ImageKey, ""), observer_capture = job["metadata"] };
            if ((string)job["phase"] == "failed")
                return new { success = false, error_code = "OBSERVER_SESSION_FAILED", error = (string)job["error"] };
            return new { success = true, observer_job = new { capture_id = captureId, status = (string)job["phase"] } };
        }
        static void Tick()
        {
            var job = Load(); if (job == null) return;
            var phase = (string)job["phase"];
            if (phase == "completed" || phase == "failed")
            {
                if (DateTime.UtcNow.Ticks > (long)job["deadline"] + TimeSpan.FromMinutes(5).Ticks) { SessionState.EraseString(Key); SessionState.EraseString(ImageKey); }
                return;
            }
            try
            {
                if (phase == "exiting" && !EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    EditorApplication.isPaused = (bool)job["paused"];
                    if (!JToken.DeepEquals(job["scenes"], ObserverCaptureCommands.Scenes(false))) job["error"] = "Original Editor scenes were not restored. No scenes were saved or overwritten by capture.";
                    job["phase"] = job["error"] == null ? "completed" : "failed";
                    if (job["metadata"] is JObject metadata) { metadata["automatic_play_session"] = true; metadata["editor_state_restored"] = job["error"] == null; }
                    if (job["error"] != null) SessionState.EraseString(ImageKey);
                    Save(job); return;
                }
                if (DateTime.UtcNow.Ticks > (long)job["deadline"]) throw new TimeoutException("Automatic capture timed out in " + phase + "; stopping its Play session.");
                if (phase == "entering" && EditorApplication.isPlaying && Application.isPlaying)
                { job["phase"] = "warming"; job["frame"] = Time.frameCount; Save(job); return; }
                if (phase != "warming") return;
                if (Time.frameCount < (int)job["frame"] + 2) { EditorApplication.QueuePlayerLoopUpdate(); return; }
                var result = JObject.FromObject(ObserverCaptureCommands.CaptureCore((string)job["views"], (int)job["width"], (int)job["height"]));
                var image = (string)result["image_base64"];
                if (image.Length > 12 * 1024 * 1024) throw new InvalidOperationException("Observer result exceeds retention limit.");
                SessionState.SetString(ImageKey, image); job["metadata"] = result["observer_capture"];
                job["metadata"]["runtime_session_id"] = (string)job["capture_id"];
                job["phase"] = "exiting"; Save(job); EditorApplication.ExitPlaymode();
            }
            catch (Exception error)
            {
                job["error"] = error.Message; job["phase"] = "exiting"; Save(job);
                if (EditorApplication.isPlayingOrWillChangePlaymode) EditorApplication.isPlaying = false;
            }
        }
    }
}
