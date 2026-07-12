// WebGpuWater - surfable "hero wave": a single parametric breaking wave that rises out of the
// open-water surface, rolls over (attractor-curl overturning lip - the expired Kelly Slater
// technique, US7561993), and remerges as its lifecycle envelope decays.
//
// Two consumers share ONE canonical evaluation (HeroEvaluate), so they can never drift:
//   - BASE offset (heightfield-safe: rise, forward lean, post-break collapse) - applied to EVERY
//     open-water vertex (full plane, window patch, ocean clipmap) so the ocean itself carries the
//     wave and there is never a second flat surface underneath it.
//   - SHEET offset (base + overturning curl) - applied only to the dedicated dense strip mesh the
//     WaterHeroWave component spawns (_IsHeroWave). A heightfield cannot represent an overhang,
//     so the curling lip is explicit geometry; the strip discards every fragment outside the curl
//     region (the base surface already renders there), leaving only the lip sheet.
//
// Everything is a closed-form function of world xz + the published uniforms (no textures, no sim
// state), so a CPU mirror for surfer physics can reproduce it exactly later (buoyancy facade plan).
#ifndef WEBGPUWATER_HERO_WAVE_INCLUDED
#define WEBGPUWATER_HERO_WAVE_INCLUDED

// Per-body uniforms, published via the MaterialPropertyBlock by WaterUniformPublisher from the
// WaterHeroWave component's state. All-zero (the default when no component is active) is inert.
float  _HeroWaveActive;  // 1 while a hero wave drives this body; 0 = every function below is skipped
float4 _HeroWaveFrame;   // xy = crest-line centre (world xz), zw = along-crest unit direction (world xz)
float4 _HeroWaveShape;   // x = amplitude (m, lifecycle envelope baked in), y = face length (m, travel side)
                         // z = back length (m), w = crest half-length (m)
float4 _HeroWaveCurl;    // x = peel position (m along crest), y = peel blend length (m)
                         // z = max roll angle (rad, envelope baked in), w = curl start fraction (0..1)
float4 _HeroWaveCurl2;   // x = pivot ahead fraction (of face length), y = pivot height fraction (of amplitude)
                         // z = forward lean distance (m), w = shoulder start fraction (0..1)
float4 _HeroWaveMotion;  // x = undulation amplitude (fraction), y = undulation wavelength (m)
                         // z = undulation phase (rad, advanced on the CPU)
                         // w = peel direction sign (+1 = breaks from the -u end, -1 = from the +u end)

#define HERO_TWO_PI              6.28318530718
#define HERO_MIN_LENGTH          1e-3  // metre floor under every published length before dividing
// Sheet fragments below this curl weight are discarded: the base ocean surface already renders that
// region, so the strip only ever shows the overturning lip (no coplanar double surface).
#define HERO_SHEET_MIN_WEIGHT    0.02
// The lip sheet fades in as the local wave grows past this height (m). Kills the sheet wherever the
// envelope/shoulder has flattened the wave, so a rising or dying wave never shows a zero-height sheet.
#define HERO_SHEET_MIN_AMPLITUDE 0.25
// Param-space finite-difference step (m) for the sheet's geometric normal. The curl radius is metres,
// so a ~decimetre step stays well inside one feature.
#define HERO_NORMAL_EPSILON      0.12
// Post-break collapse: behind the peel point the broken section sinks toward a whitewater mound.
// Runs over this many peel-blend lengths, and removes this fraction of the height when complete.
// (The foam/whitewater dressing of the collapsed section is the next phase - crest particle plan.)
#define HERO_COLLAPSE_LENGTHS    2.0
#define HERO_COLLAPSE_AMOUNT     0.7
// Lean fades out in proportion as the local wave height drops below this (m): the lean shear is a
// SHAPE term (independent of amplitude), so without this gate a flattened wave - envelope 0,
// shoulders, collapsed sections - would still shear the ocean horizontally along the crest band.
#define HERO_LEAN_REFERENCE_HEIGHT 1.0
// Early-out rectangle: beyond this many profile lengths across the crest every term is ~0
// (sech^2(6) ~ 2e-5), so the whole-ocean base evaluation costs nothing away from the wave.
#define HERO_ACROSS_CUTOFF       6.0
// Clamp on the sech argument: inside the cutoff rectangle a very asymmetric face/back pair can
// still push |d|/L past where cosh overflows to +inf. IEEE degrades gracefully (1/inf = 0) but
// WGSL leaves float overflow implementation-defined, so clamp explicitly (sech(20) ~ 4e-9 = 0).
#define HERO_SECH_ARG_MAX        20.0

