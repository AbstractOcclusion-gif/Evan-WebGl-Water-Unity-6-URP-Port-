// WebGpuWater - shoreline substrate uniforms + sampling helpers (Layer A/B).
//
// The world-frame seabed-depth field and its jump-flood SDF are baked by WaterShoreDepthField and
// published as globals. This header is the single place that declares those uniforms and the helpers
// that read them, so the vertex wave code (shoaling), the fragment (debug, whitewash, swash), and any
// other shader can share one definition. tex2Dlod everywhere so the samplers are valid in the vertex
// stage.
//
// P0 semantics change: _ShoreDepthTex now stores the STILL-WATER COLUMN DEPTH (metres, + = water,
// - = dry land above the level) instead of the seabed's absolute world Y. Half-float precision is
// spent on the small numbers near the waterline - where every consumer needs it - instead of on a
// large absolute height, which banded the shallows (depth = level - seabed subtracted two similar
// big numbers). Every reader (this file, WaterShoreSwe.compute, the surface debug) uses the new
// semantics; _ShoreWaterLevel remains published for consumers that need the absolute plane.
#ifndef WEBGPUWATER_SHORE_INCLUDED
#define WEBGPUWATER_SHORE_INCLUDED

// Depth field: R = still-water column depth (m), + in water / - on dry land. SDF field: RG =
// toward-shore direction (0..1), B = signed distance to shore (m, + in water / - on land), A = mask.
// Both share one world frame.
sampler2D _ShoreDepthTex;
float4 _ShoreDepthCenter; // world XZ centre of the field (.xy)
float4 _ShoreDepthSize;   // world XZ half-extent of the field (.xy)
float _ShoreDepthValid;   // 1 = a seabed field is baked
float _ShoreDepthDebug;   // 1 = visualize seabed depth on the surface (debug only)
float _ShoreWaterLevel;   // still-water plane world Y used when the field was baked
float _ShoreShoalDepth;   // depth (m) over which waves shoal; full strength beyond it (0 = no shoaling)
sampler2D _ShoreSDFTex;   // RG = toward-shore dir (0..1), B = signed distance (m), A = mask
float _ShoreSDFValid;     // 1 = a shoreline SDF is baked
float _ShoreSDFDebug;     // 1 = visualize the SDF on the surface (debug only)
// PER-BODY gate on this shared substrate (published via the property block, default 0): only the
// body that opted into bed depth consumes the shore field, so another body overlapping the field's
// rectangle (a pond next to the terrain lake) can never catch its shoal/surf/swash.
float _ShoreBodyGate;

// P1 shoal-transform knobs (published by WaterShoreDepthField.Publish from the body settings).
float _ShoreRefraction;   // 0..1: how hard shoaling waves bend toward the shore (crests align to beach)
float _ShoreCompression;  // phase-compression gain near shore (crests bunch as waves slow)
float _ShoreGreens;       // Green's-law amplification cap (1 = off; shoaling waves GROW before dying)

// Deep-water sentinel: a depth this large attenuates nothing (used off-field / when no field is baked).
#define SHORE_DEEP_SENTINEL 1e9
// Outer UV fraction of the field over which shore influence feathers to zero, so the field's
// rectangular border never prints a seam into the swell (B5 in the coastline audit).
#define SHORE_BORDER_FEATHER 0.08

// Everything the wave/foam/swash code needs from the shore substrate at one world xz, from ONE
// depth fetch + ONE sdf fetch. 'influence' is the feathered in-field weight: every shore effect
// multiplies by it, so leaving the field is always seamless.
struct ShoreData
{
    float depth;      // still-water column depth (m); DEEP sentinel off-field / unbaked
    float sdfDist;    // signed distance to shore (m, + in water); 0 off-field / unbaked
    float2 toShore;   // unit direction toward the waterline ((0,0) off-field / unbaked)
    float influence;  // 1 fully inside the field .. 0 at/outside the feathered border
};

// World XZ -> shore-field UV. The field is axis-aligned in world space.
float2 ShoreFieldUV(float2 worldXZ)
{
    return (worldXZ - _ShoreDepthCenter.xy) / (2.0 * _ShoreDepthSize.xy) + 0.5;
}

// Feathered in-field weight: 1 well inside, ramping to 0 across the outer border band and staying
// 0 outside (saturate of a negative edge distance). Product of the two axes keeps corners smooth.
float ShoreFieldInfluence(float2 uv)
{
    float2 edge = min(uv, 1.0 - uv);                     // distance to the closest border, per axis
    float2 ramp = saturate(edge / SHORE_BORDER_FEATHER); // 0 at/off the border, 1 inside the band
    return ramp.x * ramp.y;
}

