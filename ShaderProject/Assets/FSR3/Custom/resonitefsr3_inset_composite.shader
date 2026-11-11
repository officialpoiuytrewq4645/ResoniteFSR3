// ResoniteFSR3 quad-views composite: blends the full-resolution gaze inset (rendered by a separate camera that looks
// along the gaze direction) over the upscaled eye image.
// The inset camera shares the eye's position but is rotated, so its image maps onto the eye image through a perspective
// (homography) transform. Drawing the inset's four corners with their clip-space w makes the GPU's perspective-correct
// interpolation do that mapping exactly. Corner positions are given in the target's memory layout (row 0 = first row
// in memory), so no viewport or projection conventions are involved.
Shader "Hidden/ResoniteFSR3/InsetComposite"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "IgnoreProjector" = "True" }

        Pass
        {
            Name "InsetComposite"
            Cull Off ZWrite Off ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            ColorMask RGB

            CGPROGRAM
            #pragma vertex InsetVertex
            #pragma fragment InsetFragment
            #pragma target 5.0
            #include "UnityCG.cginc"

            Texture2D<float4> _InsetTex;
            SamplerState sampler_linear_clamp;
            // Corners in order (-1,-1), (1,-1), (-1,1), (1,1) of the inset: x, y = position in target memory pixels,
            // z = clip-space w (perspective), w unused
            float4 _CornerPixel[4];
            float4 _InsetTarget;   // x, y = target size in pixels; z = non-zero: read the inset vertically flipped; w = non-zero: draw a debug border
            float _InsetFeather;   // width of the soft edge, as a fraction of the inset radius

            struct VertexOutput
            {
                float4 position : SV_POSITION;
                float2 insetUv : TEXCOORD0;
            };

            VertexOutput InsetVertex(uint vertexId : SV_VertexID)
            {
                static const uint cornerOfVertex[6] = { 0, 1, 2, 2, 1, 3 };
                uint corner = cornerOfVertex[vertexId];
                float4 cornerPixel = _CornerPixel[corner];
                float w = max(cornerPixel.z, 1e-4);
                float2 ndc = float2(cornerPixel.x / _InsetTarget.x * 2.0 - 1.0, 1.0 - cornerPixel.y / _InsetTarget.y * 2.0);

                VertexOutput output;
                output.position = float4(ndc * w, 0.5 * w, w);
                output.insetUv = float2(corner & 1, corner >> 1);
                return output;
            }

            float4 InsetFragment(VertexOutput input) : SV_Target
            {
                float2 insetUv = input.insetUv;
                float distanceFromCenter = length(insetUv * 2.0 - 1.0);

                float2 sampleUv = insetUv;
                if (_InsetTarget.z != 0)
                    sampleUv.y = 1.0 - sampleUv.y;
                float3 color = saturate(_InsetTex.SampleLevel(sampler_linear_clamp, sampleUv, 0).rgb);

                float alpha = 1.0 - smoothstep(1.0 - _InsetFeather, 1.0, distanceFromCenter);
                if (_InsetTarget.w != 0 && distanceFromCenter > 0.97 && distanceFromCenter <= 1.0)
                    return float4(1, 0, 0, 1);
                return float4(color, alpha);
            }
            ENDCG
        }
    }
}
