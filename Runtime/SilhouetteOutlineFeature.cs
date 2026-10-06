using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SilhouetteOutline
{
    /// <summary>
    /// Add to the URP Renderer Data to draw the outlines registered in <see cref="OutlineManager"/>.
    /// </summary>
    [DisallowMultipleRendererFeature("Silhouette Outline")]
    [Tooltip("Blurred silhouette outline, depth tested against the scene.")]
    public sealed class SilhouetteOutlineFeature : ScriptableRendererFeature
    {
        public const string SILHOUETTE_SHADER = "Hidden/SilhouetteOutline/Silhouette";
        public const string COMPOSITE_SHADER = "Hidden/SilhouetteOutline/Composite";

        [Serializable]
        public sealed class OutlineSettings
        {
            public RenderPassEvent RenderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;

            [Tooltip("Depth bias of the silhouettes, so they pass the LEqual test against the copied scene depth.")]
            public float DepthBias = -1f;

            public float SlopeDepthBias = -1f;

            [Tooltip("Hides the outline where the scene is in front of the outlined object, instead of drawing it over the occluder. " +
                     "Costs one more texture read per blur tap and three full screen R32 textures.")]
            public bool OccludeOutline = false;

            [Tooltip("Eye depth, in world units, the scene may be in front of the object before the outline fades. " +
                     "Keeps the outline along contact edges, such as an object resting on a table. Fully hidden at twice this value.")]
            [Min(0.001f)] public float OcclusionTolerance = 0.1f;
        }

        [SerializeField] private OutlineSettings _settings = new OutlineSettings();

        [Tooltip("Filled automatically. Referenced here so the shaders are included in builds.")]
        [SerializeField] private Shader _silhouetteShader;
        [SerializeField] private Shader _compositeShader;

        private Material _silhouetteMaterial;
        private Material _compositeMaterial;
        private SilhouetteOutlinePass _pass;

        public OutlineSettings Settings => _settings;

        public override void Create()
        {
            ReleaseResources();

            if (_silhouetteShader == null || _compositeShader == null)
            {
                _silhouetteShader = Shader.Find(SILHOUETTE_SHADER);
                _compositeShader = Shader.Find(COMPOSITE_SHADER);
#if UNITY_EDITOR
                // Saves the references in the renderer asset so the shaders ship in builds.
                UnityEditor.EditorUtility.SetDirty(this);
#endif
            }

            if (_silhouetteShader == null || _compositeShader == null)
            {
                Debug.LogError($"[SilhouetteOutline] Missing shaders, expected {SILHOUETTE_SHADER} and {COMPOSITE_SHADER}.");
                return;
            }

            _silhouetteMaterial = CoreUtils.CreateEngineMaterial(_silhouetteShader);
            _silhouetteMaterial.enableInstancing = true;
            _compositeMaterial = CoreUtils.CreateEngineMaterial(_compositeShader);

            _pass = new SilhouetteOutlinePass(_silhouetteMaterial, _compositeMaterial);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_pass == null || !OutlineManager.HasWork) return;

            CameraType cameraType = renderingData.cameraData.cameraType;
            if (cameraType == CameraType.Preview || cameraType == CameraType.Reflection) return;

            _pass.Setup(_settings);
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            ReleaseResources();
        }

        private void ReleaseResources()
        {
            _pass?.Dispose();
            _pass = null;

            CoreUtils.Destroy(_silhouetteMaterial);
            CoreUtils.Destroy(_compositeMaterial);
            _silhouetteMaterial = null;
            _compositeMaterial = null;
        }

#if UNITY_EDITOR
        private void Reset()
        {
            _silhouetteShader = Shader.Find(SILHOUETTE_SHADER);
            _compositeShader = Shader.Find(COMPOSITE_SHADER);
        }
#endif
    }
}
