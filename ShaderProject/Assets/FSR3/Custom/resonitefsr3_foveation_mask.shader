// ResoniteFSR3 foveation depth mask: writes the near-plane depth into masked pixels so the scene's geometry fails the
// depth test there and those pixels are skipped (G-buffer, lighting, skybox, transparents).
// The depth comes from the vertex position, not SV_Depth: writing SV_Depth disables the GPU's hierarchical-Z / depth
// compression, which costs more than the mask saves.
// Pass 0: drawn from a camera command buffer (BeforeGBuffer) as a full-screen triangle over the double-wide target.
// Pass 1: "Deferred" pass for an in-scene full-screen renderer drawn first in the G-buffer pass (queue 1500).
Shader "Hidden/ResoniteFSR3/FoveationMask"
{
    SubShader
    {
        Tags { "Queue" = "Geometry-500" "RenderType" = "Opaque" "IgnoreProjector" = "True" }

        CGINCLUDE
        #include "UnityCG.cginc"
        #include "resonitefsr3_foveation.hlsl"

        float4 FoveaTargetSize;   // x = double-wide width, y = height, z = single eye width
        float FoveaMaskDepth;     // 1 with reversed Z (near plane), 0 otherwise

        struct VertexOutput
        {
            float4 position : SV_POSITION;
        };

        // Full-screen triangle from the vertex id; positions are in clip space so the current viewport is covered
        VertexOutput FullScreenVertex(uint vertexId : SV_VertexID)
        {
            VertexOutput output;
            float2 corner = float2((vertexId << 1) & 2, vertexId & 2);
            output.position = float4(corner * 2.0 - 1.0, FoveaMaskDepth, 1.0);
            return output;
        }

        void MaskFragment(VertexOutput input)
        {
            int2 targetPixel = int2(input.position.xy);
            int eyeWidth = (int)FoveaTargetSize.z;
            int eyeIndex = targetPixel.x >= eyeWidth ? 1 : 0;
            int2 eyePixel = int2(targetPixel.x - eyeIndex * eyeWidth, targetPixel.y);
            // Memory-row coordinates, identical to the eye-split compute shader, so both agree on which pixels are masked
            if (!FoveaIsMasked(eyePixel, float2(eyeWidth, FoveaTargetSize.y), eyeIndex))
                discard;
        }
        ENDCG

        Pass
        {
            Name "CommandBufferMask"
            Cull Off ZWrite On ZTest Always ColorMask 0
            CGPROGRAM
            #pragma vertex FullScreenVertex
            #pragma fragment MaskFragment
            #pragma target 5.0
            ENDCG
        }

        Pass
        {
            Name "DeferredMask"
            Tags { "LightMode" = "Deferred" }
            Cull Off ZWrite On ZTest Always ColorMask 0
            CGPROGRAM
            #pragma vertex FullScreenVertex
            #pragma fragment MaskFragment
            #pragma target 5.0
            ENDCG
        }
    }
}
