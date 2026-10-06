using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace SilhouetteOutline
{
    /// <summary>
    /// Silhouette, blur, cut and merge. The silhouettes are depth tested against a copy of the scene depth, so only
    /// the visible part of an outlined object produces an outline.
    /// The blur radius is the outline width, so outlines are grouped by width and each group runs its own chain.
    /// With occlusion on, the silhouette depth is dilated along with the blur and the cut hides the outline wherever
    /// the scene is in front of it.
    /// </summary>
    internal sealed class SilhouetteOutlinePass : ScriptableRenderPass
    {
        // Pass indices of Hidden/SilhouetteOutline/Composite.
        private const int PASS_COPY_DEPTH = 0;
        private const int PASS_BLUR_VERTICAL = 1;
        private const int PASS_BLUR_HORIZONTAL = 2;
        private const int PASS_CUT = 3;
        private const int PASS_MERGE = 4;

        private const string OCCLUSION_KEYWORD = "_SILHOUETTE_OUTLINE_OCCLUSION";

        // DrawMeshInstanced limit.
        private const int MAX_INSTANCES_PER_DRAW = 1023;

        private static readonly int KernelSizeId = Shader.PropertyToID("_KernelSize");
        private static readonly int OcclusionToleranceId = Shader.PropertyToID("_OcclusionTolerance");
        private static readonly int MaskId = Shader.PropertyToID("_SilhouetteOutlineMask");
        private static readonly int DepthId = Shader.PropertyToID("_SilhouetteOutlineDepth");
        private static readonly int SceneDepthId = Shader.PropertyToID("_SilhouetteOutlineSceneDepth");
        private static readonly int ColorsId = Shader.PropertyToID("_SilhouetteOutlineColors");
        private static readonly int ColorOffsetId = Shader.PropertyToID("_SilhouetteOutlineColorOffset");

        private static readonly Vector4 ScaleBias = new Vector4(1f, 1f, 0f, 0f);

        private struct DrawBatch
        {
            public Mesh Mesh;
            public Matrix4x4[] Matrices;
            public Renderer Renderer;
            public int ColorOffset;
        }

        private class SilhouettePassData
        {
            internal List<DrawBatch> Batches;
            internal Material Material;
            internal GraphicsBuffer Colors;
            internal Matrix4x4[] Scratch;
            internal float DepthBias;
            internal float SlopeDepthBias;
        }

        private class BlitPassData
        {
            internal TextureHandle Source;
            internal TextureHandle Mask;
            internal TextureHandle Depth;
            internal TextureHandle SceneDepth;
            internal float KernelSize;
            internal Material Material;
            internal int Pass;
        }

        // Extra inputs and outputs of a full screen pass, all optional.
        private struct BlitTargets
        {
            public TextureHandle Mask;
            public TextureHandle Depth;
            public TextureHandle SceneDepth;
            public TextureHandle DepthDestination;
            public float KernelSize;
        }

        private readonly Material _silhouetteMaterial;
        private readonly Material _compositeMaterial;

        // One batch list per distinct width, plus every batch for the shared mask when there are several widths.
        private readonly List<float> _widths = new List<float>(4);
        private readonly List<List<DrawBatch>> _groups = new List<List<DrawBatch>>(4);
        private readonly List<DrawBatch> _allBatches = new List<DrawBatch>(64);
        private readonly List<Vector4> _colors = new List<Vector4>(128);
        private readonly Matrix4x4[] _scratch = new Matrix4x4[MAX_INSTANCES_PER_DRAW];

        private GraphicsBuffer _colorBuffer;
        private SilhouetteOutlineFeature.OutlineSettings _settings;

        public SilhouetteOutlinePass(Material silhouetteMaterial, Material compositeMaterial)
        {
            _silhouetteMaterial = silhouetteMaterial;
            _compositeMaterial = compositeMaterial;

            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public void Setup(SilhouetteOutlineFeature.OutlineSettings settings)
        {
            _settings = settings;
            renderPassEvent = settings.RenderPassEvent;

            CoreUtils.SetKeyword(_silhouetteMaterial, OCCLUSION_KEYWORD, settings.OccludeOutline);
            CoreUtils.SetKeyword(_compositeMaterial, OCCLUSION_KEYWORD, settings.OccludeOutline);
        }

        public void Dispose()
        {
            _colorBuffer?.Release();
            _colorBuffer = null;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (!CollectBatches()) return;

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            TextureHandle sceneDepth = resourceData.cameraDepthTexture;
            TextureHandle cameraColor = resourceData.activeColorTexture;
            if (!sceneDepth.IsValid() || !cameraColor.IsValid()) return;

            UploadColors();

            // Built from the camera target so XR keeps its texture array and eye count.
            var colorDesc = new TextureDesc(cameraData.cameraTargetDescriptor)
            {
                msaaSamples = MSAASamples.None,
                bindTextureMS = false,
                useMipMap = false,
                autoGenerateMips = false,
                enableRandomWrite = false,
                isShadowMap = false,
                memoryless = RenderTextureMemoryless.None,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                format = SystemInfo.GetGraphicsFormat(DefaultFormat.LDR),
                clearBuffer = false,
            };

            var depthDesc = colorDesc;
            depthDesc.format = GetDepthFormat();
            depthDesc.name = "SilhouetteOutline_Depth";
            TextureHandle depth = renderGraph.CreateTexture(depthDesc);

            bool occlude = _settings.OccludeOutline;
            var silhouetteDepthDesc = colorDesc;
            silhouetteDepthDesc.format = GetSilhouetteDepthFormat();

            _compositeMaterial.SetFloat(OcclusionToleranceId, Mathf.Max(_settings.OcclusionTolerance, 0.001f));

            AddCopyDepthPass(renderGraph, sceneDepth, depth);

            // With several widths the cut uses a mask of every outlined object, so no ring is drawn over another one.
            TextureHandle sharedMask = TextureHandle.nullHandle;
            if (_widths.Count > 1)
            {
                sharedMask = CreateMask(renderGraph, colorDesc, "SilhouetteOutline_SharedMask");
                TextureHandle sharedDepthMask = occlude ? CreateSilhouetteDepth(renderGraph, silhouetteDepthDesc, "SilhouetteOutline_SharedDepthMask") : TextureHandle.nullHandle;
                AddSilhouettePass(renderGraph, _allBatches, sharedMask, sharedDepthMask, depth);
            }

            for (int group = 0; group < _widths.Count; group++)
            {
                float width = _widths[group];

                TextureHandle mask = CreateMask(renderGraph, colorDesc, "SilhouetteOutline_Mask");
                TextureHandle blurA = CreateTexture(renderGraph, colorDesc, "SilhouetteOutline_BlurA");
                TextureHandle blurB = CreateTexture(renderGraph, colorDesc, "SilhouetteOutline_BlurB");

                TextureHandle depthMask = TextureHandle.nullHandle;
                TextureHandle depthA = TextureHandle.nullHandle;
                TextureHandle depthB = TextureHandle.nullHandle;
                if (occlude)
                {
                    depthMask = CreateSilhouetteDepth(renderGraph, silhouetteDepthDesc, "SilhouetteOutline_DepthMask");
                    depthA = CreateTexture(renderGraph, silhouetteDepthDesc, "SilhouetteOutline_DepthA");
                    depthB = CreateTexture(renderGraph, silhouetteDepthDesc, "SilhouetteOutline_DepthB");
                }

                AddSilhouettePass(renderGraph, _groups[group], mask, depthMask, depth);

                AddBlitPass(renderGraph, "SilhouetteOutline_BlurVertical", mask, blurA, PASS_BLUR_VERTICAL,
                    new BlitTargets { Depth = depthMask, DepthDestination = depthA, KernelSize = width });

                AddBlitPass(renderGraph, "SilhouetteOutline_BlurHorizontal", blurA, blurB, PASS_BLUR_HORIZONTAL,
                    new BlitTargets { Depth = depthA, DepthDestination = depthB, KernelSize = width });

                AddBlitPass(renderGraph, "SilhouetteOutline_Cut", blurB, blurA, PASS_CUT,
                    new BlitTargets
                    {
                        Mask = sharedMask.IsValid() ? sharedMask : mask,
                        Depth = depthB,
                        SceneDepth = occlude ? sceneDepth : TextureHandle.nullHandle,
                    });

                AddBlitPass(renderGraph, "SilhouetteOutline_Merge", blurA, cameraColor, PASS_MERGE, default);
            }
        }

        private static TextureHandle CreateTexture(RenderGraph renderGraph, TextureDesc desc, string name)
        {
            desc.name = name;
            desc.clearBuffer = false;
            return renderGraph.CreateTexture(desc);
        }

        private static TextureHandle CreateMask(RenderGraph renderGraph, TextureDesc desc, string name)
        {
            desc.name = name;
            desc.clearBuffer = true;
            desc.clearColor = Color.clear;
            return renderGraph.CreateTexture(desc);
        }

        // Raw device depth of the silhouettes, cleared to the far plane where there is none.
        private static TextureHandle CreateSilhouetteDepth(RenderGraph renderGraph, TextureDesc desc, string name)
        {
            float far = SystemInfo.usesReversedZBuffer ? 0f : 1f;

            desc.name = name;
            desc.clearBuffer = true;
            desc.clearColor = new Color(far, far, far, far);
            return renderGraph.CreateTexture(desc);
        }

        private bool CollectBatches()
        {
            foreach (List<DrawBatch> group in _groups)
            {
                group.Clear();
            }

            _widths.Clear();
            _allBatches.Clear();
            _colors.Clear();

            foreach (OutlineInstruction instruction in OutlineManager.PersistentInstructions)
            {
                AddInstruction(instruction);
            }

            foreach (OutlineInstruction instruction in OutlineManager.FrameInstructions)
            {
                AddInstruction(instruction);
            }

            foreach (URPOutline outline in OutlineManager.Targets)
            {
                if (outline == null) continue;

                int colorOffset = -1;
                foreach (URPOutline.Entry entry in outline.Entries)
                {
                    if (!entry.IsDrawable || entry.Mesh == null) continue;

                    if (colorOffset < 0)
                    {
                        colorOffset = _colors.Count;
                        _colors.Add(outline.Color);
                    }

                    AddBatch(OutlineInstruction.ResolveWidth(outline.Width),
                        new DrawBatch { Mesh = entry.Mesh, Renderer = entry.Renderer, ColorOffset = colorOffset });
                }
            }

            return _allBatches.Count > 0;
        }

        private void AddInstruction(OutlineInstruction instruction)
        {
            if (!instruction.IsDrawable) return;

            int colorOffset = _colors.Count;
            for (int i = 0; i < instruction.Matrices.Length; i++)
            {
                _colors.Add(instruction.GetColor(i));
            }

            AddBatch(instruction.ResolvedWidth,
                new DrawBatch { Mesh = instruction.Mesh, Matrices = instruction.Matrices, ColorOffset = colorOffset });
        }

        private void AddBatch(float width, DrawBatch batch)
        {
            int group = _widths.IndexOf(width);
            if (group < 0)
            {
                group = _widths.Count;
                _widths.Add(width);
                if (_groups.Count <= group) _groups.Add(new List<DrawBatch>(16));
            }

            _groups[group].Add(batch);
            _allBatches.Add(batch);
        }

        // Every camera of a frame collects the same data, so overwriting the buffer between cameras is harmless.
        private void UploadColors()
        {
            if (_colorBuffer == null || _colorBuffer.count < _colors.Count)
            {
                _colorBuffer?.Release();
                _colorBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.NextPowerOfTwo(Mathf.Max(_colors.Count, 128)), sizeof(float) * 4);
            }

            _colorBuffer.SetData(_colors);
        }

        private void AddCopyDepthPass(RenderGraph renderGraph, TextureHandle sceneDepth, TextureHandle depth)
        {
            using (var builder = renderGraph.AddRasterRenderPass("SilhouetteOutline_CopyDepth", out BlitPassData passData))
            {
                passData.Source = sceneDepth;
                passData.Material = _compositeMaterial;
                passData.Pass = PASS_COPY_DEPTH;

                builder.UseTexture(sceneDepth);
                builder.SetRenderAttachmentDepth(depth, AccessFlags.Write);
                builder.SetRenderFunc(static (BlitPassData data, RasterGraphContext context) =>
                    Blitter.BlitTexture(context.cmd, data.Source, ScaleBias, data.Material, data.Pass));
            }
        }

        private void AddSilhouettePass(RenderGraph renderGraph, List<DrawBatch> batches, TextureHandle mask, TextureHandle depthMask, TextureHandle depth)
        {
            using (var builder = renderGraph.AddRasterRenderPass("SilhouetteOutline_Silhouette", out SilhouettePassData passData))
            {
                passData.Batches = batches;
                passData.Material = _silhouetteMaterial;
                passData.Colors = _colorBuffer;
                passData.Scratch = _scratch;
                passData.DepthBias = _settings.DepthBias;
                passData.SlopeDepthBias = _settings.SlopeDepthBias;

                builder.SetRenderAttachment(mask, 0, AccessFlags.Write);
                if (depthMask.IsValid()) builder.SetRenderAttachment(depthMask, 1, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(depth, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (SilhouettePassData data, RasterGraphContext context) => DrawSilhouettes(data, context.cmd));
            }
        }

        private void AddBlitPass(RenderGraph renderGraph, string name, TextureHandle source, TextureHandle destination, int pass, BlitTargets targets)
        {
            using (var builder = renderGraph.AddRasterRenderPass(name, out BlitPassData passData))
            {
                passData.Source = source;
                passData.Mask = targets.Mask;
                passData.Depth = targets.Depth;
                passData.SceneDepth = targets.SceneDepth;
                passData.KernelSize = targets.KernelSize;
                passData.Material = _compositeMaterial;
                passData.Pass = pass;

                builder.UseTexture(source);
                if (passData.Mask.IsValid()) builder.UseTexture(passData.Mask);
                if (passData.Depth.IsValid()) builder.UseTexture(passData.Depth);
                if (passData.SceneDepth.IsValid()) builder.UseTexture(passData.SceneDepth);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                if (targets.DepthDestination.IsValid()) builder.SetRenderAttachment(targets.DepthDestination, 1, AccessFlags.Write);

                builder.SetRenderFunc(static (BlitPassData data, RasterGraphContext context) =>
                {
                    // Set through the command buffer, not the material, so each group, camera and eye keeps its own values.
                    if (data.KernelSize > 0f) context.cmd.SetGlobalFloat(KernelSizeId, data.KernelSize);
                    if (data.Mask.IsValid()) context.cmd.SetGlobalTexture(MaskId, data.Mask);
                    if (data.Depth.IsValid()) context.cmd.SetGlobalTexture(DepthId, data.Depth);
                    if (data.SceneDepth.IsValid()) context.cmd.SetGlobalTexture(SceneDepthId, data.SceneDepth);

                    Blitter.BlitTexture(context.cmd, data.Source, ScaleBias, data.Material, data.Pass);
                });
            }
        }

        private static void DrawSilhouettes(SilhouettePassData data, RasterCommandBuffer cmd)
        {
            cmd.SetGlobalBuffer(ColorsId, data.Colors);

            // CPU composed matrices are not bit exact with the scene depth, LEqual needs some slack.
            cmd.SetGlobalDepthBias(data.DepthBias, data.SlopeDepthBias);

            foreach (DrawBatch batch in data.Batches)
            {
                int subMeshCount = batch.Mesh.subMeshCount;

                if (batch.Renderer != null)
                {
                    cmd.SetGlobalFloat(ColorOffsetId, batch.ColorOffset);
                    for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
                    {
                        cmd.DrawRenderer(batch.Renderer, data.Material, subMesh, 0);
                    }

                    continue;
                }

                Matrix4x4[] matrices = batch.Matrices;
                for (int start = 0; start < matrices.Length; start += MAX_INSTANCES_PER_DRAW)
                {
                    int count = Mathf.Min(MAX_INSTANCES_PER_DRAW, matrices.Length - start);

                    // The command buffer copies the matrices, so the scratch array can be reused right away.
                    Matrix4x4[] chunk = matrices;
                    if (start > 0)
                    {
                        System.Array.Copy(matrices, start, data.Scratch, 0, count);
                        chunk = data.Scratch;
                    }

                    cmd.SetGlobalFloat(ColorOffsetId, batch.ColorOffset + start);
                    for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
                    {
                        cmd.DrawMeshInstanced(batch.Mesh, subMesh, data.Material, 0, chunk, count);
                    }
                }
            }

            cmd.SetGlobalDepthBias(0f, 0f);
        }

        // D32 matches the scene depth texture so the copy round trip stays exact.
        private static GraphicsFormat GetDepthFormat()
        {
            return SystemInfo.IsFormatSupported(GraphicsFormat.D32_SFloat, GraphicsFormatUsage.Render)
                ? GraphicsFormat.D32_SFloat
                : CoreUtils.GetDefaultDepthOnlyFormat();
        }

        // Raw device depth needs 32 bits, half precision collapses everything past a few meters.
        private static GraphicsFormat GetSilhouetteDepthFormat()
        {
            return SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat, GraphicsFormatUsage.Render)
                ? GraphicsFormat.R32_SFloat
                : GraphicsFormat.R16_SFloat;
        }
    }
}
