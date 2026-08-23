#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityPipeline.Extensions
{
    /// <summary>
    /// Small runtime-safe probe used only by pipeline_self_test to exercise Unity serialization.
    /// It intentionally contains a List of serializable structs because that is one of Pipeline's
    /// highest-risk serialization paths.
    /// </summary>
    [AddComponentMenu("")]
    public sealed class PipelineExtensionsSerializationProbe : MonoBehaviour
    {
        public int scalar;
        public Vector3 vector;
        public List<PipelineExtensionsProbeItem> items = new List<PipelineExtensionsProbeItem>();
    }

    [Serializable]
    public struct PipelineExtensionsProbeItem
    {
        public Vector3 position;
        public int count;
        public bool enabled;
    }

}
#endif
