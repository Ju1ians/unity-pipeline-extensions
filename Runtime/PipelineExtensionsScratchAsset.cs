#if UNITY_EDITOR
using UnityEngine;

namespace UnityPipeline.Extensions
{
    /// <summary>Disposable asset type used by pipeline_self_test to verify scratch-asset cleanup.</summary>
    public sealed class PipelineExtensionsScratchAsset : ScriptableObject
    {
        public string marker;
    }
}
#endif
