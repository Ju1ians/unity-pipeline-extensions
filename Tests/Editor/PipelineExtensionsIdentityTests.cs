using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityPipeline.Extensions.Editor;

namespace UnityPipeline.Extensions.Tests.Editor
{
    public class PipelineExtensionsIdentityTests
    {
        [Test]
        public void Status_ReportsCanonicalCompatibilityBuild()
        {
            var status = JObject.FromObject(PipelineExtensionCommands.PipelineExtensionsStatus());
            Assert.AreEqual(
                PipelineExtensionsIdentity.PackageName,
                status["extensionIdentity"]?["packageName"]?.ToString());
            Assert.AreEqual(
                PipelineExtensionsIdentity.PackageVersion,
                status["extensionIdentity"]?["packageVersion"]?.ToString());
            Assert.AreEqual(
                PipelineExtensionsIdentity.CompatibilityRevision,
                status["extensionIdentity"]?["compatibilityRevision"]?.ToString());
            Assert.AreEqual(
                PipelineExtensionsIdentity.BuildIdentity,
                status["extensionIdentity"]?["buildIdentity"]?.ToString());
            Assert.AreEqual(
                PipelineExtensionsIdentity.SafeAddComponentCommand,
                status["capabilities"]?["safeAddComponentCommand"]?.ToString());
            Assert.AreEqual(PipelineExtensionsIdentity.ProjectAuditSchema,
                status["capabilities"]?["projectAuditSchema"]?.ToString());
            foreach (var command in new[] { "project_audit_start", "project_audit_status", "project_audit_results", "project_audit_dispose" })
                Assert.IsTrue(status["commandCatalog"]?["registeredByExtension"]?.ToObject<string[]>() is string[] names &&
                    System.Array.IndexOf(names, command) >= 0);
        }
    }
}
