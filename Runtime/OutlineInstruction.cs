using UnityEngine;

namespace SilhouetteOutline
{
    /// <summary>
    /// One mesh drawn as an outlined silhouette, instanced once per matrix.
    /// </summary>
    public struct OutlineInstruction
    {
        public Mesh Mesh;

        /// <summary>Local to world matrix of each instance.</summary>
        public Matrix4x4[] Matrices;

        /// <summary>One color per instance, or a single color shared by every instance.</summary>
        public Color[] Colors;

        public OutlineInstruction(Mesh mesh, Matrix4x4 matrix, Color color)
        {
            Mesh = mesh;
            Matrices = new[] { matrix };
            Colors = new[] { color };
        }

        public OutlineInstruction(Mesh mesh, Matrix4x4[] matrices, Color color)
        {
            Mesh = mesh;
            Matrices = matrices;
            Colors = new[] { color };
        }

        public OutlineInstruction(Mesh mesh, Matrix4x4[] matrices, Color[] colors)
        {
            Mesh = mesh;
            Matrices = matrices;
            Colors = colors;
        }

        internal bool IsDrawable => Mesh != null && Matrices != null && Matrices.Length > 0 && Colors != null && Colors.Length > 0;

        internal Color GetColor(int instance) => Colors[Colors.Length == 1 ? 0 : Mathf.Min(instance, Colors.Length - 1)];
    }
}
