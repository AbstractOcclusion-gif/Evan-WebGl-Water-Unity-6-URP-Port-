// WebGpuWater - water exclusion volumes (dry interiors), Phase 1: analytic OBBs.
// Declares the global exclusion uniforms plus the ONE point test every water consumer
// shares (reuse-never-rewrite: consumers include this file, nobody hand-copies the loop).
// Kept OUT of WaterShared.hlsl on purpose: that header's contract is pure math with no
// global declarations, and these ARE globals.
//
// Published by WaterUniformPublisher.PublishSharedGlobals (global, not per body: a dry
// room is dry in whichever body intersects it). _ExclusionWorldToBox maps world space
// into each volume's UNIT box, so one matrix carries centre + rotation + size and the
// inside test is abs(local) <= 0.5 per axis.
//
// Phase 2 adds ExclusionRayLength(origin, dir, maxDist) here (slab test per box via
// WaterShared's IntersectCube) when the fog/god-ray consumers arrive - not shipped
// early, so this header carries no dead code.
#ifndef WEBGL_WATER_EXCLUSION_INCLUDED
#define WEBGL_WATER_EXCLUSION_INCLUDED

// C# pair: WaterExclusionVolume.MaxVolumes (WaterWaveConstantsValidator guards the pair).
#define EXCLUSION_MAX_VOLUMES 4

// Half-extent of the unit box the world->box matrices map into.
#define EXCLUSION_BOX_HALF_EXTENT 0.5

float    _ExclusionCount; // active volumes (float so it binds like _WaveCount); 0 disables
float4x4 _ExclusionWorldToBox[EXCLUSION_MAX_VOLUMES];

// True when world-space worldPos lies inside any active exclusion volume. The trip count
// and matrices are uniforms and no texture is sampled inside, so the loop itself keeps
// uniform control flow - only the boolean RESULT is per-fragment (the caller's discard
// demotes the invocation, which keeps feeding neighbour derivatives; the WGSL contract).
// With zero volumes the loop body never runs: the zero-cost off state.
bool InsideExclusion(float3 worldPos)
{
    int count = (int)_ExclusionCount;
    [loop]
    for (int i = 0; i < count; i++)
    {
        float3 boxLocal = mul(_ExclusionWorldToBox[i], float4(worldPos, 1.0)).xyz;
        if (all(abs(boxLocal) <= EXCLUSION_BOX_HALF_EXTENT)) return true;
    }
    return false;
}

#endif // WEBGL_WATER_EXCLUSION_INCLUDED
