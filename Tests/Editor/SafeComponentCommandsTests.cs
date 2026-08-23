using System;
using NUnit.Framework;
using Unity.Pipeline.Editor.Authoring;
using Unity.Pipeline.Models;
using UnityEditor;
using UnityEngine;
using UnityPipeline.Extensions.Editor;
using Object = UnityEngine.Object;

namespace UnityPipeline.Extensions.Tests.Editor
{
    public class SafeComponentCommandsTests
    {
        private GameObject _gameObject;

        [SetUp]
        public void SetUp()
        {
            _gameObject = new GameObject("SafeComponentTests");
            Undo.ClearAll();
        }

        [TearDown]
        public void TearDown()
        {
            if (_gameObject != null)
                Object.DestroyImmediate(_gameObject);
            Undo.ClearAll();
        }

        [Test]
        public void Resolver_AcceptsFullAndUniqueShortNames()
        {
            Assert.AreEqual(typeof(BoxCollider), SafeComponentCommands.ResolveComponentType("BoxCollider"));
            Assert.AreEqual(typeof(BoxCollider), SafeComponentCommands.ResolveComponentType("UnityEngine.BoxCollider"));
        }

        [Test]
        public void Resolver_RejectsNonComponentsAbstractComponentsAndAmbiguousShortNames()
        {
            Assert.IsNull(SafeComponentCommands.ResolveComponentType(typeof(string).FullName));
            Assert.IsNull(SafeComponentCommands.ResolveComponentType(typeof(Component).FullName));
            Assert.IsNull(SafeComponentCommands.ResolveComponentType("AmbiguousTestComponent"));
            Assert.AreEqual(
                typeof(AmbiguityOne.AmbiguousTestComponent),
                SafeComponentCommands.ResolveComponentType(typeof(AmbiguityOne.AmbiguousTestComponent).FullName));
        }

        [Test]
        public void AddComponent_AcceptsGameObjectRefAndReturnsAuthoritativeComponentRef()
        {
            var result = SafeComponentCommands.AddComponent(Ref(_gameObject), "BoxCollider");

            Assert.NotNull(result);
            Assert.AreEqual("BoxCollider", result.Type);
            Assert.IsTrue(result.InstanceId.HasValue);
            Assert.IsTrue(ObjectResolver.TryResolve(ToRef(result), out var resolved, out var error), error);
            Assert.IsInstanceOf<BoxCollider>(resolved);
        }

        [Test]
        public void AddComponent_ComponentTargetUsesOwningGameObjectAndAllowsSupportedDuplicates()
        {
            var first = _gameObject.AddComponent<BoxCollider>();
            SafeComponentCommands.AddComponent(Ref(first), "BoxCollider");

            Assert.AreEqual(2, _gameObject.GetComponents<BoxCollider>().Length);
        }

        [Test]
        public void AddComponent_IsUndoable()
        {
            SafeComponentCommands.AddComponent(Ref(_gameObject), "BoxCollider");
            Assert.NotNull(_gameObject.GetComponent<BoxCollider>());

            Undo.PerformUndo();

            Assert.IsNull(_gameObject.GetComponent<BoxCollider>());
        }

        [Test]
        public void AddComponent_InvalidTypeFailsWithoutMutation()
        {
            Assert.Throws<ArgumentException>(() =>
                SafeComponentCommands.AddComponent(Ref(_gameObject), "System.String"));
            Assert.IsNull(_gameObject.GetComponent<BoxCollider>());
        }

        private static ObjectRef Ref(Object value)
        {
            var described = ObjectResolver.Describe(value);
            return new ObjectRef
            {
                InstanceId = described.InstanceId,
                HierarchyPath = described.HierarchyPath
            };
        }

        private static ObjectRef ToRef(AuthoringResult result)
        {
            return new ObjectRef
            {
                InstanceId = result.InstanceId,
                HierarchyPath = result.HierarchyPath
            };
        }
    }
}

namespace UnityPipeline.Extensions.Tests.Editor.AmbiguityOne
{
    public sealed class AmbiguousTestComponent : MonoBehaviour { }
}

namespace UnityPipeline.Extensions.Tests.Editor.AmbiguityTwo
{
    public sealed class AmbiguousTestComponent : MonoBehaviour { }
}
