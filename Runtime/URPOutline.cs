using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SilhouetteOutline
{
    /// <summary>
    /// Outlines the mesh and skinned mesh renderers of this GameObject (and its children) while enabled.
    /// Needs the Silhouette Outline Renderer Feature on the URP renderer.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Rendering/URP Outline")]
    public sealed class URPOutline : MonoBehaviour
    {
        internal struct Entry
        {
            public Renderer Renderer;
            public MeshFilter Filter;
            public SkinnedMeshRenderer Skinned;

            public Mesh Mesh => Skinned != null ? Skinned.sharedMesh : Filter != null ? Filter.sharedMesh : null;

            public bool IsDrawable =>
                Renderer != null
                && Renderer.enabled
                && Renderer.gameObject.activeInHierarchy
                && !Renderer.forceRenderingOff
                && Renderer.shadowCastingMode != ShadowCastingMode.ShadowsOnly;
        }

        [SerializeField] private Color _color = Color.white;

        [Tooltip("Outline width in pixels. Each distinct width in view adds one blur chain.")]
        [Range(OutlineInstruction.MIN_WIDTH, OutlineInstruction.MAX_WIDTH)]
        [SerializeField] private float _width = OutlineInstruction.DEFAULT_WIDTH;

        [Tooltip("Also outline the renderers of the children. Call RefreshRenderers after changing the hierarchy at runtime.")]
        [SerializeField] private bool _includeChildren = true;

        private readonly List<Entry> _entries = new List<Entry>();

        public Color Color
        {
            get => _color;
            set => _color = value;
        }

        public float Width
        {
            get => _width;
            set => _width = Mathf.Clamp(value, OutlineInstruction.MIN_WIDTH, OutlineInstruction.MAX_WIDTH);
        }

        public bool IncludeChildren
        {
            get => _includeChildren;
            set
            {
                _includeChildren = value;
                RefreshRenderers();
            }
        }

        internal List<Entry> Entries => _entries;

        private void OnEnable()
        {
            RefreshRenderers();
            OutlineManager.Register(this);
        }

        private void OnDisable()
        {
            OutlineManager.Unregister(this);
        }

        private void OnValidate()
        {
            if (isActiveAndEnabled) RefreshRenderers();
        }

        private void OnTransformChildrenChanged()
        {
            if (_includeChildren) RefreshRenderers();
        }

        /// <summary>Collects the renderers to outline again.</summary>
        public void RefreshRenderers()
        {
            _entries.Clear();

            var renderers = new List<Renderer>();
            if (_includeChildren) GetComponentsInChildren(true, renderers);
            else GetComponents(renderers);

            HashSet<Renderer> lowerLods = CollectLowerLods();

            foreach (Renderer renderer in renderers)
            {
                if (lowerLods != null && lowerLods.Contains(renderer)) continue;

                switch (renderer)
                {
                    case SkinnedMeshRenderer skinned:
                        _entries.Add(new Entry { Renderer = renderer, Skinned = skinned });
                        break;

                    case MeshRenderer _ when renderer.TryGetComponent(out MeshFilter filter):
                        _entries.Add(new Entry { Renderer = renderer, Filter = filter });
                        break;
                }
            }
        }

        // DrawRenderer ignores LOD culling, so only LOD 0 is outlined or every level would stack.
        private HashSet<Renderer> CollectLowerLods()
        {
            LODGroup[] groups = _includeChildren ? GetComponentsInChildren<LODGroup>(true) : GetComponents<LODGroup>();
            if (groups.Length == 0) return null;

            var lowerLods = new HashSet<Renderer>();
            foreach (LODGroup group in groups)
            {
                LOD[] lods = group.GetLODs();
                for (int i = 1; i < lods.Length; i++)
                {
                    foreach (Renderer renderer in lods[i].renderers)
                    {
                        if (renderer != null) lowerLods.Add(renderer);
                    }
                }
            }

            // A renderer shared with LOD 0 stays outlined.
            foreach (LODGroup group in groups)
            {
                LOD[] lods = group.GetLODs();
                if (lods.Length == 0) continue;

                foreach (Renderer renderer in lods[0].renderers)
                {
                    if (renderer != null) lowerLods.Remove(renderer);
                }
            }

            return lowerLods;
        }
    }
}
