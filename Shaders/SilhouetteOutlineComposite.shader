// Full screen passes of the silhouette outline. Pass order must match the PASS_ constants of SilhouetteOutlinePass.
// All passes go through the core Blit.hlsl vertex shader and TEXTURE2D_X, so they work with XR texture arrays.
Shader "Hidden/SilhouetteOutline/Composite"
{
    HLSLINCLUDE
    #pragma target 3.5

    // The copy pass reads raw depth through _BlitTexture.
    #define USE_FULL_PRECISION_BLIT_TEXTURE

    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

    float _KernelSize;
    float _OcclusionTolerance;

    TEXTURE2D_X(_SilhouetteOutlineMask);
    TEXTURE2D_X_FLOAT(_SilhouetteOutlineDepth);
    TEXTURE2D_X_FLOAT(_SilhouetteOutlineSceneDepth);

    #if UNITY_REVERSED_Z
        #define SILHOUETTE_OUTLINE_FAR_DEPTH 0.0
        #define NEAREST_DEPTH(a, b) max(a, b)
    #else
        #define SILHOUETTE_OUTLINE_FAR_DEPTH 1.0
        #define NEAREST_DEPTH(a, b) min(a, b)
    #endif

    half GaussianWeight(int x, float sigma)
    {
        return exp(-(x * x) / (2.0 * sigma * sigma));
    }

    struct BlurOutput
    {
        half4 color : SV_Target0;
        #if defined(_SILHOUETTE_OUTLINE_OCCLUSION)
            float depth : SV_Target1;
        #endif
    };

    struct BlurState
    {
        half sum;
        half weightSum;
        half3 maxColor;
        half maxColorValue;
        float nearestDepth;
    };

    // Inputs are premultiplied (the silhouette mask is (color, 1) or (0, 0), the vertical pass output is premultiplied),
    // so the filtered color divided by the filtered alpha is the pure color. A mix of two colors is never longer than
    // the longest one, so the max still picks a pure color.
    void AccumulateTap(inout BlurState state, float2 uv, half weight)
    {
        half4 value = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0);
        state.sum += value.a * weight;
        state.weightSum += weight;

        if (value.a <= 0.0001) return;

        half3 color = saturate(value.rgb / value.a);
        half intensity = length(color);
        if (intensity > state.maxColorValue)
        {
            state.maxColor = color;
            state.maxColorValue = intensity;
        }
    }

    // The nearest depth is not a weighted sum, so each texel is still read on its own.
    void AccumulateDepth(inout BlurState state, float2 uv)
    {
        #if defined(_SILHOUETTE_OUTLINE_OCCLUSION)
            float depth = SAMPLE_TEXTURE2D_X_LOD(_SilhouetteOutlineDepth, sampler_PointClamp, uv, 0).r;
            state.nearestDepth = NEAREST_DEPTH(state.nearestDepth, depth);
        #endif
    }

    // Gaussian blur of the alpha along one axis, keeping the brightest color under the kernel.
    // Texels i and i + 1 are merged into one bilinear fetch, placed between them by their weights.
    // With occlusion, also dilates the silhouette depth, keeping the nearest one.
    BlurOutput BlurMaxed(Varyings input, float2 axis, bool premultiplyOutput)
    {
        BlurState state;
        state.sum = 0;
        state.weightSum = 0;
        state.maxColor = 0;
        state.maxColorValue = 0;
        state.nearestDepth = SILHOUETTE_OUTLINE_FAR_DEPTH;

        int radius = (int)_KernelSize;
        float sigma = _KernelSize * 0.5;
        float2 texel = axis * _BlitTexture_TexelSize.xy;
        float2 uv = input.texcoord;

        AccumulateTap(state, uv, GaussianWeight(0, sigma));
        AccumulateDepth(state, uv);

        [loop]
        for (int i = 1; i <= radius; i += 2)
        {
            bool hasPair = i + 1 <= radius;
            half w1 = GaussianWeight(i, sigma);
            half w2 = hasPair ? GaussianWeight(i + 1, sigma) : 0;
            half w = w1 + w2;
            float2 offset = texel * (i + w2 / w);

            AccumulateTap(state, uv + offset, w);
            AccumulateTap(state, uv - offset, w);

            AccumulateDepth(state, uv + texel * i);
            AccumulateDepth(state, uv - texel * i);
            if (hasPair)
            {
                AccumulateDepth(state, uv + texel * (i + 1));
                AccumulateDepth(state, uv - texel * (i + 1));
            }
        }

        half blurredAlpha = state.sum / state.weightSum;

        BlurOutput output;
        // The vertical pass is premultiplied so the horizontal pass can recover the color from its own bilinear fetches.
        output.color = half4(premultiplyOutput ? state.maxColor * blurredAlpha : state.maxColor, blurredAlpha);
        #if defined(_SILHOUETTE_OUTLINE_OCCLUSION)
            output.depth = state.nearestDepth;
        #endif
        return output;
    }

    // Distance from the camera plane in world units, for both projections.
    float RawDepthToEyeDepth(float rawDepth)
    {
        if (unity_OrthoParams.w > 0.5)
        {
            #if UNITY_REVERSED_Z
                rawDepth = 1.0 - rawDepth;
            #endif
            return lerp(_ProjectionParams.y, _ProjectionParams.z, rawDepth);
        }

        return LinearEyeDepth(rawDepth, _ZBufferParams);
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Cull Off
        ZWrite Off
        ZTest Always

        // 0: copy of the scene depth into the silhouette depth buffer.
        Pass
        {
            Name "CopyDepth"

            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            float Frag(Varyings input) : SV_Depth
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                return SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, input.texcoord, 0).r;
            }
            ENDHLSL
        }

        // 1: vertical blur.
        Pass
        {
            Name "BlurVerticalMaxed"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_local _ _SILHOUETTE_OUTLINE_OCCLUSION

            BlurOutput Frag(Varyings input)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                return BlurMaxed(input, float2(0, 1), true);
            }
            ENDHLSL
        }

        // 2: horizontal blur.
        Pass
        {
            Name "BlurHorizontalMaxed"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_local _ _SILHOUETTE_OUTLINE_OCCLUSION

            BlurOutput Frag(Varyings input)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                return BlurMaxed(input, float2(1, 0), false);
            }
            ENDHLSL
        }

        // 3: removes the silhouette from the blurred mask, leaving only the outline ring.
        Pass
        {
            Name "Cut"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_local _ _SILHOUETTE_OUTLINE_OCCLUSION

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 blurred = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, input.texcoord, 0);

                // The darkest silhouette sample of the 3x3 neighbourhood: erodes the mask by one pixel so the
                // outline slightly overlaps the object edge.
                half4 silhouette = 0;
                half minIntensity = 9999;

                [unroll]
                for (int y = -1; y <= 1; y++)
                {
                    [unroll]
                    for (int x = -1; x <= 1; x++)
                    {
                        float2 offset = float2(x, y) * _BlitTexture_TexelSize.xy;
                        half4 value = SAMPLE_TEXTURE2D_X_LOD(_SilhouetteOutlineMask, sampler_LinearClamp, input.texcoord + offset, 0);

                        half intensity = length(value.rgb);
                        if (intensity < minIntensity)
                        {
                            minIntensity = intensity;
                            silhouette = value;
                        }
                    }
                }

                // Saturated before pow: inside the silhouette the difference is negative and pow would return NaN.
                half ring = pow(saturate(blurred.a - silhouette.a), 0.75);

                #if defined(_SILHOUETTE_OUTLINE_OCCLUSION)
                    // Fades the ring where the scene is in front of the nearest silhouette, past the tolerance.
                    float outlineDepth = RawDepthToEyeDepth(SAMPLE_TEXTURE2D_X_LOD(_SilhouetteOutlineDepth, sampler_PointClamp, input.texcoord, 0).r);
                    float sceneDepth = RawDepthToEyeDepth(SAMPLE_TEXTURE2D_X_LOD(_SilhouetteOutlineSceneDepth, sampler_PointClamp, input.texcoord, 0).r);
                    ring *= 1.0 - saturate((outlineDepth - sceneDepth - _OcclusionTolerance) / _OcclusionTolerance);
                #endif

                half3 color = saturate(blurred.rgb) * ring;

                return half4(color, 1);
            }
            ENDHLSL
        }

        // 4: premultiplied blend of the outline over the camera color.
        Pass
        {
            Name "Merge"

            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 outline = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, input.texcoord, 0);
                half alpha = max(outline.r, max(outline.g, outline.b));

                return half4(outline.rgb, alpha);
            }
            ENDHLSL
        }
    }
}
