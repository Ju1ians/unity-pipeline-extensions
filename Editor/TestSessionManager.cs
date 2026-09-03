using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityPipeline.Extensions.Editor
{
    internal static class TestSessionManager
    {
        internal const string ScratchParent = "Assets/__PipelineTemp";
        private const string SessionStateKey = "UnityPipeline.Extensions.ActiveTestSession.v1";
        private const string BootstrapDiagnosticsStateKey = "UnityPipeline.Extensions.LastBootstrapDiagnostics.v1";


        [Serializable]
        internal sealed class BootstrapSceneState
        {
            public string name;
            public string path;
            public ulong handle;
            public bool isLoaded;
            public bool isDirty;
            public bool isActive;
        }

        [Serializable]
        internal sealed class BootstrapDiagnostics
        {
            public string sessionId;
            public string stage;
            public string expectedScenePath;
            public string scratchRoot;
            public bool scratchDirectoryExists;
            public bool sceneCreateSucceeded;
            public bool sceneAssetExists;
            public List<BootstrapSceneState> loadedScenesAfterCreate = new List<BootstrapSceneState>();
            public bool matchingLoadedSceneFound;
            public BootstrapSceneState activeSceneBeforeActivation;
            public bool setActiveSceneAttempted;
            public bool setActiveSceneSucceeded;
            public BootstrapSceneState activeSceneAfterActivation;
            public string error;
        }

        [Serializable]
        internal sealed class SceneSnapshot
        {
            public ulong handle;
            public string name;
            public string path;
            public bool wasDirty;
            public bool wasActive;
        }

        [Serializable]
        internal sealed class SessionRecord
        {
            public string sessionId;
            public string temporarySceneName;
            public ulong temporarySceneHandle;
            public string temporaryScenePath;
            public string scratchRoot;
            public bool scratchParentExisted;
            public List<SceneSnapshot> originalScenes = new List<SceneSnapshot>();
        }

        internal sealed class CleanupReport
        {
            public string sessionId;
            public bool temporarySceneClosed;
            public bool scratchAssetsDeleted;
            public bool originalActiveSceneRestored;
            public bool preexistingSceneDirtyStatePreserved;
            public List<string> dirtyStateChanges = new List<string>();
            public List<string> missingOriginalScenes = new List<string>();
            public List<string> errors = new List<string>();
            public bool cleanupComplete;
        }

        internal static bool TryGetActive(out SessionRecord record)
        {
            record = null;
            var json = SessionState.GetString(SessionStateKey, string.Empty);
            if (string.IsNullOrEmpty(json))
                return false;

            try
            {
                record = JsonUtility.FromJson<SessionRecord>(json);
                return record != null && !string.IsNullOrEmpty(record.sessionId);
            }
            catch
            {
                return false;
            }
        }

        internal static object GetStatus()
        {
            var lastBootstrap = GetLastBootstrapDiagnostics();

            if (!TryGetActive(out var record))
                return new { active = false, lastBootstrap };

            var scene = FindTemporaryScene(record);
            return new
            {
                active = true,
                sessionId = record.sessionId,
                temporaryScene = new
                {
                    name = record.temporarySceneName,
                    handle = record.temporarySceneHandle,
                    path = record.temporaryScenePath,
                    isOpen = scene.IsValid() && scene.isLoaded,
                    isDirty = scene.IsValid() && scene.isLoaded && scene.isDirty
                },
                scratchRoot = record.scratchRoot,
                scratchExists = AssetDatabase.IsValidFolder(record.scratchRoot),
                lastBootstrap
            };
        }

        internal static async Task<object> BeginAsync()
        {
            GuardEditorReady("begin_test_session");

            if (TryGetActive(out var existing))
            {
                var existingScene = FindTemporaryScene(existing);
                var scratchExists = AssetDatabase.IsValidFolder(existing.scratchRoot);
                if (existingScene.IsValid() || scratchExists)
                    throw new InvalidOperationException(
                        $"A Pipeline test session is already active ('{existing.sessionId}'). End it before starting another.");

                // Stale SessionState from an interrupted/domain-reloaded cleanup with no resources left.
                SessionState.EraseString(SessionStateKey);
            }

            var id = Guid.NewGuid().ToString("N").Substring(0, 12);
            var record = new SessionRecord
            {
                sessionId = id,
                temporarySceneName = "__PipelineTemp_" + id,
                scratchRoot = ScratchParent + "/" + id,
                temporaryScenePath = ScratchParent + "/" + id + "/TemporaryScene.unity",
                scratchParentExisted = AssetDatabase.IsValidFolder(ScratchParent)
            };

            var diagnostics = new BootstrapDiagnostics
            {
                sessionId = id,
                stage = "snapshot_original_scenes",
                expectedScenePath = record.temporaryScenePath,
                scratchRoot = record.scratchRoot
            };
            SaveBootstrapDiagnostics(diagnostics);

            var activeBefore = SceneManager.GetActiveScene();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                record.originalScenes.Add(new SceneSnapshot
                {
                    handle = GetSceneHandleRawData(scene),
                    name = scene.name,
                    path = scene.path,
                    wasDirty = scene.isDirty,
                    wasActive = scene.handle == activeBefore.handle
                });
            }

            Scene tempScene = default;
            try
            {
                diagnostics.stage = "ensure_scratch_directory";
                EnsureFolder(ScratchParent);
                EnsureFolder(record.scratchRoot);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                diagnostics.scratchDirectoryExists = AssetDatabase.IsValidFolder(record.scratchRoot);
                SaveBootstrapDiagnostics(diagnostics);
                if (!diagnostics.scratchDirectoryExists)
                    throw new InvalidOperationException($"Scratch directory '{record.scratchRoot}' was not created.");

                diagnostics.stage = "create_scene";
                tempScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                if (!EditorSceneManager.SaveScene(tempScene, record.temporaryScenePath, false))
                    throw new InvalidOperationException($"Temporary scene could not be saved at '{record.temporaryScenePath}'.");
                diagnostics.sceneCreateSucceeded = true;
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                diagnostics.sceneAssetExists = AssetDatabase.LoadAssetAtPath<SceneAsset>(record.temporaryScenePath) != null;
                SaveBootstrapDiagnostics(diagnostics);
                if (!diagnostics.sceneAssetExists)
                    throw new InvalidOperationException($"Temporary scene asset was not created at '{record.temporaryScenePath}'.");

                // A standalone create_scene request and a subsequent set_active_scene request are separated
                // by an Editor update. Reproduce that boundary here before activation so Unity 6000.3 has a
                // chance to publish the newly-created additive scene into the authoritative loaded-scene list.
                diagnostics.stage = "wait_for_scene_registration";
                await NextEditorUpdateAsync();

                diagnostics.stage = "verify_loaded_scene";
                diagnostics.loadedScenesAfterCreate = CaptureOpenScenes();
                tempScene = FindOpenSceneByPath(record.temporaryScenePath);
                diagnostics.matchingLoadedSceneFound = tempScene.IsValid() && tempScene.isLoaded;
                diagnostics.activeSceneBeforeActivation = CaptureScene(SceneManager.GetActiveScene());
                SaveBootstrapDiagnostics(diagnostics);
                if (!diagnostics.matchingLoadedSceneFound)
                    throw new InvalidOperationException(
                        $"Temporary scene asset exists at '{record.temporaryScenePath}', but no loaded scene has that exact path.");

                diagnostics.stage = "activate_scene";
                diagnostics.setActiveSceneAttempted = true;

                // If creation already made the expected scene active, treat that as success rather than
                // performing a redundant activation call. Otherwise use Unity's public SceneManager API.
                var activeNow = SceneManager.GetActiveScene();
                if (activeNow.IsValid() && activeNow.handle == tempScene.handle)
                {
                    diagnostics.setActiveSceneSucceeded = true;
                }
                else
                {
                    diagnostics.setActiveSceneSucceeded = SceneManager.SetActiveScene(tempScene);
                }

                diagnostics.activeSceneAfterActivation = CaptureScene(SceneManager.GetActiveScene());
                SaveBootstrapDiagnostics(diagnostics);

                var activeScene = SceneManager.GetActiveScene();
                if (!activeScene.IsValid() || activeScene.handle != tempScene.handle)
                    throw new InvalidOperationException("Activation completed, but the temporary test scene is not the active scene.");

                record.temporarySceneName = tempScene.name;
                record.temporarySceneHandle = GetSceneHandleRawData(tempScene);

                diagnostics.stage = "complete";
                diagnostics.error = null;
                SaveBootstrapDiagnostics(diagnostics);

                Save(record);
            }
            catch (Exception ex)
            {
                diagnostics.error = ex.GetType().Name + ": " + ex.Message;
                diagnostics.activeSceneAfterActivation = CaptureScene(SceneManager.GetActiveScene());
                SaveBootstrapDiagnostics(diagnostics);

                RestoreOriginalActiveScene(record);
                if (tempScene.IsValid() && tempScene.isLoaded)
                {
                    EditorSceneManager.CloseScene(tempScene, true);
                }

                DeleteScratch(record);
                throw new InvalidOperationException(
                    ex.Message + " Bootstrap diagnostics are available at pipeline_extensions_status.testSession.lastBootstrap.", ex);
            }

            return new
            {
                sessionId = record.sessionId,
                temporaryScene = new
                {
                    name = record.temporarySceneName,
                    handle = record.temporarySceneHandle,
                    path = record.temporaryScenePath,
                    savedAsAsset = true,
                    isActive = true
                },
                scratchRoot = record.scratchRoot,
                bootstrap = diagnostics,
                originalScenes = record.originalScenes.Select(s => new
                {
                    s.name,
                    s.path,
                    s.wasDirty,
                    s.wasActive
                }).ToArray(),
                safety = new
                {
                    additiveScene = true,
                    originalScenesSaved = false,
                    dirtyScenesAllowedButNotModified = true,
                    scratchAssetsTracked = true
                }
            };
        }

        internal static CleanupReport End(string requestedSessionId = null)
        {
            GuardEditorReady("end_test_session");

            if (!TryGetActive(out var record))
                throw new InvalidOperationException("No Pipeline test session is active.");

            if (!string.IsNullOrEmpty(requestedSessionId) &&
                !string.Equals(requestedSessionId, record.sessionId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Requested session '{requestedSessionId}' does not match active session '{record.sessionId}'.");
            }

            var report = new CleanupReport { sessionId = record.sessionId };

            try
            {
                report.originalActiveSceneRestored = RestoreOriginalActiveScene(record);
            }
            catch (Exception ex)
            {
                report.errors.Add("Restore active scene: " + ex.Message);
            }

            try
            {
                var tempScene = FindTemporaryScene(record);
                if (tempScene.IsValid() && tempScene.isLoaded)
                {
                    // This scene is explicitly disposable; close it without saving.
                    report.temporarySceneClosed = EditorSceneManager.CloseScene(tempScene, true);
                }
                else
                {
                    report.temporarySceneClosed = true;
                }
            }
            catch (Exception ex)
            {
                report.errors.Add("Close temporary scene: " + ex.Message);
            }

            try
            {
                report.scratchAssetsDeleted = DeleteScratch(record);
            }
            catch (Exception ex)
            {
                report.errors.Add("Delete scratch assets: " + ex.Message);
            }

            VerifyOriginalSceneDirtyState(record, report);

            report.cleanupComplete = report.temporarySceneClosed && report.scratchAssetsDeleted;
            if (report.cleanupComplete)
                SessionState.EraseString(SessionStateKey);
            else
                Save(record); // retain enough state for a cleanup retry

            return report;
        }

        private static BootstrapDiagnostics GetLastBootstrapDiagnostics()
        {
            var json = SessionState.GetString(BootstrapDiagnosticsStateKey, string.Empty);
            if (string.IsNullOrEmpty(json))
                return null;

            try
            {
                return JsonUtility.FromJson<BootstrapDiagnostics>(json);
            }
            catch
            {
                return null;
            }
        }

        private static void SaveBootstrapDiagnostics(BootstrapDiagnostics diagnostics)
        {
            if (diagnostics == null)
            {
                SessionState.EraseString(BootstrapDiagnosticsStateKey);
                return;
            }

            SessionState.SetString(BootstrapDiagnosticsStateKey, JsonUtility.ToJson(diagnostics));
        }

        private static List<BootstrapSceneState> CaptureOpenScenes()
        {
            var active = SceneManager.GetActiveScene();
            var result = new List<BootstrapSceneState>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                result.Add(CaptureScene(scene, active));
            }

            return result;
        }

        private static BootstrapSceneState CaptureScene(Scene scene)
        {
            return CaptureScene(scene, SceneManager.GetActiveScene());
        }

        private static BootstrapSceneState CaptureScene(Scene scene, Scene active)
        {
            if (!scene.IsValid())
                return null;

            return new BootstrapSceneState
            {
                name = scene.name,
                path = scene.path,
                handle = GetSceneHandleRawData(scene),
                isLoaded = scene.isLoaded,
                isDirty = scene.isLoaded && scene.isDirty,
                isActive = active.IsValid() && scene.handle == active.handle
            };
        }

        private static Task NextEditorUpdateAsync()
        {
            var completion = new TaskCompletionSource<bool>();
            EditorApplication.delayCall += () => completion.TrySetResult(true);
            return completion.Task;
        }

        private static void GuardEditorReady(string command)
        {
            GuardNotPlaying(command);
            if (EditorApplication.isCompiling)
                throw new InvalidOperationException($"'{command}' cannot start while Unity is compiling.");
            if (EditorApplication.isUpdating)
                throw new InvalidOperationException($"'{command}' cannot start while Unity is importing/updating assets.");
        }

        private static void GuardNotPlaying(string command)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException(
                    $"'{command}' cannot run while Unity is in (or entering) Play Mode. Exit Play Mode and retry.");
        }

        private static void Save(SessionRecord record)
        {
            SessionState.SetString(SessionStateKey, JsonUtility.ToJson(record));
        }

        private static Scene FindOpenSceneByPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return default;

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (string.Equals(scene.path, path, StringComparison.OrdinalIgnoreCase))
                    return scene;
            }

            return default;
        }

        private static Scene FindTemporaryScene(SessionRecord record)
        {
            if (record == null)
                return default;

            if (record.temporarySceneHandle != 0)
            {
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (GetSceneHandleRawData(scene) == record.temporarySceneHandle)
                        return scene;
                }
            }

            if (!string.IsNullOrEmpty(record.temporaryScenePath))
            {
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (string.Equals(
                        scene.path,
                        record.temporaryScenePath,
                        StringComparison.OrdinalIgnoreCase))
                        return scene;
                }

                // Current records always persist an exact path. If it no longer
                // resolves, do not fall back to a shared filename such as
                // TemporaryScene and risk closing a pre-existing user scene.
                return default;
            }

            // Legacy records may lack both a usable handle and a path. A name is
            // safe only when it identifies exactly one loaded scene.
            Scene uniqueNameMatch = default;
            var nameMatchCount = 0;
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!string.Equals(scene.name, record.temporarySceneName, StringComparison.Ordinal))
                    continue;

                uniqueNameMatch = scene;
                nameMatchCount++;
                if (nameMatchCount > 1)
                    return default;
            }

            return nameMatchCount == 1 ? uniqueNameMatch : default;
        }

        private static bool RestoreOriginalActiveScene(SessionRecord record)
        {
            var original = record.originalScenes.FirstOrDefault(s => s.wasActive);
            if (original == null)
                return false;

            var scene = FindOriginalScene(original);
            return scene.IsValid() && scene.isLoaded && SceneManager.SetActiveScene(scene);
        }

        private static Scene FindOriginalScene(SceneSnapshot snapshot)
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (GetSceneHandleRawData(scene) == snapshot.handle)
                    return scene;
                if (!string.IsNullOrEmpty(snapshot.path) &&
                    string.Equals(scene.path, snapshot.path, StringComparison.OrdinalIgnoreCase))
                    return scene;
                if (string.IsNullOrEmpty(snapshot.path) &&
                    string.Equals(scene.name, snapshot.name, StringComparison.Ordinal))
                    return scene;
            }

            return default;
        }

        private static ulong GetSceneHandleRawData(Scene scene)
        {
#if UNITY_6000_5_OR_NEWER
            return scene.handle.GetRawData();
#else
            return unchecked((ulong)(int)scene.handle);
#endif
        }

        private static void VerifyOriginalSceneDirtyState(SessionRecord record, CleanupReport report)
        {
            foreach (var snapshot in record.originalScenes)
            {
                var scene = FindOriginalScene(snapshot);
                if (!scene.IsValid())
                {
                    report.missingOriginalScenes.Add(
                        string.IsNullOrEmpty(snapshot.path) ? snapshot.name : snapshot.path);
                    continue;
                }

                if (scene.isDirty != snapshot.wasDirty)
                {
                    report.dirtyStateChanges.Add(
                        $"{(string.IsNullOrEmpty(scene.path) ? scene.name : scene.path)}: " +
                        $"wasDirty={snapshot.wasDirty}, nowDirty={scene.isDirty}");
                }
            }

            report.preexistingSceneDirtyStatePreserved =
                report.dirtyStateChanges.Count == 0 && report.missingOriginalScenes.Count == 0;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;

            var normalized = path.Replace('\\', '/').TrimEnd('/');
            var parent = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
            var name = Path.GetFileName(normalized);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
                throw new ArgumentException($"Invalid Unity folder path '{path}'.");

            if (!AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);

            if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, name)))
                throw new InvalidOperationException($"Failed to create scratch folder '{path}'.");
        }

        private static bool DeleteScratch(SessionRecord record)
        {
            if (AssetDatabase.IsValidFolder(record.scratchRoot) && !AssetDatabase.DeleteAsset(record.scratchRoot))
                return false;

            AssetDatabase.Refresh();

            // Remove our parent root only when this session created it and it is now empty. Never
            // delete a pre-existing user folder named __PipelineTemp.
            if (!record.scratchParentExisted && AssetDatabase.IsValidFolder(ScratchParent))
            {
                var absolute = Path.Combine(
                    Path.GetDirectoryName(Application.dataPath) ?? string.Empty,
                    ScratchParent.Replace('/', Path.DirectorySeparatorChar));

                if (Directory.Exists(absolute) && !Directory.EnumerateFileSystemEntries(absolute).Any())
                    AssetDatabase.DeleteAsset(ScratchParent);
            }

            AssetDatabase.Refresh();
            return !AssetDatabase.IsValidFolder(record.scratchRoot);
        }
    }
}
