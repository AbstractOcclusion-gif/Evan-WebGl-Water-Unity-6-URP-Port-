// WebGpuWater - surf breaker wavefronts (Layer C-analytic, "P2" of the coastline plan).
//
// Periodic breaking wave fronts whose phase is a function of SHORE DISTANCE + time, so crests are
// shore-parallel on any coastline shape by construction (the HFW / KWS1 / Kelly-Slater family of
// techniques - authored shore waves, not simulation). Each front runs a depth-driven lifecycle:
// grow (Green's law) -> steepen/lean -> break where H exceeds the depth criterion -> collapse into
// a whitewash bore that keeps running shoreward -> hand over to the swash at the waterline.
//
// PURE CLOSED-FORM MATH: every function is a function of (inputs, time) only - no textures, no sim
// state. The hosts sample the Layer A shore field themselves and pass depth/sdf/direction in:
//   - WaterLargeWaves.hlsl (surface vertex + fragment) via WaterShore.hlsl's ShoreData,
//   - WaterSim.compute (foam injection) via its own Texture2D fetches,
//   - LargeWaveField.cs (CPU buoyancy mirror) - kept BYTE-FOR-BYTE with SurfFrontHeight below.
// That makes the layer WebGPU-safe (no readback anywhere) and exactly CPU-mirrorable.
#ifndef WEBGPUWATER_SURF_WAVES_INCLUDED
#define WEBGPUWATER_SURF_WAVES_INCLUDED

// Published as globals for the surface (WaterShoreDepthField.Publish) and set explicitly on the
// ripple-sim compute (WaterSimulation.BindShoreFoam). All-zero (unpublished) is inert.
float _SurfActive;        // 1 = the breaker-front layer runs on this body (bed depth + SDF baked)
float _SurfAmplitude;     // deep-water set-wave amplitude (m) feeding the fronts
float _SurfWavelength;    // front spacing offshore (m)
float _SurfPeriod;        // seconds between fronts arriving at a fixed point
float _SurfBandDepth;     // column depth (m) at which fronts are fully developed (fade in deeper)
float _SurfSetStrength;   // 0..1 amplitude variation between wave sets (waves come in sets)
float _SurfLean;          // forward-lean shear (fraction of local height thrown shoreward at the crest)
float _SurfCompression;   // front-spacing compression toward the waterline (crests bunch as they slow)
float _SurfGreens;        // Green's-law growth cap for the fronts (1 = no growth)
float _SurfAmbientFade;   // 0..1 how much the ambient swell/FFT fades where fronts own the surface
float _SurfSwashAmplitude;// run-up height (m) of the swash film above the still waterline
float _SurfWaterlineFoam; // standing lace hugging the waterline (fills the last metres to the sand)
float _SurfCrestLength;   // alongshore length scale (m) of crest segments (finite crests, not bands)
float _SurfCrestVariation;// 0..1 how deeply the crest noise modulates amplitude (0 = endless bands)
float _SurfDirectionality;// 0..1 gate surf by shore exposure to the swell (lee side goes calm)
float4 _SurfWindDirXZ;    // xy = (cos, sin) of the swell/wind heading (the wave travel direction)
// Dedicated surf-foam LOOK controls (decoupled from BOTH the ripple/pond foam sliders and the
// ocean whitecap sliders - tuning either must never restyle the surf whitewash). The surf renders
// through the ocean-whitecap pipeline (same texture + contrast law: whitewash IS whitecap foam)
// but with these knobs. Consumed by the surface fragment only; published with the _Surf* globals.
float _SurfFoamStrength;  // coverage scale of the whitewash/geometry foam layer
float _SurfFoamFeather;   // dissolve softness at the coverage threshold (0 = hard-edged lace)
float _SurfFoamTileSize;  // metres per foam-pattern tile on the surf
float4 _SurfFoamColor;    // rgb tint, a = master opacity

