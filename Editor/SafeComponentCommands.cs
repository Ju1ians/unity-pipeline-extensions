using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Pipeline.Commands;
using Unity.Pipeline.Editor.Authoring;
using Unity.Pipeline.Models;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityPipeline.Extensions.Editor
{
    /// <summary>
    /// Product-owned component authoring commands. Component discovery intentionally uses
    /// Unity's TypeCache so unrelated analyzer/code-fix assemblies are never reflected.
    /// </summary>
    public static class SafeComponentCommands
    {
        [CliCommand(
            "safe_add_component",
            "Add a component (by full or unambiguous short type name) to a GameObject using an analyzer-safe resolver.",
            Tags = new[] { "extensions", "extensions/authoring", "gameobjects/components" })]
        public static AuthoringResult AddComponent(
            [CliArg("target", "Handle of the GameObject (a Component handle uses its owning GameObject).", Required = true)] ObjectRef target,
            [CliArg("type", "Component type name (for example 'Rigidbody' or 'UnityEngine.Camera').", Required = true)] string type)
        {
            var gameObject = ResolveGameObject(target);
            var componentType = ResolveComponentType(type);
            if (componentType == null)
                throw new ArgumentException($"Could not resolve component type '{type}'. Use a full type name when a short name is ambiguous.");

            using (new AuthoringUndoScope("Add Component"))
            {
                var component = Undo.AddComponent(gameObject, componentType);
                if (component == null)
                {
                    throw new InvalidOperationException(
                        $"Failed to add component '{componentType.Name}' to '{gameObject.name}' (it may be disallowed on this GameObject).");
                }

                EditorUtility.SetDirty(gameObject);
                if (gameObject.scene.IsValid())
                    EditorSceneManager.MarkSceneDirty(gameObject.scene);
                return ObjectResolver.Describe(component);
            }
        }

        /// <summary>
        /// Resolve an exact full name first, then an unambiguous short name, from Unity's
        /// editor-maintained index of concrete Component types.
        /// </summary>
        public static Type ResolveComponentType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return null;

            typeName = typeName.Trim();
            var direct = Type.GetType(typeName, throwOnError: false, ignoreCase: false);
            if (IsConcreteComponent(direct))
                return direct;

            var fullNameMatches = new List<Type>();
            var shortNameMatches = new List<Type>();
            foreach (var candidate in TypeCache.GetTypesDerivedFrom<Component>())
            {
                if (!IsConcreteComponent(candidate))
                    continue;
                if (string.Equals(candidate.FullName, typeName, StringComparison.Ordinal))
                    fullNameMatches.Add(candidate);
                else if (string.Equals(candidate.Name, typeName, StringComparison.Ordinal))
                    shortNameMatches.Add(candidate);
            }

            var distinctFullNames = fullNameMatches.Distinct().ToList();
            if (distinctFullNames.Count == 1)
                return distinctFullNames[0];
            if (distinctFullNames.Count > 1)
                return null;

            var distinctShortNames = shortNameMatches.Distinct().ToList();
            return distinctShortNames.Count == 1 ? distinctShortNames[0] : null;
        }

        private static GameObject ResolveGameObject(ObjectRef target)
        {
            if (!ObjectResolver.TryResolve(target, out var resolved, out var error))
                throw new ArgumentException($"Could not resolve 'target': {error}");

            var gameObject = resolved as GameObject ?? (resolved as Component)?.gameObject;
            if (gameObject == null)
                throw new ArgumentException($"'target' did not resolve to a GameObject (got {resolved.GetType().Name}).");
            return gameObject;
        }

        private static bool IsConcreteComponent(Type type)
        {
            return type != null && typeof(Component).IsAssignableFrom(type) && !type.IsAbstract;
        }
    }
}
