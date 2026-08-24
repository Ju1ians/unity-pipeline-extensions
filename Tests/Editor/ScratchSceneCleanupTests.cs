using System;
using System.Collections;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityPipeline.Extensions.Editor;

namespace UnityPipeline.Extensions.Tests.Editor
{
    public class ScratchSceneCleanupTests
    {
        private const string ScratchParent = "Assets/__PipelineTemp";

        [UnityTest]
        public IEnumerator EndTestSession_PreservesDirtySceneWithSameFilename()
        {
            var scratchParentExisted = AssetDatabase.IsValidFolder(ScratchParent);
            var fixtureRoot = ScratchParent + "/cleanup-regression-" +
                Guid.NewGuid().ToString("N").Substring(0, 12);
            var fixtureScenePath = fixtureRoot + "/TemporaryScene.unity";
            var activeBefore = SceneManager.GetActiveScene();
            var useTestRunnerUntitledScene = activeBefore.IsValid() &&
                string.IsNullOrEmpty(activeBefore.path);

            Scene preexistingScene = default;
            string sessionId = null;
            string sessionScenePath = null;
            string sessionScratchRoot = null;
            try
            {
                EnsureFolder(fixtureRoot);
                // EditMode tests run in a disposable untitled scene. Saving that
                // runner-owned scene gives the regression a real same-filename
                // pre-existing scene without touching a project asset.
                preexistingScene = useTestRunnerUntitledScene
                    ? activeBefore
                    : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                Assert.IsTrue(EditorSceneManager.SaveScene(preexistingScene, fixtureScenePath));

                var marker = new GameObject("PreexistingDirtyMarker");
                SceneManager.MoveGameObjectToScene(marker, preexistingScene);
                EditorSceneManager.MarkSceneDirty(preexistingScene);
                Assert.IsTrue(preexistingScene.isDirty);

                var beginTask = PipelineExtensionCommands.BeginTestSession();
                while (!beginTask.IsCompleted)
                    yield return null;
                if (beginTask.IsFaulted)
                    throw beginTask.Exception?.GetBaseException() ?? beginTask.Exception;

                var begin = JObject.FromObject(beginTask.Result);
                sessionId = begin["sessionId"]?.ToString();
                sessionScenePath = begin["temporaryScene"]?["path"]?.ToString();
                sessionScratchRoot = begin["scratchRoot"]?.ToString();
                Assert.IsNotEmpty(sessionId);
                Assert.AreEqual("TemporaryScene", preexistingScene.name);
                Assert.AreEqual("TemporaryScene", begin["temporaryScene"]?["name"]?.ToString());
                Assert.AreNotEqual(fixtureScenePath, sessionScenePath);

                var cleanup = JObject.FromObject(
                    PipelineExtensionCommands.EndTestSession(sessionId));
                sessionId = null;

                Assert.IsTrue(cleanup["temporarySceneClosed"]?.Value<bool>());
                Assert.IsTrue(cleanup["scratchAssetsDeleted"]?.Value<bool>());
                Assert.IsTrue(cleanup["preexistingSceneDirtyStatePreserved"]?.Value<bool>());
                Assert.IsTrue(cleanup["cleanupComplete"]?.Value<bool>());

                var preserved = SceneManager.GetSceneByPath(fixtureScenePath);
                Assert.IsTrue(preserved.IsValid() && preserved.isLoaded);
                Assert.IsTrue(preserved.isDirty);
                Assert.IsFalse(SceneManager.GetSceneByPath(sessionScenePath).isLoaded);
                Assert.IsFalse(AssetDatabase.IsValidFolder(sessionScratchRoot));
            }
            finally
            {
                if (!string.IsNullOrEmpty(sessionId))
                {
                    try
                    {
                        PipelineExtensionCommands.EndTestSession(sessionId);
                    }
                    catch
                    {
                        // Preserve the original assertion when emergency cleanup also fails.
                    }
                }

                var fixture = SceneManager.GetSceneByPath(fixtureScenePath);
                if (fixture.IsValid() && fixture.isLoaded)
                    EditorSceneManager.CloseScene(fixture, true);
                AssetDatabase.DeleteAsset(fixtureRoot);
                AssetDatabase.Refresh();

                var scratchParentAbsolute = Path.Combine(
                    Path.GetDirectoryName(Application.dataPath) ?? string.Empty,
                    ScratchParent.Replace('/', Path.DirectorySeparatorChar));
                if (!scratchParentExisted &&
                    Directory.Exists(scratchParentAbsolute) &&
                    !Directory.EnumerateFileSystemEntries(scratchParentAbsolute).Any())
                {
                    AssetDatabase.DeleteAsset(ScratchParent);
                }
                AssetDatabase.Refresh();
            }
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;

            var segments = path.Split('/');
            var current = segments[0];
            for (var i = 1; i < segments.Length; i++)
            {
                var next = current + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(next))
                    Assert.IsNotEmpty(AssetDatabase.CreateFolder(current, segments[i]));
                current = next;
            }
        }
    }
}