#define SURF_TWO_PI            6.28318530718
// H/d breaking criterion (McCowan's solitary-wave limit ~0.78): a front whose height exceeds this
// fraction of the local column depth cannot stand and collapses into a bore.
#define SURF_BREAK_RATIO       0.78
#define SURF_MIN_DEPTH         0.05  // metre floor under every depth divide
#define SURF_FACE_FRACTION     0.10  // steep shoreward face length, as a fraction of front spacing
#define SURF_BACK_FRACTION     0.24  // long offshore back length, as a fraction of front spacing
#define SURF_BORE_HEIGHT_KEEP  0.6   // height fraction a broken front keeps as the whitewash bore
#define SURF_SET_WAVES         5.0   // pseudo-period (in fronts) of the set envelope
#define SURF_NEAR_FADE         0.55  // fraction of _SurfBandDepth where fronts are fully developed
#define SURF_SECH_ARG_MAX      20.0  // cosh overflow clamp (WGSL float overflow is impl-defined)
#define SURF_SLOPE_EPSILON     0.5   // metres, finite-difference step for the front slope
// Swash timing: fraction of the front period spent on the quick uprush (the rest is the slower
// backwash), and how much of the run-up height stays glistening wet through one full cycle.
#define SURF_SWASH_UPRUSH      0.30
#define SURF_SWASH_WET_FLOOR   0.45
// The swash film rides this far (m) proud of the sand, so the film/glaze fragments WIN the depth
// test against the opaque beach (a flat plane under the terrain would be entirely occluded).
#define SURF_FILM_THICKNESS    0.03

// Matches LbwHash in WaterLargeWaves.hlsl / Hash in LargeWaveField.cs (same constants, so the CPU
// mirror stays byte-for-byte).
float SurfHash(float n)
{
    return frac(sin(n * 12.9898) * 43758.5453);
}

// Per-front set amplitude: a slow sine over SURF_SET_WAVES fronts (waves arrive in sets) plus a
// per-front hash jitter. _SurfSetStrength 0 = every front identical.
float SurfSetAmp(float frontIndex)
{
    float h = SurfHash(frontIndex);
    float setWave = 0.5 + 0.5 * sin((frontIndex / SURF_SET_WAVES) * SURF_TWO_PI + h * 2.4);
    return lerp(1.0, lerp(0.35, 1.0, setWave), _SurfSetStrength) * lerp(0.9, 1.1, h);
}

// Shore-distance warp: compresses front spacing toward the waterline (waves slow down, crests
// bunch). Monotonic for gains up to ~2 so fronts never fold back on themselves.
float SurfWarpDistance(float s)
{
    float reach = 2.0 * max(_SurfWavelength, 1.0);
    return s * (1.0 + _SurfCompression * exp(-max(s, 0.0) / reach));
}

// Alongshore crest modulation: a slow world-space noise (two rotated sine octaves - cheap, smooth,
// non-repeating at shore scale), seeded per front so segment gaps never align between consecutive
// fronts. Crests are locally shore-parallel, so world-position noise naturally varies ALONG the
// crest - long bands break into finite crest segments with calm water between them. Returns an
// amplitude factor in [1 - variation, 1]; where it dips, the front stays under the breaking
// criterion and produces no bore/foam at all - the gaps read as real lulls, not faded foam.
float SurfCrestFactor(float2 worldXZ, float frontIndex)
{
    if (_SurfCrestVariation <= 0.0) return 1.0;
    float invLen = 1.0 / max(_SurfCrestLength, 4.0);
    float seed = SurfHash(frontIndex) * 37.0;
    float n = sin(dot(worldXZ, float2(1.0, 0.31)) * (SURF_TWO_PI * invLen) + seed)
            + 0.5 * sin(dot(worldXZ, float2(-0.42, 1.0)) * (SURF_TWO_PI * invLen * 1.7) + seed * 1.3);
    float n01 = saturate(n / 1.5 * 0.5 + 0.5);
    return 1.0 - _SurfCrestVariation * (1.0 - n01);
}

// Shore-exposure gate: surf only pounds coasts that FACE the swell. The soft negative lower edge
// lets waves wrap a little past the tangent point (cheap stand-in for diffraction) instead of
// cutting off knife-sharp at 90 degrees. 1 everywhere when _SurfDirectionality = 0.
float SurfExposure(float2 toShore)
{
    float facing = smoothstep(-0.25, 0.5, dot(_SurfWindDirXZ.xy, toShore));
    return lerp(1.0, facing, saturate(_SurfDirectionality));
}

