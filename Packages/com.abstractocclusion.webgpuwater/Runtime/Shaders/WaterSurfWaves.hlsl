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

#define SURF_TWO_PI            6.28318530718
// H/d breaking criterion (McCowan's solitary-wave limit ~0.78): a front whose height exceeds this
// fraction of the local column depth cannot stand and collapses into a bore.
#define SURF_BREAK_RATIO       0.78
#define SURF_MIN_DEPTH         0.05  // metre floor under every depth divide
#define SURF_FACE_FRACTION     0.10  // steep shoreward face length, as a fraction of front spacing
#define SURF_BACK_FRACTION     0.24  // long offshore back length, as a fraction of front spacing
#define SURF_BORE_HEIGHT_KEEP  0.45  // height fraction a broken front keeps as the whitewash bore
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

// Core front shape at a warped shore distance + local depth. Returns (height m, whitewash,
// breaker) as a plain float3 - NO out parameters: FXC's inliner is fragile around out-params in
// deeply nested calls (the editor's shader-compiler process died on the first build of this file),
// and a value return is also simply cleaner. This is THE canonical evaluation - the CPU mirror
// (LargeWaveField.SurfFrontHeight) reproduces exactly the .x math.
//   .y whitewash: 0..1 broken-bore coverage (foam fuel, trails behind the moving front)
//   .z breaker:   0..1 "cresting/about to break" signal (thin line at the lip - foam + SSS fuel)
float3 SurfFrontHeight(float sWarp, float depth, float time)
{
    float L = max(_SurfWavelength, 1.0);
    float T = max(_SurfPeriod, 0.5);
    // Phase grows with time at fixed distance, so an iso-phase crest moves TOWARD the shore
    // (smaller s) as time advances; speed drops where the warp has compressed the spacing.
    float phase = sWarp / L + time / T;
    float frontIndex = floor(phase);
    float f = phase - frontIndex;              // 0..1 across the front cell, crest at 0.5

    float setAmp = SurfSetAmp(frontIndex);
    float d = max(depth, SURF_MIN_DEPTH);

    // Local height: Green's-law growth toward the shore, capped by the breaking criterion.
    float green = min(pow(max(_SurfBandDepth, d) / d, 0.25), max(_SurfGreens, 1.0));
    float H = _SurfAmplitude * setAmp * green;
    float capH = SURF_BREAK_RATIO * d;
    float overCap = H / max(capH, 1e-3);
    float cresting = smoothstep(0.75, 1.05, overCap); // cresting: approaching/at the limit
    float broken = smoothstep(1.0, 1.35, overCap);    // fully broken -> whitewash bore
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
    float mask = develop * wet * influence;

    // Standing waterline lace: foam hugging the last metres of water and a hint onto the swash
    // zone, independent of the front rhythm - it bridges the gap between the final bore and the
    // sand so the run-out never reads as flat open water.
    float lace = (1.0 - smoothstep(0.2, 1.8, max(depth, 0.0)))
               * smoothstep(-0.35, -0.05, depth)
               * _SurfWaterlineFoam * influence;

    if (mask <= 0.001 && lace <= 0.001) return o;

    float s = max(sdfDist, 0.0);
    float3 front = SurfFrontHeight(SurfWarpDistance(s), depth, time); // (height, whitewash, breaker)

    // Slope by finite difference ALONG the shore-distance axis (the front varies along it by
    // construction). grad(sdfDist) = -toShore, since distance grows offshore.
    float h1 = SurfFrontHeight(SurfWarpDistance(s + SURF_SLOPE_EPSILON), depth, time).x;
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
float2 EvaluateSurfSwash(float influence, float time)
{
    if (_SurfActive < 0.5 || influence <= 0.001 || _SurfSwashAmplitude <= 0.0)
        return float2(0.0, 0.0);

    float T = max(_SurfPeriod, 0.5);
    float phase = time / T;                    // the front field evaluated at the waterline (s = 0)
    float frontIndex = floor(phase);
    float f = phase - frontIndex;

    float run = _SurfSwashAmplitude * SurfSetAmp(frontIndex) * influence;
    // Quick uprush, slower backwash (real swash is strongly asymmetric).
    float upDown = (f < SURF_SWASH_UPRUSH)
        ? smoothstep(0.0, SURF_SWASH_UPRUSH, f)
        : 1.0 - smoothstep(SURF_SWASH_UPRUSH, 1.0, f);
    float swashLevel = run * upDown;
    // The wet line starts at the full run-up as the front recedes and dries toward the floor
    // through the rest of the cycle; the NEXT front re-wets it. Never below the current film.
    float wetLevel = max(run * lerp(1.0, SURF_SWASH_WET_FLOOR, smoothstep(SURF_SWASH_UPRUSH, 1.0, f)),
                         swashLevel);
    return float2(swashLevel, wetLevel);
}

#endif // WEBGPUWATER_SURF_WAVES_INCLUDED