// The inert (deep open-water) sample: one definition, so every early-out and every caller's
// default is provably fully-initialized for the compiler's definite-assignment analysis.
ShoreData ShoreDataInert()
{
    ShoreData s;
    s.depth = SHORE_DEEP_SENTINEL;
    s.sdfDist = 0.0;
    s.toShore = float2(0.0, 0.0);
    s.influence = 0.0;
    return s;
}

// One-stop shore fetch. Off-field or unbaked returns the inert ShoreData (deep, no direction,
// zero influence) so every consumer's math collapses to open-water behaviour with no branches.
ShoreData ShoreSample(float2 worldXZ)
{
    ShoreData s = ShoreDataInert();
    if (_ShoreDepthValid < 0.5 || _ShoreBodyGate < 0.5) return s;

    float2 uv = ShoreFieldUV(worldXZ);
    float influence = ShoreFieldInfluence(uv);
    if (influence <= 0.0) return s;

    s.influence = influence;
    s.depth = tex2Dlod(_ShoreDepthTex, float4(saturate(uv), 0, 0)).r;
    if (_ShoreSDFValid > 0.5)
    {
        float4 sdf = tex2Dlod(_ShoreSDFTex, float4(saturate(uv), 0, 0));
        float2 dir = sdf.rg * 2.0 - 1.0;
        float len = length(dir);
        s.toShore = len > 1e-4 ? dir / len : float2(0.0, 0.0);
        s.sdfDist = sdf.b;
    }
    return s;
}

// Still-water column depth (metres) under a world xz - kept for consumers that only need depth.
// Returns a deep sentinel where no field is baked or the point is outside it, so those places
// never shoal.
float ShoreShoalDepth(float2 worldXZ)
{
    if (_ShoreDepthValid < 0.5 || _ShoreBodyGate < 0.5) return SHORE_DEEP_SENTINEL;
    float2 uv = ShoreFieldUV(worldXZ);
    if (ShoreFieldInfluence(uv) <= 0.0) return SHORE_DEEP_SENTINEL;
    return tex2Dlod(_ShoreDepthTex, float4(saturate(uv), 0, 0)).r;
}

// Depth-based shoaling weight for one wave component: 0 at the waterline, ramping to 1 within the
// near-shore band. Short waves recover shallower than long ones (Crest's saturate(2*depth/L)). The
// _ShoreShoalDepth clamp forces full strength past that depth so ONLY the near-shore band
// attenuates - and (P0 fix, B3 in the audit) blends in over a smoothstep instead of a hard lerp
// ramp, so there is no derivative kink / visible wall at exactly the band depth. Negative depth
// (dry land) clamps to 0, so waves vanish rather than punch through the seabed. _ShoreShoalDepth
// of 0 (or unpublished) disables shoaling entirely (weight 1 everywhere).
float ShoalWeight(float depth, float wavelength)
{
    float clamped = max(depth, 0.0);
    float raw = saturate(2.0 * clamped / max(wavelength, 1e-3));
    float band = max(_ShoreShoalDepth, 1e-3);
    float deep = smoothstep(0.35 * band, band, clamped); // smooth hand-over to full strength
    return lerp(raw, 1.0, deep);
}

// Green's-law shoaling gain: waves GROW as the water column shrinks (a ~ depth^-1/4), clamped by
// the _ShoreGreens art cap so the growth never runs away, and faded right at the waterline where
// the breaking/whitewash layer takes over. 1 offshore / off-field: pure amplification-only term.
float ShoreGreenGain(ShoreData shore)
{
    float band = max(_ShoreShoalDepth, 1e-3);
    if (shore.influence <= 0.0 || shore.depth >= band) return 1.0;
    float d = max(shore.depth, 0.05);
    float green = min(pow(band / d, 0.25), max(_ShoreGreens, 1.0));
    // Fade the gain out over the last stretch to the waterline: attenuation (ShoalWeight) wins the
    // final metres, so amplified waves hand over to the surf/whitewash layer instead of spiking.
    green = lerp(green, 1.0, saturate(1.0 - shore.depth / (0.35 * band)));
    return lerp(1.0, green, shore.influence);
}

// Extra phase distance from near-shore compression: waves slow down in shallow water, so crests
// bunch together. Implemented as a smooth warp of the shore-distance field (monotonic for gains
// up to ~2, so crests never fold back), added to a component's plane phase scaled by its own
// shoaling response - long waves feel the bottom (and compress) sooner than short ones.
float ShoreWarpExtra(ShoreData shore)
{
    if (shore.influence <= 0.0 || _ShoreCompression <= 0.0) return 0.0;
    float s = max(shore.sdfDist, 0.0);
    float reach = max(4.0 * _ShoreShoalDepth, 8.0); // compression reach scales with the shoal band
    return _ShoreCompression * s * exp(-s / reach) * shore.influence;
}

#endif // WEBGPUWATER_SHORE_INCLUDED
