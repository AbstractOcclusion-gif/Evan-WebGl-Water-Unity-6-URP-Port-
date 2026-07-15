# Surf Physics + Procedural Curl — implementation prompt

Date: 2026-07-15. Status: **prompt for the next work session — no code until Bert says go.**
Companion to `docs/COASTLINE_MASTER_PLAN_2026-07-15.md` (the phased plan) and the shipped
coastline stack (P0–P5-lite, see `docs/NIGHT_REPORT_COASTLINE_2026-07-15.md` + the session that
followed it). Project rules apply: verify every claim against the live tree before building on it,
reuse working code as thin adapters, small reviewable chunks, CPU-mirror parity for anything that
moves the surface, WebGPU-safe (no readback, no float32 filtering, no unbound samplers).

## 0. Where the stack stands (verify in Phase 0, don't trust blindly)

- `WaterShoreDepthField.cs` — Layer A: world-frame column-depth field + jump-flood SDF
  (smoothed direction), CPU arrays kept for the buoyancy mirror (`TrySampleShore`). SDF texture is
  RGBAHalf: RG = toward-shore dir, B = signed distance, **A = mask (currently always 1 — the free
  channel this work will use)**.
- `Runtime/Shaders/WaterSurfWaves.hlsl` — the surf front layer: SDF-phase fronts, Green's-law
  growth, McCowan break (`SURF_BREAK_RATIO 0.78` constant), bore collapse
  (`SURF_BORE_HEIGHT_KEEP 0.6` constant), whitewash/breaker signals, crest segmentation
  (`SurfCrestFactor`), swell exposure (`SurfExposure`), synced swash + continuous wet-line
  envelope (`EvaluateSurfSwash`).
- `Runtime/Shaders/WaterLargeWaves.hlsl` — shoal transform (refraction/compression/Green) +
  geometry foam (`ApplyLargeBodyWaveNormalFoamShore`: Jacobian pinch + slope gate).
- `Runtime/LargeWaveField.cs` — CPU mirror of ALL of the above height math (byte-for-byte;
  every constant change must land in both).
- `Runtime/Shaders/WaterHeroWave.hlsl` — Bert's surfable rolling-wave experiment. NOT a shore
  system, but its **attractor-curl machinery is the exact math the plunging lip needs**:
  `HeroEvaluate` (sech² profile → curl-region mask → rotation of crest points around a pivot
  ahead of the face, US7561993), `HeroSheetNormal` (param-space FD normal through the overhang),
  the `_IsHeroWave` dense-strip pattern in `WaterSurface.shader` (discard below
  `HERO_SHEET_MIN_WEIGHT`), and `HeroWaveFoamSource`. Read it thoroughly before designing.
- SWE is REMOVED (2026-07-15). Do not resurrect it for this work.

## Part 1 — SURF-PHYS: slope-aware breaker physics (the science upgrade)

Replace the hand-tuned breaking constants with the standard coastal-engineering relations, so the
coastline diversifies itself from the bathymetry: spilling foam on flat shelves, plunging barrels
on moderate slopes, surging swash against steep shores — automatically, per crest segment.

### 1a. Beach slope channel (bake-time)
In `WaterShoreDepthField.BuildSdf`, compute per texel the LOCAL BEACH SLOPE `tanBeta` =
|∇depth| from the baked depth array (central differences over the world texel size, then the same
3×3 box-smooth the direction field gets — raw terrain gradients are noisy). Store it in the SDF
texture's **A channel** (currently constant 1; every reader uses `.a` only as a mask — grep and
update the few readers to treat A as slope, with `> 0` serving the old mask role, or pack
`slope` and keep mask implicit in the valid flag — decide in Phase 0 after the grep).
Keep a CPU copy (`_cpuSlope`) and extend `TrySampleShore` with it. The slope that matters for
breaker physics is the slope AT/UNDER the surf zone — sampling it per-fragment/per-eval at the
same uv as depth is correct and free.

### 1b. Iribarren classification (per evaluation point, in WaterSurfWaves.hlsl)
```
xi = tanBeta / sqrt(max(H0, eps) / L0)     // H0 = _SurfAmplitude*setAmp (deep-water height),
                                           // L0 = 1.56 * _SurfPeriod^2 (deep-water wavelength, g/2pi*T^2)
spill  = 1 - smoothstep(0.45, 0.60, xi)    // xi < ~0.5
plunge = smoothstep(0.45, 0.60, xi) * (1 - smoothstep(2.8, 3.6, xi))
surge  = smoothstep(2.8, 3.6, xi)          // xi > ~3.3
```
These three weights drive the lifecycle SHAPE (smooth blends, never branches):
- **spilling**: today's behaviour — early break, wide gradual whitewash, bore runs long.
  Whitewash gain × ~1, bore profile wide.
- **plunging**: break later and harder — narrower, more intense whitewash; the `breaker`
  (cresting-lip) signal is amplified and widened; **this weight is the gate for the Part-2 curl**.
- **surging**: suppress whitewash almost entirely; skip the bore; route the wave's energy into
  the swash instead (see 1e) — the wave arrives as a surge up the slope.

### 1c. Slope-dependent breaker index (replaces the 0.78 constant)
Weggel-style: `gamma = clamp(0.6 + 5.0 * tanBeta, 0.6, 1.1)` (exact coefficients tunable; verify
against the Weggel 1972 form `b(m) - a(m)*H/(gT^2)` and pick the simplification consciously).
Use `gamma` where `SURF_BREAK_RATIO` is used today (capH, overCap). Steep beaches now break
later, in relatively shallower water, more violently — flat beaches break early and soft.

