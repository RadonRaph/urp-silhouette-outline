// Flat color silhouette of the outlined meshes. The color comes from a structured buffer indexed by the instance,
// so one instanced draw can carry a different color per instance.
Shader "Hidden/SilhouetteOutline/Silhouette"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Silhouette"

            Cull Off
            ZWrite On
            ZTest LEqual
            Blend Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ _SILHOUETTE_OUTLINE_OCCLUSION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            StructuredBuffer<float4> _SilhouetteOutlineColors;
            float _SilhouetteOutlineColorOffset;

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                nointerpolation uint colorIndex : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);

                // With single pass instanced XR the eye is folded into the instance id, unity_InstanceID is already divided back.
                uint instance = 0;
                #if UNITY_ANY_INSTANCING_ENABLED
                    instance = unity_InstanceID;
                #endif
                output.colorIndex = (uint)_SilhouetteOutlineColorOffset + instance;

                return output;
            }

            struct FragOutput
            {
                half4 color : SV_Target0;
                #if defined(_SILHOUETTE_OUTLINE_OCCLUSION)
                    // Raw device depth, compared with the scene depth texture at the end of the chain.
                    float depth : SV_Target1;
                #endif
            };

            // Read in the fragment stage: some mobile GPUs expose no storage buffer to the vertex stage.
            FragOutput Frag(Varyings input)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                FragOutput output;
                output.color = half4(_SilhouetteOutlineColors[input.colorIndex].rgb, 1.0);
                #if defined(_SILHOUETTE_OUTLINE_OCCLUSION)
                    output.depth = input.positionCS.z;
                #endif
                return output;
            }
            ENDHLSL
        }
    }
}