// World xz -> crest-local coordinates: x = u along the crest line, y = d across it (+d = travel
// direction). Travel is derived from the along direction exactly like the CPU side
// (WaterHeroWave.TravelFromAlong): along = transform.right => travel = transform.forward.
float2 HeroLocalCoords(float2 worldXZ)
{
    float2 rel = worldXZ - _HeroWaveFrame.xy;
    float2 along = _HeroWaveFrame.zw;
    float2 travel = float2(-along.y, along.x);
    return float2(dot(rel, along), dot(rel, travel));
}

// Canonical scalar terms at crest-local (u, d), shared by the geometry (HeroEvaluate) and the
// whitewater source (HeroWaveFoamSource) so the foam can never drift from the shape it rides.
struct HeroWaveTerms
{
    float amplitude; // local height (m): envelope x shoulder x undulation x collapse
    float profile;   // across-wave sech^2 shape, 1 on the crest line, -> 0 across
    float crestness; // curl-region mask from the profile (0 on the face .. 1 at the crest tip)
    float peel;      // break progress: 0 unbroken face .. 1 at/behind the peel point
    float collapse;  // post-break: 0 at the peel point .. 1 fully collapsed broken section
};

HeroWaveTerms HeroComputeTerms(float2 uv)
{
    HeroWaveTerms t;
    t.amplitude = 0.0;
    t.profile = 0.0;
    t.crestness = 0.0;
    t.peel = 0.0;
    t.collapse = 0.0;

    float u = uv.x;
    float d = uv.y;
    float crestHalf = max(_HeroWaveShape.w, HERO_MIN_LENGTH);

    // Outside the wave's active rectangle every term is ~0 (shoulder ends exactly at the crest
    // half-length; the profile is negligible past the across cutoff) - skip the transcendental
    // work. Continuous: height, lean, weight and foam all reach 0 at these bounds.
    if (abs(u) >= crestHalf
        || abs(d) > HERO_ACROSS_CUTOFF * max(_HeroWaveShape.y, _HeroWaveShape.z))
        return t;

    // Shoulder: height dies smoothly toward the crest ends so the wave merges into the ocean.
    float shoulder = 1.0 - smoothstep(_HeroWaveCurl2.w * crestHalf, crestHalf, abs(u));
    // Liveliness: a slow height undulation travelling along the crest (phase advanced on the CPU).
    float undulation = 1.0 + _HeroWaveMotion.x
        * sin(u * (HERO_TWO_PI / max(_HeroWaveMotion.y, HERO_MIN_LENGTH)) + _HeroWaveMotion.z);

    // Peel: the break travels along the crest. peel saturates at 1 at the peel point; PAST it the
    // unclamped ramp keeps growing and drives the post-break collapse of the broken section.
    // Motion.w flips the peel direction without flipping the frame (which would flip travel too).
    float peelRamp = (_HeroWaveCurl.x - u * _HeroWaveMotion.w) / max(_HeroWaveCurl.y, HERO_MIN_LENGTH);
    t.peel = saturate(peelRamp);
    t.collapse = saturate((peelRamp - 1.0) / HERO_COLLAPSE_LENGTHS);

    t.amplitude = _HeroWaveShape.x * shoulder * undulation
                * (1.0 - t.collapse * HERO_COLLAPSE_AMOUNT);

    // Asymmetric solitary profile: sech^2 with a short steep front (travel side) and a long back.
    float profileLength = (d >= 0.0) ? max(_HeroWaveShape.y, HERO_MIN_LENGTH)
                                     : max(_HeroWaveShape.z, HERO_MIN_LENGTH);
    float sechTerm = 1.0 / cosh(min(abs(d) / profileLength, HERO_SECH_ARG_MAX));
    t.profile = sechTerm * sechTerm;

    // Curl-region mask: the part of the wave above the curl-start fraction of the crest.
    t.crestness = saturate((t.profile - _HeroWaveCurl.w) / max(1.0 - _HeroWaveCurl.w, HERO_MIN_LENGTH));
    return t;
}

