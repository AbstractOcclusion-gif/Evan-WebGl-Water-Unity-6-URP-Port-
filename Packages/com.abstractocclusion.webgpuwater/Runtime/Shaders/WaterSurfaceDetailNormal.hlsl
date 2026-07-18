// WaterSurface pass: Crest-style crossing scrolling detail normals.
// Split out of WaterSurface.shader (SHADER-SPLIT-2) as VERBATIM moves - any
// behavior change here is a bug. The tex2D taps use IMPLICIT derivatives, so
// DetailNormalTilt may only ever be called from UNIFORM control flow (the
// caller's strength/underwater gates) - see the WGSL note on the function.
#ifndef WATER_SURFACE_DETAIL_NORMAL_INCLUDED
#define WATER_SURFACE_DETAIL_NORMAL_INCLUDED

// Crest-style crossing detail normals: the two fixed crossing directions are Crest's
// own (non-orthogonal, non-axis-aligned, so the two scrolls never read as a grid).
// The far layer runs at a bigger tile and half the scroll so the layers never sync;
// it crossfades in over [BLEND_START, BLEND_START+BLEND_RANGE] metres and the whole
// effect fades out over [FADE_START, FADE_START+FADE_RANGE] metres (beyond that the
// distance-grown roughness carries the look and per-pixel detail would only shimmer).
#define DETAIL_NORMAL_DIR0            float2(0.94, 0.34)
#define DETAIL_NORMAL_DIR1            float2(-0.85, -0.53)
#define DETAIL_NORMAL_FAR_TILE_MULT   2.0
#define DETAIL_NORMAL_FAR_SPEED_MULT  0.5
#define DETAIL_NORMAL_FAR_BLEND_START 30.0
#define DETAIL_NORMAL_FAR_BLEND_RANGE 90.0
#define DETAIL_NORMAL_FADE_START      250.0
#define DETAIL_NORMAL_FADE_RANGE      350.0

sampler2D _DetailNormalTex; // tiling water normals; default "bump" = flat = feature inert
float _DetailNormalStrength, _DetailNormalScale, _DetailNormalSpeed;

// ---- Crest-style detail normal: two CROSSING, SCROLLING samples of a tiling normal
// map at two world scales, crossfaded by camera distance (see DETAIL_NORMAL_*).
// Returns an xz slope tilt for the world normal. All four taps always run - the
// distance fade is a multiply, not a branch, because a per-pixel branch around
// tex2D's implicit derivatives is undefined on WGSL; the caller's gate (strength
// knob + above-water) is uniform, which IS branch-safe. ----
float2 DetailNormalTilt(float2 worldXZ, float viewDist)
{
    float scrollTime = _DetailNormalSpeed * _WaveTime;
    float2 scroll0 = DETAIL_NORMAL_DIR0 * scrollTime;
    float2 scroll1 = DETAIL_NORMAL_DIR1 * scrollTime;

    float2 tiltNear =
          UnpackNormal(tex2D(_DetailNormalTex, (worldXZ + scroll0) / _DetailNormalScale)).xy
        + UnpackNormal(tex2D(_DetailNormalTex, (worldXZ + scroll1) / _DetailNormalScale)).xy;

    float farTile = _DetailNormalScale * DETAIL_NORMAL_FAR_TILE_MULT;
    float2 tiltFar =
          UnpackNormal(tex2D(_DetailNormalTex,
              (worldXZ + scroll0 * DETAIL_NORMAL_FAR_SPEED_MULT) / farTile)).xy
        + UnpackNormal(tex2D(_DetailNormalTex,
              (worldXZ + scroll1 * DETAIL_NORMAL_FAR_SPEED_MULT) / farTile)).xy;

    float farBlend = saturate((viewDist - DETAIL_NORMAL_FAR_BLEND_START)
                              / DETAIL_NORMAL_FAR_BLEND_RANGE);
    float fade = 1.0 - saturate((viewDist - DETAIL_NORMAL_FADE_START)
                                / DETAIL_NORMAL_FADE_RANGE);
    return lerp(tiltNear, tiltFar, farBlend) * fade;
}

#endif // WATER_SURFACE_DETAIL_NORMAL_INCLUDED
