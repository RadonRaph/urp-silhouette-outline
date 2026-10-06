using UnityEngine;

namespace SilhouetteOutline
{
    /// <summary>
    /// One mesh drawn as an outlined silhouette, instanced once per matrix.
    /// </summary>
    public struct OutlineInstruction
    {
        public const float MIN_WIDTH = 1f;
        public const float MAX_WIDTH = 32f;
        public const float DEFAULT_WIDTH = 4f;

        public Mesh Mesh;

        /// <summary>Local to world matrix of each instance.</summary>
        public Matrix4x4[] Matrices;

        /// <summary>One color per instance, or a single color shared by every instance.</summary>
        public Color[] Colors;

        /// <summary>Outline width in pixels. 0 uses <see cref="DEFAULT_WIDTH"/>.</summary>
        public float Width;

        public OutlineInstruction(Mesh mesh, Matrix4x4 matrix, Color color, float width = DEFAULT_WIDTH)
        {
            Mesh = mesh;
            Matrices = new[] { matrix };
            Colors = new[] { color };
            Width = width;
        }

        public OutlineInstruction(Mesh mesh, Matrix4x4[] matrices, Color color, float width = DEFAULT_WIDTH)
        {
            Mesh = mesh;
            Matrices = matrices;
            Colors = new[] { color };
            Width = width;
        }

        public OutlineInstruction(Mesh mesh, Matrix4x4[] matrices, Color[] colors, float width = DEFAULT_WIDTH)
        {
            Mesh = mesh;
            Matrices = matrices;
            Colors = colors;
            Width = width;
        }

        internal bool IsDrawable => Mesh != null && Matrices != null && Matrices.Length > 0 && Colors != null && Colors.Length > 0;

        internal float ResolvedWidth => ResolveWidth(Width);

        internal Color GetColor(int instance) => Colors[Colors.Length == 1 ? 0 : Mathf.Min(instance, Colors.Length - 1)];

        internal static float ResolveWidth(float width) => width > 0f ? Mathf.Clamp(width, MIN_WIDTH, MAX_WIDTH) : DEFAULT_WIDTH;
    }
}