// Canonical hero-wave surface evaluation at crest-local (u, d).
//   acrossUp : deformed point relative to the flat surface point, in the (across, up) plane -
//              x = horizontal offset along travel (m), y = height (m)
//   weight   : curl/sheet weight in 0..1 (0 = base surface, 1 = fully rolled lip tip)
//   withCurl : false = heightfield-safe base (every ocean vertex), true = + overturning roll (strip)
void HeroEvaluate(float2 uv, bool withCurl, out float2 acrossUp, out float weight)
{
    float d = uv.y;
    HeroWaveTerms t = HeroComputeTerms(uv);

    // Forward lean (Fournier-Reeves phase advance): the crest TOP shears toward travel, steepening
    // the face. profile^2 biases the shear to the upper part so the trough line stays put; the
    // amplitude gate makes the shear die with the wave (see HERO_LEAN_REFERENCE_HEIGHT).
    float leanShear = _HeroWaveCurl2.z * t.profile * t.profile
                    * saturate(t.amplitude / HERO_LEAN_REFERENCE_HEIGHT);
    float2 q = float2(d + leanShear, t.amplitude * t.profile);

    // Sheet weight: crest region x peel progress, gated by the LOCAL height so a flattened wave
    // (envelope, shoulder, collapse) never grows a lip.
    weight = t.crestness * t.peel * smoothstep(0.0, HERO_SHEET_MIN_AMPLITUDE, t.amplitude)
             * (1.0 - t.collapse);

    if (withCurl && weight > 0.0)
    {
        // Attractor curl: rotate the crest region around a pivot ahead of the face. The roll angle
        // scales with the per-point weight, so the tip travels furthest - a continuous spiral that
        // pitches forward over the pivot and plunges: the overturning lip.
        float theta = _HeroWaveCurl.z * weight;
        float2 pivot = float2(_HeroWaveCurl2.x * max(_HeroWaveShape.y, HERO_MIN_LENGTH),
                              _HeroWaveCurl2.y * t.amplitude);
        float2 rel = q - pivot;
        float sinT = sin(theta);
        float cosT = cos(theta);
        // Clockwise in (across, up): up-vectors tip toward +travel first, then down.
        q = pivot + float2(rel.x * cosT + rel.y * sinT, -rel.x * sinT + rel.y * cosT);
    }

    acrossUp = float2(q.x - d, q.y);
}

// World-space vertex offset for a surface point whose UNDISPLACED world xz is worldXZ.
// Callers gate on _HeroWaveActive.
float3 HeroWaveOffset(float2 worldXZ, bool withCurl, out float weight)
{
    float2 uv = HeroLocalCoords(worldXZ);
    float2 acrossUp;
    HeroEvaluate(uv, withCurl, acrossUp, weight);
    float2 along = _HeroWaveFrame.zw;
    float2 travel = float2(-along.y, along.x);
    return float3(travel.x * acrossUp.x, acrossUp.y, travel.y * acrossUp.x);
}