// Core front shape at a warped shore distance + local depth. Returns (height m, whitewash,
// breaker) as a plain float3 - NO out parameters: FXC's inliner is fragile around out-params in
// deeply nested calls (the editor's shader-compiler process died on the first build of this file),
// and a value return is also simply cleaner. This is THE canonical evaluation - the CPU mirror
// (LargeWaveField.SurfFrontHeight) reproduces exactly the .x math.
//   .y whitewash: 0..1 broken-bore coverage (foam fuel, trails behind the moving front)
//   .z breaker:   0..1 "cresting/about to break" signal (thin line at the lip - foam + SSS fuel)
float3 SurfFrontHeight(float2 worldXZ, float sWarp, float depth, float time)
{
    float L = max(_SurfWavelength, 1.0);
    float T = max(_SurfPeriod, 0.5);
    // Phase grows with time at fixed distance, so an iso-phase crest moves TOWARD the shore
    // (smaller s) as time advances; speed drops where the warp has compressed the spacing.
    float phase = sWarp / L + time / T;
    float frontIndex = floor(phase);
    float f = phase - frontIndex;              // 0..1 across the front cell, crest at 0.5

    // Set envelope (in time) x crest segmentation (alongshore): both fold into the amplitude, so
    // the breaking criterion, the bore, the whitewash and the crest glow all follow them for free.
    float setAmp = SurfSetAmp(frontIndex) * SurfCrestFactor(worldXZ, frontIndex);
    float d = max(depth, SURF_MIN_DEPTH);

    // Local height: Green's-law growth toward the shore, capped by the breaking criterion.
    float green = min(pow(max(_SurfBandDepth, d) / d, 0.25), max(_SurfGreens, 1.0));
    float H = _SurfAmplitude * setAmp * green;
    float capH = SURF_BREAK_RATIO * d;
    float overCap = H / max(capH, 1e-3);
    float cresting = smoothstep(0.75, 1.05, overCap); // cresting: approaching/at the limit
    float broken = smoothstep(1.05, 1.5, overCap);    // fully broken -> whitewash bore
    // (Later hand-over than v1: the cresting face stays tall further in, so the wave is
    // still VISIBLY a wave when it arrives instead of collapsing to a flat foam smear.)
    H = min(H, capH);

    // Asymmetric solitary profile across the front (sech^2, Fournier-Reeves family): crest at
    // f = 0.5, SHORT steep face on the shoreward side (f < 0.5 = smaller s), long offshore back.
    // The lean shear throws the crest top shoreward as it steepens (phase-advance forward lean).
    float dAcross = (f - 0.5) * L;                   // metres from the crest, + = offshore side
    float lean = _SurfLean * H * cresting;           // lean grows as the front approaches breaking
    dAcross += lean * exp(-abs(dAcross) / (0.25 * L));
    float faceLen = SURF_FACE_FRACTION * L;
    float backLen = SURF_BACK_FRACTION * L;
    float profLen = (dAcross < 0.0) ? faceLen : backLen;
    float sechTerm = 1.0 / cosh(min(abs(dAcross) / profLen, SURF_SECH_ARG_MAX));
    float profile = sechTerm * sechTerm;

    // Broken front: collapse toward a LOWER, WIDER whitewash bore (rounded step of churned water
    // that keeps running shoreward). sech (not sech^2) at 1.4x the back length reads as the mound.
    float boreSech = 1.0 / cosh(min(abs(dAcross) / (backLen * 1.4), SURF_SECH_ARG_MAX));
    float height = H * lerp(profile, boreSech * SURF_BORE_HEIGHT_KEEP, broken);

    // Whitewash: rides the bore and TRAILS OFFSHORE behind the shoreward-moving front (the churned
    // water is left behind as the front travels on; the shoreward side gets its foam from the sim
    // injection + waterline lace instead). NARROW on purpose: each front's foam footprint must be
    // clearly smaller than the compressed front spacing (~L/(1+c)), or neighbouring bores' foam
    // overlaps into one solid static-looking carpet and the march toward shore becomes invisible -
    // exactly the "big slow band" failure. Gated by the set amplitude so lulls stay clean.
    float trail = (dAcross > 0.0) ? exp(-dAcross / backLen) : 0.0;
    float whitewash = broken * max(boreSech, 0.4 * trail) * saturate(setAmp);
    // Thin cresting line right at the lip while the front is breaking (not yet fully broken).
    float breaker = cresting * (1.0 - broken) * profile;

    return float3(height, whitewash, breaker);
}

