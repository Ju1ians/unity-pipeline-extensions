using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;
using Unity.Pipeline.Editor.Authoring;
using Unity.Pipeline.Models;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityPipeline.Extensions.Editor
{
    public static class PipelineExtensionCommands
    {
        [CliCommand(
            "pipeline_extensions_status",
            "Read Unity/Pipeline/extension versions, compilation/play state, open-scene safety state, authoring root, and active test-session status.",
            Tags = new[] { "extensions", "extensions/diagnostics" })]
        public static object PipelineExtensionsStatus()
        {
            var extensionPackage = SafePackageInfo(typeof(PipelineExtensionCommands).Assembly);
            var pipelinePackage = SafePackageInfo(typeof(CliCommandAttribute).Assembly);
            var activeScene = SceneManager.GetActiveScene();
            var scenes = new List<object>();
            var dirtySceneCount = 0;

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty)
                    dirtySceneCount++;

                scenes.Add(new
                {
                    scene.name,
                    scene.path,
                    handle = scene.handle,
                    isLoaded = scene.isLoaded,
                    isDirty = scene.isDirty,
                    isActive = scene.handle == activeScene.handle
                });
            }

            var extensionCommands = new[]
            {
                "pipeline_extensions_status",
                "begin_test_session",
                "end_test_session",
                "pipeline_self_test",
                PipelineExtensionsIdentity.SafeAddComponentCommand
            };

            return new
            {
                unityVersion = Application.unityVersion,
                projectPath = Path.GetDirectoryName(Application.dataPath),
                pipeline = pipelinePackage,
                extensions = extensionPackage,
                extensionIdentity = new
                {
                    packageName = PipelineExtensionsIdentity.PackageName,
                    packageVersion = PipelineExtensionsIdentity.PackageVersion,
                    compatibilityRevision = PipelineExtensionsIdentity.CompatibilityRevision,
                    buildIdentity = PipelineExtensionsIdentity.BuildIdentity
                },
                capabilities = new
                {
                    safeAddComponentCommand = PipelineExtensionsIdentity.SafeAddComponentCommand
                },
                commandCatalog = new
                {
                    registeredByExtension = extensionCommands,
                    fromThisExtension = extensionCommands.Length
                },
                editor = new
                {
                    compiling = EditorApplication.isCompiling,
                    updating = EditorApplication.isUpdating,
                    playing = EditorApplication.isPlaying,
                    paused = EditorApplication.isPaused,
                    playMode = EditorApplication.isPaused ? "paused" : EditorApplication.isPlaying ? "playing" : "stopped"
                },
                authoringRoot = ProjectPaths.AuthoringRoot,
                openScenes = scenes,
                dirtySceneCount,
                testSession = TestSessionManager.GetStatus()
            };
        }

        [CliCommand(
            "begin_test_session",
            "Create an additive empty temporary scene under Assets/__PipelineTemp, make it active, and track the whole scratch root for cleanup without replacing existing scenes.",
            Tags = new[] { "extensions", "extensions/testing" })]
        public static async Task<object> BeginTestSession()
        {
            return await TestSessionManager.BeginAsync();
        }

        [CliCommand(
            "end_test_session",
            "Restore the original active scene, discard the temporary test scene without saving, delete the tracked scratch assets, and report whether pre-existing scene dirty state was preserved.",
            Tags = new[] { "extensions", "extensions/testing" })]
        public static object EndTestSession(
            [CliArg("session_id", "Optional safety check: must match the currently active test session when supplied.")] string sessionId = null)
        {
            return TestSessionManager.End(sessionId);
        }

        [CliCommand(
            "pipeline_self_test",
            "Run a deterministic isolated regression test of ObjectRef round-tripping, safe_add_component, transform mutation, nested serialized struct arrays, scratch assets, and cleanup.",
            Tags = new[] { "extensions", "extensions/testing", "extensions/diagnostics" })]
        public static async Task<object> PipelineSelfTest()
        {
            var report = new SelfTestReport
            {
                unityVersion = Application.unityVersion,
                startedUtc = DateTime.UtcNow.ToString("o")
            };

            string sessionId = null;
            AuthoringResult gameObjectResult = null;
            ObjectRef gameObjectRef = null;
            AuthoringResult probeComponentResult = null;
            ObjectRef probeComponentRef = null;

            try
            {
                var beginStopwatch = Stopwatch.StartNew();
                try
                {
                    var begin = await TestSessionManager.BeginAsync();
                    var token = JToken.FromObject(begin);
                    sessionId = token["sessionId"]?.ToString();
                    if (string.IsNullOrEmpty(sessionId))
                        throw new InvalidOperationException("begin_test_session did not return a sessionId.");
                    report.sessionId = sessionId;
                    report.tests.Add(new SelfTestCaseResult
                    {
                        name = "Begin isolated test session",
                        passed = true,
                        durationMs = beginStopwatch.ElapsedMilliseconds
                    });
                }
                catch (Exception ex)
                {
                    report.tests.Add(new SelfTestCaseResult
                    {
                        name = "Begin isolated test session",
                        passed = false,
                        durationMs = beginStopwatch.ElapsedMilliseconds,
                        details = ex.GetType().Name + ": " + ex.Message
                    });
                }

                RunCase(report, "Create GameObject through Pipeline", () =>
                {
                    RequireSession(sessionId);
                    var gameObject = new GameObject("PipelineExtensions_SelfTest");
                    Undo.RegisterCreatedObjectUndo(gameObject, "Pipeline Extensions self-test GameObject");
                    gameObjectResult = ObjectResolver.Describe(gameObject);
                    gameObjectRef = ToRef(gameObjectResult);
                    if (gameObjectResult == null || gameObjectRef == null || gameObjectRef.IsEmpty)
                        throw new InvalidOperationException("Pipeline did not return a usable ObjectRef identity for the created GameObject.");
                });

                RunCase(report, "ObjectRef roundtrip", () =>
                {
                    RequireRef(gameObjectRef, "GameObject");
                    if (!ObjectResolver.TryResolve(gameObjectRef, out var resolved, out var error))
                        throw new InvalidOperationException("ObjectRef failed to resolve: " + error);
                    if (!(resolved is GameObject go) || go.name != "PipelineExtensions_SelfTest")
                        throw new InvalidOperationException("ObjectRef resolved to the wrong object.");
                });

                RunCase(report, "safe_add_component via analyzer-safe resolver", () =>
                {
                    RequireRef(gameObjectRef, "GameObject");
                    var box = SafeComponentCommands.AddComponent(gameObjectRef, "BoxCollider");
                    var boxRef = ToRef(box);
                    if (!ObjectResolver.TryResolve(boxRef, out var resolved, out var error) || !(resolved is BoxCollider))
                        throw new InvalidOperationException("BoxCollider readback failed: " + error);
                });

                RunCase(report, "Transform mutation and readback", () =>
                {
                    RequireRef(gameObjectRef, "GameObject");
                    if (!ObjectResolver.TryResolve(gameObjectRef, out var resolved, out var error) || !(resolved is GameObject go))
                        throw new InvalidOperationException("Transform target readback failed: " + error);

                    Undo.RecordObject(go.transform, "Pipeline Extensions self-test transform");
                    go.transform.localPosition = new Vector3(1.25f, 2.5f, -3.75f);
                    go.transform.localEulerAngles = new Vector3(10f, 20f, 30f);
                    go.transform.localScale = new Vector3(2f, 3f, 4f);
                    EditorUtility.SetDirty(go.transform);

                    AssertApproximately(go.transform.localPosition, new Vector3(1.25f, 2.5f, -3.75f), "position");
                    AssertApproximately(go.transform.localScale, new Vector3(2f, 3f, 4f), "scale");
                    AssertApproximatelyEuler(go.transform.localEulerAngles, new Vector3(10f, 20f, 30f), "rotation");
                });

                RunCase(report, "Add serialization probe component", () =>
                {
                    RequireRef(gameObjectRef, "GameObject");
                    probeComponentResult = SafeComponentCommands.AddComponent(
                        gameObjectRef,
                        "UnityPipeline.Extensions.PipelineExtensionsSerializationProbe");
                    probeComponentRef = ToRef(probeComponentResult);

                    if (!ObjectResolver.TryResolve(probeComponentRef, out var resolved, out var error) ||
                        !(resolved is PipelineExtensionsSerializationProbe))
                    {
                        throw new InvalidOperationException("Serialization probe component readback failed: " + error);
                    }
                });

                RunCase(report, "Serialized scalar and Vector3", () =>
                {
                    RequireRef(probeComponentRef, "serialization probe component");
                    SetSerializedField(probeComponentRef, "scalar", new JValue(42));
                    SetSerializedField(
                        probeComponentRef,
                        "vector",
                        JObject.FromObject(new { x = 4.5f, y = -2f, z = 9.25f }));

                    var probe = ResolveProbe(probeComponentRef);
                    if (probe.scalar != 42)
                        throw new InvalidOperationException($"Scalar readback mismatch: expected 42, got {probe.scalar}.");
                    AssertApproximately(probe.vector, new Vector3(4.5f, -2f, 9.25f), "serialized vector");
                });

                RunCase(report, "Serialized struct-list resize and leaf writes", () =>
                {
                    RequireRef(probeComponentRef, "serialization probe component");
                    SetSerializedField(probeComponentRef, "items.Array.size", new JValue(2));

                    SetVector(probeComponentRef, "items.Array.data[0].position", 1f, 2f, 3f);
                    SetSerializedField(probeComponentRef, "items.Array.data[0].count", new JValue(7));
                    SetSerializedField(probeComponentRef, "items.Array.data[0].enabled", new JValue(true));

                    SetVector(probeComponentRef, "items.Array.data[1].position", -4f, 5f, 6.5f);
                    SetSerializedField(probeComponentRef, "items.Array.data[1].count", new JValue(11));
                    SetSerializedField(probeComponentRef, "items.Array.data[1].enabled", new JValue(false));

                    var probe = ResolveProbe(probeComponentRef);
                    if (probe.items == null || probe.items.Count != 2)
                        throw new InvalidOperationException("Struct-list resize did not produce exactly two elements.");
                    AssertApproximately(probe.items[0].position, new Vector3(1f, 2f, 3f), "items[0].position");
                    if (probe.items[0].count != 7 || !probe.items[0].enabled)
                        throw new InvalidOperationException("items[0] leaf readback mismatch.");
                    AssertApproximately(probe.items[1].position, new Vector3(-4f, 5f, 6.5f), "items[1].position");
                    if (probe.items[1].count != 11 || probe.items[1].enabled)
                        throw new InvalidOperationException("items[1] leaf readback mismatch.");

                    // Verify the same serialized representation through Unity's public API.
                    var value = ReadSerializedVector3(probeComponentRef, "items.Array.data[1].position");
                    if ((value - new Vector3(-4f, 5f, 6.5f)).sqrMagnitude > 0.000001f)
                    {
                        throw new InvalidOperationException("get_serialized_fields nested Vector3 readback mismatch.");
                    }
                });

                RunCase(report, "Tracked scratch asset", () =>
                {
                    RequireSession(sessionId);
                    if (!TestSessionManager.TryGetActive(out var record))
                        throw new InvalidOperationException("Test session record disappeared.");

                    var path = record.scratchRoot + "/SelfTest.asset";
                    var asset = ScriptableObject.CreateInstance<PipelineExtensionsScratchAsset>();
                    asset.marker = sessionId;
                    AssetDatabase.CreateAsset(asset, path);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();

                    var readback = AssetDatabase.LoadAssetAtPath<PipelineExtensionsScratchAsset>(path);
                    if (readback == null || readback.marker != sessionId)
                        throw new InvalidOperationException("Scratch asset could not be read back after creation.");

                    report.scratchAssetPath = path;
                });
            }
            catch (Exception ex)
            {
                // RunCase normally contains individual failures. This catch is for unexpected harness
                // failures that escape between cases.
                report.tests.Add(new SelfTestCaseResult
                {
                    name = "Self-test harness",
                    passed = false,
                    details = ex.GetType().Name + ": " + ex.Message
                });
            }
            finally
            {
                if (!string.IsNullOrEmpty(sessionId))
                {
                    try
                    {
                        report.cleanup = TestSessionManager.End(sessionId);
                    }
                    catch (Exception ex)
                    {
                        report.cleanup = new TestSessionManager.CleanupReport
                        {
                            sessionId = sessionId,
                            cleanupComplete = false,
                            errors = new List<string> { ex.GetType().Name + ": " + ex.Message }
                        };
                    }
                }
            }

            if (report.cleanup != null && !string.IsNullOrEmpty(report.scratchAssetPath))
                report.scratchAssetRemoved = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(report.scratchAssetPath) == null;

            report.finishedUtc = DateTime.UtcNow.ToString("o");
            report.passed = report.tests.Count > 0 && report.tests.All(t => t.passed) &&
                            report.cleanup != null && report.cleanup.cleanupComplete &&
                            report.cleanup.originalActiveSceneRestored &&
                            report.cleanup.preexistingSceneDirtyStatePreserved &&
                            report.scratchAssetRemoved;
            return report;
        }

        private static object SafePackageInfo(System.Reflection.Assembly assembly)
        {
            try
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(assembly);
                if (package == null)
                    return new { found = false };

                return new
                {
                    found = true,
                    package.name,
                    package.displayName,
                    package.version,
                    source = package.source.ToString(),
                    package.resolvedPath
                };
            }
            catch (Exception ex)
            {
                return new { found = false, error = ex.Message };
            }
        }

        private static void RunCase(SelfTestReport report, string name, Action action)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                action();
                report.tests.Add(new SelfTestCaseResult
                {
                    name = name,
                    passed = true,
                    durationMs = stopwatch.ElapsedMilliseconds
                });
            }
            catch (Exception ex)
            {
                report.tests.Add(new SelfTestCaseResult
                {
                    name = name,
                    passed = false,
                    durationMs = stopwatch.ElapsedMilliseconds,
                    details = ex.GetType().Name + ": " + ex.Message
                });
            }
        }

        private static void RequireSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new InvalidOperationException("The isolated test session did not start successfully.");
        }

        private static void RequireRef(ObjectRef reference, string label)
        {
            if (reference == null || reference.IsEmpty)
                throw new InvalidOperationException("No usable ObjectRef is available for " + label + ".");
        }

        private static ObjectRef ToRef(AuthoringResult result)
        {
            if (result == null)
                return null;

            return new ObjectRef
            {
                GlobalId = result.GlobalId,
                Path = result.AssetPath,
                Guid = result.Guid,
                FileId = result.FileId,
                InstanceId = result.InstanceId,
                HierarchyPath = result.HierarchyPath
            };
        }

        private static PipelineExtensionsSerializationProbe ResolveProbe(ObjectRef reference)
        {
            if (!ObjectResolver.TryResolve(reference, out var resolved, out var error) ||
                !(resolved is PipelineExtensionsSerializationProbe probe))
            {
                throw new InvalidOperationException("Could not resolve serialization probe: " + error);
            }

            return probe;
        }

        private static void SetVector(ObjectRef reference, string field, float x, float y, float z)
        {
            SetSerializedField(
                reference,
                field,
                JObject.FromObject(new { x, y, z }));
        }

        private static UnityEngine.Object ResolveSerializedTarget(ObjectRef reference)
        {
            if (!ObjectResolver.TryResolve(reference, out var resolved, out var error) || resolved == null)
                throw new InvalidOperationException("Could not resolve serialized target: " + error);
            return resolved;
        }

        private static void SetSerializedField(ObjectRef reference, string propertyPath, JToken value)
        {
            var target = ResolveSerializedTarget(reference);
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(propertyPath);
            if (property == null)
                throw new InvalidOperationException($"Serialized property '{propertyPath}' was not found.");

            Undo.RecordObject(target, "Pipeline Extensions self-test serialization");
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.ArraySize:
                    property.intValue = value.Value<int>();
                    break;
                case SerializedPropertyType.Boolean:
                    property.boolValue = value.Value<bool>();
                    break;
                case SerializedPropertyType.Vector3:
                    property.vector3Value = new Vector3(
                        value["x"].Value<float>(),
                        value["y"].Value<float>(),
                        value["z"].Value<float>());
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Self-test does not support serialized property type '{property.propertyType}' at '{propertyPath}'.");
            }
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
        }

        private static Vector3 ReadSerializedVector3(ObjectRef reference, string propertyPath)
        {
            var serialized = new SerializedObject(ResolveSerializedTarget(reference));
            serialized.Update();
            var property = serialized.FindProperty(propertyPath);
            if (property == null || property.propertyType != SerializedPropertyType.Vector3)
                throw new InvalidOperationException($"Vector3 serialized property '{propertyPath}' was not found.");
            return property.vector3Value;
        }

        private static void AssertApproximately(Vector3 actual, Vector3 expected, string label)
        {
            if ((actual - expected).sqrMagnitude > 0.000001f)
                throw new InvalidOperationException($"{label} mismatch: expected {expected}, got {actual}.");
        }

        private static void AssertApproximatelyEuler(Vector3 actual, Vector3 expected, string label)
        {
            var dx = Mathf.Abs(Mathf.DeltaAngle(actual.x, expected.x));
            var dy = Mathf.Abs(Mathf.DeltaAngle(actual.y, expected.y));
            var dz = Mathf.Abs(Mathf.DeltaAngle(actual.z, expected.z));
            if (dx > 0.01f || dy > 0.01f || dz > 0.01f)
                throw new InvalidOperationException($"{label} mismatch: expected {expected}, got {actual}.");
        }

        private sealed class SelfTestCaseResult
        {
            public string name;
            public bool passed;
            public long durationMs;
            public string details;
        }

        private sealed class SelfTestReport
        {
            public bool passed;
            public string unityVersion;
            public string startedUtc;
            public string finishedUtc;
            public string sessionId;
            public string scratchAssetPath;
            public bool scratchAssetRemoved;
            public List<SelfTestCaseResult> tests = new List<SelfTestCaseResult>();
            public TestSessionManager.CleanupReport cleanup;
        }
    }
}