### 1d. Bore decay (replaces SURF_BORE_HEIGHT_KEEP)
Dally–Dean–Dalrymple: a broken bore decays toward a STABLE height `~0.4 * depth`, it doesn't
keep a fixed fraction of its break height. Practical closed form (no integration available in a
stateless field): `boreH = max(gammaStable * d, H_atBreak * exp(-k * (sBreak - s) / d)) `-style
decay is NOT closed-form without knowing sBreak — so use the stateless approximation:
`boreH = lerp(H_capped, gammaStable * d, broken)` with `gammaStable = 0.40` and `broken` the
existing smoothstep — i.e. the bore relaxes onto the 0.4·d envelope instead of 0.6·H. Simple,
stateless, and matches the reference behaviour direction. Document the approximation.

### 1e. Hunt run-up (swash from physics)
`R = xi * H_atWaterline` (Hunt 1959). `EvaluateSurfSwash` computes `run` from this instead of a
raw amplitude knob; `_SurfSwashAmplitude` becomes a MULTIPLIER on Hunt (default 1) so scenes stay
tunable. The `surge` weight from 1b additionally boosts the swash share (surging waves put
everything into run-up). Slope + toShore are already passed/available at both swash call sites.

### 1f. Parity + gates
Every formula lands identically in `LargeWaveField.cs` (heights + swash are render-only? NO —
front height changes, so the mirror MUST match; swash remains render-only). Verification gate:
on one screenshotable scene, artificially vary the beach slope (terrain edit) and confirm:
flat shelf → early wide spilling; moderate ramp → late hard break; cliff → no foam, big surge.
Constants live once in WaterSurfWaves.hlsl with the C# mirror lockstep-commented.

## Part 2 — CURL: procedural plunging lip (KWS-look, option (a) — no baked assets)

Goal: where a front is classified **plunging** (1b) and its crest segment is strong
(`SurfCrestFactor` high), the cresting face grows an actual overturning lip for the
break moment — the KWS1 spectacle, generated procedurally.

Approach (adapt, don't rewrite — the machinery exists in WaterHeroWave.hlsl):
1. **Base surface stays heightfield-safe.** The existing front profile already leans; add at most
   a steepened face (profile sharpening by `plunge * breaker`) on the base surface. The overhang
   itself CANNOT live on the heightfield.
2. **Lip sheet = the hero-wave strip pattern, auto-driven.** A dense strip mesh (reuse/adapt the
   `_IsHeroWave` renderer pattern: dedicated mesh + vertex branch + fragment discard below a
   min weight) whose vertices are parameterized (u = alongshore, d = across). Per vertex:
   sample the shore field → front phase → if `plunge * breaker * crestSegment` exceeds the
   threshold, evaluate an attractor-curl rotation (HeroEvaluate's pivot-rotation math, driven by
   the front's local height/phase instead of the hero uniforms) → world offset. Fragments
   outside the curl region discard, exactly like the hero sheet.
   - Placement: the strip follows the camera along the BREAK LINE (the depth contour where
     overCap≈1) within the surf band — a camera-following ribbon, positioned on the CPU each
     frame from the same closed-form field (no readback; `LargeWaveField`/shore CPU arrays give
     everything). Start with ONE ribbon near the camera; multiple fronts later.
3. **Foam/spray hooks**: the curl weight feeds the breaker signal (SSS + whitewash already
   consume it) and later the spray-particle emitter (`OCEAN_CREST_PARTICLES_PLAN.md`).
4. **Quality tier**: the sheet is an opt-in component/tier (phones skip it; the 1b plunging
   *shape* changes still read without the overhang).
5. **Physics**: sheet is render-only; buoyancy keeps the heightfield-safe base (document).

Phasing: CURL-0 read-only grounding (hero strip wiring, clipmap near-ring density, exact
break-line query) → CURL-1 static test ribbon with curl profile over a flat ocean (look test,
mirrors P0 of the old SURF_WAVE_OPTIONS study) → CURL-2 shore-field-driven placement + lifecycle
→ CURL-3 foam/spray integration + tier gating.

## Part 3 — Backlog after these two (unordered)

- Wet sand on the TERRAIN material (hook: analytic wet line from `EvaluateSurfSwash`; needs a
  decision on terrain shader/decal approach).
- Spray particles from the curl/breaker signal (`WaterFoamParticles` + the crest-particles plan).
- Alongshore arc-length coordinate baked into the shore field (unlocks authored crest windows +
  a traveling PEEL point along the break line — the full Kelly-Slater peel; also what a
  KWS1-style baked-patch system would sit on if option (b) is ever wanted).
- Master-Base-Clean Option 1 (GPU bake, delete CPU mirrors) — do in a live session with
  buoyancy playtesting.
- Quality tiers for mobile; audit W1 (foam-mask manual bilinear); multi-shore-body support.

## References (for the agent doing the work — verify formulas before use)

- Iribarren number / surf similarity: Battjes (1974) "Surf similarity"; any coastal engineering
  text (USACE Coastal Engineering Manual Part II-4 covers breaker types + criteria).
- Weggel (1972) "Maximum breaker height" — slope-dependent breaker index.
- Dally, Dean & Dalrymple (1985) "Wave height variation across beaches of arbitrary profile" —
  bore decay to stable height.
- Hunt (1959) run-up; Stockdon et al. (2006) for the modern empirical form if more fidelity is
  ever wanted.
- Local code references: `WaterHeroWave.hlsl` (curl math), `KWS1` project (now connected:
  `Assets/KriptoFX/WaterSystem/WaterResources/` — flipbook shoreline waves, for comparison and
  look reference: `ShorelineWavesPass.cs`, `KWS_ShorelineWaves.shader`), plus the KWS2/Crest
  paths listed in `docs/SHORELINE_PLANNING_PROMPT_2026-07-13.md` §3.
