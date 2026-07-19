// WebGpuWater - the displaced water surface height (the wavy waterline), shared.
// ONE source of truth for "where is the surface at this world xz": the rest plane through
// the volume transform (extent.y + rotation exact) + the wind-wave layer + the open-water
// swell/FFT. Split out of WaterUnderwaterFog.shader (verbatim move - any behaviour change
// here is a bug) so the exclusion wall clips at the SAME surface the fog integrates
// against: the wall's flat rest-plane clip left an empty band between the wall top and a
// wave crest on partially submerged volumes. Both wave layers are analytic (no texture
// samples), so fragment-stage use costs ALU only.
#ifndef WEBGPUWATER_WATERLINE_INCLUDED
#define WEBGPUWATER_WATERLINE_INCLUDED

#include "WaterVolume.hlsl"     // WorldToPool / PoolToWorld + _VolumeCenter (rest plane)
#include "WaterWaves.hlsl"      // WaveHeight: wind-wave layer (+ _WaveTime for the swell below)
#include "WaterLargeWaves.hlsl" // LargeBodyWaveHeight: open-water swell/FFT; needs _WaveTime (above)

float _OceanWorldWaves; // 1 = sample wind waves in WORLD metres (ocean); 0 = pool xz (pond)

#define WAVE_METERS_MIN 1e-3 // matches WindWaveSampleXZ's guard in WaterSurface.shader

// Displaced world-space surface height at a WORLD xz: the single source of truth for the wavy
// waterline. Rest plane (via the volume transform, so extent.y + rotation are exact, matching
// TryGetAnalyticWaterline) + wind-wave layer + open-water swell/FFT. Pools: the swell is a no-op
// (_LargeBody = 0), so this reduces to the wind-wave surface over the flat pool top.
float SurfaceHeightAtXZ(float2 worldXZ)
{
    // Map to pool xz at the rest plane; the surface shader samples the wind waves off this xz.
    float3 poolAtRest = WorldToPool(float3(worldXZ.x, _VolumeCenter.y, worldXZ.y));
    float2 poolXZ = poolAtRest.xz;

    // Oceans sample the wind waves in WORLD metres (extent-independent) to match WindWaveSampleXZ.
    float2 windSampleXZ = (_OceanWorldWaves > 0.5) ? (worldXZ / max(_WaveMetersPerUnit, WAVE_METERS_MIN))
                                                   : poolXZ;
    // Wind-wave height is authored in pool units; lift it to world through the full transform,
    // exactly as the vertex path does (PoolToWorld of the displaced pool point).
    float surfaceY = PoolToWorld(float3(poolXZ.x, WaveHeight(windSampleXZ), poolXZ.y)).y;

    // Open-water swell/FFT is authored in WORLD metres and layered on top (no-op for pools).
    if (_LargeBody > 0.5) surfaceY += LargeBodyWaveHeight(worldXZ);
    return surfaceY;
}

// Signed height of a world point above its local displaced surface (>0 in air, <=0 underwater).
float SurfaceSignedGap(float3 world)
{
    return world.y - SurfaceHeightAtXZ(world.xz);
}

#endif // WEBGPUWATER_WATERLINE_INCLUDED