// Geometric normal of the full (curled) sheet surface, from a param-space finite difference -
// the sheet is multi-valued in world xz (it overhangs), so slopes in xz would be wrong; tangents
// in crest-local (u, d) parameter space stay single-valued through the overturn.
float3 HeroSheetNormal(float2 worldXZ)
{
    float2 uv = HeroLocalCoords(worldXZ);
    float weightUnused;
    float2 p0;
    float2 p1;
    float2 p2;
    HeroEvaluate(uv, true, p0, weightUnused);
    HeroEvaluate(uv + float2(HERO_NORMAL_EPSILON, 0.0), true, p1, weightUnused);
    HeroEvaluate(uv + float2(0.0, HERO_NORMAL_EPSILON), true, p2, weightUnused);
    // Local-frame positions (x = along u, y = up, z = across d + horizontal offset).
    float3 pointAtUv = float3(uv.x,                       p0.y, uv.y + p0.x);
    float3 pointAlongU = float3(uv.x + HERO_NORMAL_EPSILON, p1.y, uv.y + p1.x);
    float3 pointAcrossD = float3(uv.x,                       p2.y, uv.y + HERO_NORMAL_EPSILON + p2.x);
    float3 normalLocal = normalize(cross(pointAcrossD - pointAtUv, pointAlongU - pointAtUv));
    // Local frame -> world (yaw-only orthonormal frame).
    float2 along = _HeroWaveFrame.zw;
    float2 travel = float2(-along.y, along.x);
    return normalize(float3(along.x, 0.0, along.y) * normalLocal.x
                     + float3(0.0, 1.0, 0.0) * normalLocal.y
                     + float3(travel.x, 0.0, travel.y) * normalLocal.z);
}

// Tilt a world-space surface normal by the BASE (heightfield) hero-wave slope at the source xz -
// the fragment counterpart of the base vertex offset, same pattern as ApplyLargeBodyWaveNormal.
// A height gradient g contributes normal.xz = -g. Lean shear is ignored for the slope (small term).
float3 ApplyHeroWaveNormal(float3 worldNormal, float2 sourceXZ, float strength)
{
    float2 uv = HeroLocalCoords(sourceXZ);
    float wIgnored;
    float2 sample0;
    float2 sampleU;
    float2 sampleD;
    HeroEvaluate(uv, false, sample0, wIgnored);
    HeroEvaluate(uv + float2(HERO_NORMAL_EPSILON, 0.0), false, sampleU, wIgnored);
    HeroEvaluate(uv + float2(0.0, HERO_NORMAL_EPSILON), false, sampleD, wIgnored);
    float2 slopeLocal = float2(sampleU.y - sample0.y, sampleD.y - sample0.y) / HERO_NORMAL_EPSILON;
    float2 along = _HeroWaveFrame.zw;
    float2 travel = float2(-along.y, along.x);
    float2 slopeXZ = along * slopeLocal.x + travel * slopeLocal.y;
    return normalize(worldNormal + float3(-slopeXZ.x, 0.0, -slopeXZ.y) * strength);
}

// --- Whitewater source ------------------------------------------------------------------------
// Relative foam weights of the two generation zones. Fixed look constants (the master strength is
// the component's Whitewater Strength): LIP = the actively rolling curl, WASH = the churned mound
// left on the collapsed broken section. sqrt(profile) widens the wash footprint past the crest
// line so whitewash reads as a mound, not a stripe.
#define HERO_FOAM_LIP_GAIN  1.0
#define HERO_FOAM_WASH_GAIN 0.8

// Whitewater generation rate in 0..1 at a world xz (the caller scales by strength and step time).
// Injected into the ripple-sim foam buffer, so it advects, diffuses and decays like all other
// foam - and everything downstream (surface foam, foam particles, density foam) rides it for free.
float HeroWaveFoamSource(float2 worldXZ)
{
    HeroWaveTerms t = HeroComputeTerms(HeroLocalCoords(worldXZ));
    float heightGate = smoothstep(0.0, HERO_SHEET_MIN_AMPLITUDE, t.amplitude);
    float lip = t.crestness * t.peel * (1.0 - t.collapse);
    float wash = sqrt(t.profile) * t.collapse;
    return saturate(lip * HERO_FOAM_LIP_GAIN + wash * HERO_FOAM_WASH_GAIN) * heightGate;
}

#endif // WEBGPUWATER_HERO_WAVE_INCLUDED