// Everything the surface / foam / CPU mirror needs from the front layer at one world xz.
struct SurfWaveSample
{
    float height;     // metres added to the surface (0 outside the surf band)
    float2 slopeXZ;   // d(height)/d(world xz) - drives the normal
    float whitewash;  // 0..1 whitewash coverage (broken bore + trail)
    float breaker;    // 0..1 cresting-lip signal (foam + subsurface glow fuel)
    float mask;       // 0..1 where the front layer owns the surface (ambient-fade weight)
};

// The inert (all-zero) sample: one definition, so every early-out and every caller's default is
// provably fully-initialized for the compiler's definite-assignment analysis.
SurfWaveSample SurfWaveSampleInert()
{
    SurfWaveSample o;
    o.height = 0.0;
    o.slopeXZ = float2(0.0, 0.0);
    o.whitewash = 0.0;
    o.breaker = 0.0;
    o.mask = 0.0;
    return o;
}

// Evaluate the breaker-front layer. The caller provides the Layer A samples: still-water column
// depth (m), signed shore distance (m, + in water), unit direction toward shore, and the feathered
// in-field influence. Inert (all zeros) when inactive, off-field, offshore of the band, or on land.
SurfWaveSample EvaluateSurfWaves(float2 worldXZ, float depth, float sdfDist, float2 toShore,
                                 float influence, float time)
{
    SurfWaveSample o = SurfWaveSampleInert();
    if (_SurfActive < 0.5 || influence <= 0.001) return o;

    float band = max(_SurfBandDepth, 0.25);
    // Fronts develop as the water shallows into the band and run almost to the waterline (the
    // tight wet fade): the bore's own depth-capped height already dies gracefully as the column
    // vanishes, so a wide fade here just STRANDED the foam - fronts visibly stopped ~25 cm deep
    // and left a flat blue gap to the sand.
    float develop = 1.0 - smoothstep(SURF_NEAR_FADE * band, band, max(depth, 0.0));
    float wet = smoothstep(-0.05, 0.1, depth);
    // Shore exposure: the swell-facing side of a coast gets the surf; the lee side calms down.
    float exposure = SurfExposure(toShore);
    float mask = develop * wet * influence * exposure;

    // Standing waterline lace: foam hugging the last metres of water and a hint onto the swash
    // zone - it bridges the gap between the final bore and the sand so the run-out never reads
    // as flat open water. Exposure-gated AND segmented alongshore like the fronts (seeded by the
    // most recent arrival, so the segmentation drifts with each wave instead of forming one
    // endless static ribbon around the island).
    float laceIndex = floor(time / max(_SurfPeriod, 0.5) - 0.5);
    float laceSeg = SurfCrestFactor(worldXZ, laceIndex);
    float lace = (1.0 - smoothstep(0.2, 1.8, max(depth, 0.0)))
               * smoothstep(-0.35, -0.05, depth)
               * _SurfWaterlineFoam * influence * exposure * laceSeg;

    if (mask <= 0.001 && lace <= 0.001) return o;

    float s = max(sdfDist, 0.0);
    float3 front = SurfFrontHeight(worldXZ, SurfWarpDistance(s), depth, time); // (height, whitewash, breaker)

    // Slope by finite difference ALONG the shore-distance axis (the front varies along it by
    // construction; the crest noise is held fixed across the step so it never spikes the normal).
    // grad(sdfDist) = -toShore, since distance grows offshore.
    float h1 = SurfFrontHeight(worldXZ, SurfWarpDistance(s + SURF_SLOPE_EPSILON), depth, time).x;
    float dhds = (h1 - front.x) / SURF_SLOPE_EPSILON;

    o.height = front.x * mask;
    o.slopeXZ = -toShore * (dhds * mask);
    o.whitewash = saturate(front.y * mask + lace);
    o.breaker = front.z * mask;
    o.mask = mask;
    return o;
}

