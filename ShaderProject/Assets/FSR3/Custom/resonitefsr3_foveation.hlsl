// ResoniteFSR3 graded foveation mask, shared by the depth-mask shader and the eye-split compute shader.
//
// Works on whole 2x2 pixel quads (GPUs shade in 2x2 quads, so skipping single pixels inside a quad saves nothing).
// Within each 2x2 block of quads (4x4 pixels), a number of quads are skipped depending on the distance from the gaze:
//   inside InnerRadius: keep 4/4   |   to MidRadius: keep 3/4   |   to OuterRadius: keep 2/4   |   beyond: keep 1/4
// The skipped quads rotate every frame (FoveaFrame), so over 2-4 frames every pixel is sampled and FSR's history can
// accumulate full detail.
#ifndef RESONITEFSR3_FOVEATION_INCLUDED
#define RESONITEFSR3_FOVEATION_INCLUDED

float4 FoveaCenterLeft;   // xy = gaze point in eye UV (0..1, bottom-up like Unity render textures)
float4 FoveaCenterRight;
float4 FoveaRadii;        // x = inner, y = mid, z = outer (in eye-height UV units), w = eye aspect (width / height)
int FoveaFrame;

// Returns the number of pixels kept per 2x2 quad at this position (1..4)
int FoveaKeepPerQuad(float2 eyeUv, int eyeIndex)
{
    float2 center = eyeIndex == 0 ? FoveaCenterLeft.xy : FoveaCenterRight.xy;
    float2 offset = eyeUv - center;
    offset.x *= FoveaRadii.w;
    float distanceFromGaze = length(offset);
    if (distanceFromGaze < FoveaRadii.x) return 4;
    if (distanceFromGaze < FoveaRadii.y) return 3;
    if (distanceFromGaze < FoveaRadii.z) return 2;
    return 1;
}

// True if this pixel is skipped (masked out) this frame. All 4 pixels of a quad always get the same answer.
bool FoveaIsMasked(int2 eyePixel, float2 eyeSize, int eyeIndex)
{
    int2 quad = eyePixel >> 1;
    float2 quadCenterUv = (float2(quad) * 2.0 + 1.0) / eyeSize;
    int keep = FoveaKeepPerQuad(quadCenterUv, eyeIndex);
    if (keep >= 4)
        return false;

    int quadIndex = (quad.x & 1) + 2 * (quad.y & 1);
    int rotated = (quadIndex + FoveaFrame) & 3;
    if (keep == 3) return rotated == 0;                    // skip 1 of 4
    if (keep == 2) return rotated == 1 || rotated == 2;    // skip a diagonal pair
    return rotated != 0;                                   // keep 1 of 4
}

#endif
