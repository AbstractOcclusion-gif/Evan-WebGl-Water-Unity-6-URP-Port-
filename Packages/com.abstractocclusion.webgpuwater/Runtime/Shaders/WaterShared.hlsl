// WebGL Water - shared constants & pure helpers (Unity 6 / URP port)
// Backend-agnostic: ONLY #defines, static consts and pure math here (no sampler or
// global declarations), so both the legacy-CG shaders and the URP HLSL shaders can
// include it without clashing. Faithful to Evan Wallace's renderer.js (MIT).
#ifndef WEBGL_WATER_SHARED_INCLUDED
#define WEBGL_WATER_SHARED_INCLUDED

#define IOR_AIR   1.0
#define IOR_WATER 1.333

#define POOL_HEIGHT     1.0          // pool floor sits at y = -POOL_HEIGHT
#define POOL_RIM_HEIGHT (2.0 / 12.0) // top of the pool walls, in pool units

// Pool interior as an axis-aligned box in pool space, used by every analytic ray march
// (surface refraction, caustics, underwater fog). Floor at -POOL_HEIGHT; the top gives
// headroom above the surface so upward rays don't clip the waterline.
#define POOL_BOX_TOP 2.0
#define POOL_BOX_MIN float3(-1.0, -POOL_HEIGHT, -1.0)
#define POOL_BOX_MAX float3(1.0, POOL_BOX_TOP, 1.0)

#define CAUSTIC_PROJECTION_SCALE 0.75 // fits the projected caustic map into the pool footprint

// Rim-shadow sigmoid shaping (softens the pool-wall shadow edge in the caustic/wall passes).
#define RIM_SHADOW_SHARPNESS 200.0
#define RIM_SHADOW_SPREAD    10.0

// Caustic projection divides by the refracted light's downward component; keep it away
// from zero so a near-horizontal sun can't blow the projection up to infinity. The
// refracted light points DOWN (negative y is carried by the callers' conventions), so
// clamp the magnitude, preserving sign.
#define MIN_REFRACTED_LIGHT_Y 0.05

// Slab intersection of a ray with an axis-aligned box; returns (tNear, tFar).
float2 IntersectCube(float3 origin, float3 ray, float3 cubeMin, float3 cubeMax)
{
    float3 tMin = (cubeMin - origin) / ray;
    float3 tMax = (cubeMax - origin) / ray;
    float3 t1 = min(tMin, tMax);
    float3 t2 = max(tMin, tMax);
    float tNear = max(max(t1.x, t1.y), t1.z);
    float tFar  = min(min(t2.x, t2.y), t2.z);
    return float2(tNear, tFar);
}

// Signed clamp away from zero for the caustic-projection divides.
float SafeRefractedLightY(float y)
{
    return sign(y) * max(abs(y), MIN_REFRACTED_LIGHT_Y);
}

// Project a pool-space point down the refracted light onto the caustic map's UV.
float2 ProjectCausticUV(float3 poolPos, float3 refractedLight)
{
    return CAUSTIC_PROJECTION_SCALE
           * (poolPos.xz - poolPos.y * refractedLight.xz / SafeRefractedLightY(refractedLight.y))
           * 0.5 + 0.5;
}

#endif // WEBGL_WATER_SHARED_INCLUDED