// Ambient-wave fade where the fronts own the surface (the anti-double-crest "replace" rule from
// Crest's spline Blend mode / HFW): multiply the ambient swell/FFT amplitude by this.
float SurfAmbientWeight(float surfMask)
{
    return 1.0 - surfMask * saturate(_SurfAmbientFade);
}

// --- Swash + wet sand (P4, fully analytic - no simulation, no persistent state) -----------------
// The swash is the thin film running up and back over the beach with each arriving front. At the
// waterline the front field's phase is time / period (s = 0), so the film's rhythm, set variation
// and drying can all be closed-form. Returns (no out-params - see SurfFrontHeight note):
//   .x swashLevel: metres of extra water level RIGHT NOW above the still plane (uprush/backwash)
//   .y wetLevel:   metres up to which the sand still glistens (recent max, drying through the cycle)
// The surface shader keeps beach fragments alive up to max(.x, .y) and renders the zone above the
// current film as the dark wet-sand glaze - wet sand with zero extra state.
float2 EvaluateSurfSwash(float2 worldXZ, float2 toShore, float influence, float time)
{
    if (_SurfActive < 0.5 || influence <= 0.001 || _SurfSwashAmplitude <= 0.0)
        return float2(0.0, 0.0);

    float T = max(_SurfPeriod, 0.5);
    // SYNC: a front's CREST reaches the waterline when its cell phase f hits 0.5 at s = 0, i.e.
    // when frac(time/T) = 0.5. Shifting the swash cycle by that half-cell makes the uprush START
    // exactly as the bore arrives (v1 peaked ~0.15 T BEFORE the wave hit - the "swash pops out of
    // nowhere, out of sync" read). The arriving front's index is floor(phase - 0.5), so the swash
    // also inherits THAT front's set amplitude + crest segmentation - the film runs up exactly
    // where and exactly as hard as the wave that just broke.
    float phase = time / T;                    // the front field evaluated at the waterline (s = 0)
    float arrivalIndex = floor(phase - 0.5);   // the front whose crest last hit the waterline
    float f = frac(phase - 0.5);               // 0 = crest arrival at the waterline

    float exposure = SurfExposure(toShore);
    float run = _SurfSwashAmplitude * SurfSetAmp(arrivalIndex) * influence
              * SurfCrestFactor(worldXZ, arrivalIndex) * exposure;
    // Quick uprush, slower backwash (real swash is strongly asymmetric).
    float upDown = (f < SURF_SWASH_UPRUSH)
        ? smoothstep(0.0, SURF_SWASH_UPRUSH, f)
        : 1.0 - smoothstep(SURF_SWASH_UPRUSH, 1.0, f);
    float swashLevel = run * upDown;
    // Wet line as a CONTINUOUS two-front envelope. The naive form referenced the NEW arrival's
    // full run-up the instant the cycle rolled over, so the wet/clip line teleported up the beach
    // in one frame ~a quarter-period before the water got there - the visible "pop" between the
    // last small wave and the swash. Instead:
    //  - THIS cycle's wet line can only be as high as the film has actually reached (it rises
    //    WITH the uprush), then dries toward the floor through the backwash;
    //  - the PREVIOUS front's wet line keeps drying through this cycle (second-stage dry-out);
    //  - the sand shows whichever is higher. Continuous everywhere, including cycle rollover:
    //    at f->1 this cycle ends at run*FLOOR, and at f=0 the next cycle's "previous" term
    //    starts at exactly run*FLOOR.
    float runPrev = _SurfSwashAmplitude * SurfSetAmp(arrivalIndex - 1.0) * influence
                  * SurfCrestFactor(worldXZ, arrivalIndex - 1.0) * exposure;
    float thisCycleWet = (f < SURF_SWASH_UPRUSH)
        ? swashLevel
        : run * lerp(1.0, SURF_SWASH_WET_FLOOR, smoothstep(SURF_SWASH_UPRUSH, 1.0, f));
    float prevCycleWet = runPrev * SURF_SWASH_WET_FLOOR * lerp(1.0, 0.25, smoothstep(0.0, 1.0, f));
    float wetLevel = max(thisCycleWet, prevCycleWet);
    return float2(swashLevel, wetLevel);
}

#endif // WEBGPUWATER_SURF_WAVES_INCLUDED
