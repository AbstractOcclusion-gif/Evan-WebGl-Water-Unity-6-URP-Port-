# Particle Systems Review & Harmonization Plan — 2026-07-16

Scope: WaterFoamParticles (foam/whitecap sprites + density splat), WaterSurfRollerParticles
(shore roller spray), WaterSplashEmitter/WaterSplash (impact crown + droplets), WaterSurfCurl
(ribbon, touched only where it shares code). References mined: **KWS2** (DynamicWaves GPU
foam/splash pipeline), **KWS1** (baked shoreline foam particles), **Crest 5** (foam LOD sim).

ANALYSIS ONLY — no code has been touched. Each fix chunk below needs your GO.

---

## 1. BUG — Foam particles "move when the camera moves" (ocean FFT)

Individual particles are fine: they live in **world space** (`worldPos` integrated with no
camera term, `WaterFoamParticles.compute` Update ~635) and the FFT re-attach in
`FoamParticles.shader:137` is keyed on the particle's own world xz. What moves is the
**population envelope** — four camera-anchored mechanisms, none world-snapped:

| # | Mechanism | Where | Effect |
|---|---|---|---|
| A | Emission domain = camera-following sim window (`_SimCenter` republished each frame, spawn = 1 thread/sim texel) | compute:246, WaterUniformPublisher.cs:196 | spawnable region is a camera-centred rectangle that glides continuously |
| B | Distance LOD centred on raw camera xz, re-evaluated per frame (`distNorm = dist(texelWorld, _SpawnCameraXZ)/_SpawnMaxDistance`, 80 m) | compute:433, cs:384 | density blob follows the camera |
| C | Hard kill at the moving window edge (`abs(simNorm) > 1.02 → life = 0`) | compute:594–600 | world-anchored foam behind you is culled while the leading edge refills — with 1.5–4 s lifetimes this reads as "foam glued to camera" |
| D | Spawn randomness keyed on **window-relative** texel id + per-frame `_FrameSeed` | compute:450, cs:355 | the "which spots emit" pattern has no world identity at all |

Plus two secondary issues: the ScreenSpaceDensity splat always uses **Camera.main**'s
matrices (`cs:328`) but draws into every camera seeing the bounds (scene view / secondary
cams see a translated layer), and the VP matrix is captured in LateUpdate — a
later-ordered camera mover (Cinemachine) gives a one-frame swim.

**The roller already contains the cure** — `WindowSnapMeters = 30` world-lattice snap
(WaterSurfRollerParticles.cs:55–61, 364–370). And KWS2 confirms the reference recipe
(CONFIRMED in KWS2_DynamicWavesFoamParticlesCompute.compute):

- Emission from a **fixed world grid** (sim-zone texel → world via zone transform, :179–190);
  camera never defines positions, only *gates* acceptance.
- Camera gating is **stochastic with a keep-alive floor** (`FOAM_MIN_LOD_COUNT = 0.02`
  spawns even outside the view cone, :315–336) so coverage exists before you turn.
- Each particle stores a **permanent random ticket** (`initialRandom01`) used for its
  distance-LOD roll every frame — same particles survive frame to frame, no re-roll popping.
- Soft border: near-edge particles get lifetime shortened randomly (:637), not hard-killed.

### Fix F1 (foam world-anchoring) — proposed
1. Snap the spawn lattice + LOD centre to a world lattice (reuse the roller's
   `WindowSnapMeters` idiom; snap `_SpawnCameraXZ` and derive spawn texel ids from
   **world** cell coords, not window coords).
2. Key spawn randomness on world cell id (`floor(worldXZ / cellSize)`) instead of
   window texel id + frame seed → world-stable emission pattern.
3. Replace the hard `FRAME_KILL_MARGIN` kill with a randomized lifetime-shortening fade
   band (KWS2 :637 idiom).
4. Give each particle a stored LOD ticket (seed already exists in the struct — reuse it)
   and make the distance thinning use it instead of a fresh roll.
5. Gate the density composite to the density camera (or per-camera VP) to stop the
   scene-view translation artifact.

---

## 2. BUG — Roller particles "only spawn at some points of the waves"

The v1.1 ragged gate fixed the half-missing rows. What still limits coverage is a stack of
serial gates in `WaterSurfRoller.compute::Emit` (:232–354):

1. **One 120 m window, one break line** (`EmitWindowLengthMeters = 120`), centred on the
   *first* overCap crossing marched from the camera; no emission outside it, ever
   (cs:41, 335, 398–419).
2. **Fixed across-shore line vs per-wave break depth** — the window sits where the *mean*
   set wave breaks; big set waves break offshore of it, small ones inshore, so at
   crest-arrival the local `cresting` signal is often < 1 → eaten by gate 3. This is the
   main "only some waves emit" structural limiter.
3. **Signal gate evaluated at exactly one frame** (`signal = cresting × (1-broken) ×
   plungeOrSpill × crestSeg; if (signal < 0.2) return` :274–277) — set-envelope lulls,
   crest-segmentation gaps, surging waves, and any missed frame (beat wrap guard :263)
   permanently zero that (front, slot).
4. **Ragged probability gate** thins anything with signal < 0.4 (:282–284).
5. **Field mask gate** (border feather, wet gate, `SurfExposure` lee-side) :268–269.
6. **Frame budget = pool/4** with first-come `InterlockedAdd` — a full 120 m front with
   burst 3 wants ~1080 particles; a 2048 pool caps at 512 and the losing slots
   **permanently skip that front** (arrival test won't refire) → partial rows on big waves.
7. Window snap hop (30 m) can double-emit or gap one front period (minor).

### Fix F2 (roller coverage) — proposed
1. **Per-slot break tracking**: evaluate arrival at the slot's own `overCap=1` crossing
   (Newton refine per slot, we already invert the warp per-slot in Update) instead of one
   shared across-shore line → every wave emits where *it* breaks.
2. Widen the arrival test to a short **phase window** (e.g. 2–3 beat-frames of tolerance)
   with the existing dedupe keys, so a missed frame degrades to "late" not "never".
3. Budget fairness: when over budget, drop **per-burst members** (b>0 first), not whole
   slots — or hash-rotate which slots win per front so partial rows are dithered, not
   one-sided.
4. Optional v2 (bigger): multi-window along the coast (N snapped windows around the
   camera) — turns the single 120 m strip into coast-wide coverage; KWS1 does per-wave
   instances with distance-LOD stride skipping (stable subsets, no popping).

---

## 3. Architecture — "2 scripts, dispatched, needs a clean"

Current split of responsibilities and the duplication map (all CONFIRMED, quotes in the
agent audit):

- **Pool recipe** (pow2 rounding, budget clamp, zero-init) copy-pasted between
  WaterFoamParticles.cs:277–284 and WaterSurfRollerParticles.cs:248–253.
- **PCG hash** duplicated in both computes (roller admits it: "Duplicated from
  WaterFoamParticles.compute").
- **Billboard expansion + flipbook cell math** byte-identical in FoamParticles.shader
  :61–63/211–216 and SurfRollerParticles.shader :50–52/134–139.
- **Shore-field sampling** exists twice with *diverged contracts*: foam's `SurfSampleAt`
  continues with `toShore=(0,0)` on degenerate SDF; roller's `SampleShoreField` returns
  false → kills. Behavioural drift at SDF singularities.
- **Break-line solve** duplicated near-verbatim WaterSurfCurl.cs:268–334 ↔
  WaterSurfRollerParticles.cs:378–444 (comment even says "change this one the same way").
- **Burst jitter constants** duplicated CPU↔GPU (WaterSplashEmitter.cs:31–37 ↔
  WaterFoamParticles.compute:167–172) — retune one side and the look silently forks.
- **Struct layouts maintained ×3** (C# / compute / shader) for FoamParticle and
  RollerParticle, ×2 for BurstRequest — "MUST match" comments, no shared source.
- **Sentinels/feathers ×4 copies** (`1e9` deep sentinel, `0.08` border feather, each with
  its own "KEEP IN SYNC" note); `FrameSeedHashPrime` ×2.
- **Lifecycle drift**: roller has a LateUpdate dead-pool null guard, foam doesn't; roller
  uses `GetComponentInParent`, foam `GetComponent`; roller honors `volume.targetCamera`,
  foam hardcodes `Camera.main`; AddComponentMenu names differ ("AbstractOcclusion/Water"
  vs "WebGL Water").
- **Crown ownership**: WaterSplashEmitter is sole runtime owner (crown Shuriken +
  ConfigureCrown); droplets route to the foam GPU pool when present, else legacy Shuriken.
  WaterSplashEmitter also does a CPU `GetParticles/SetParticles` round-trip + per-droplet
  `BodyContaining` every frame even when idle (cs:119–140).

### Fix F3 (harmonization) — proposed, behavior-preserving
1. **Shared runtime core** `WaterParticlePool` (C# helper): pow2 pool alloc, counters,
   ring cursor, budget upload, dispatch helper, MPB flipbook plumbing, null-guard
   lifecycle. Foam + roller become thin owners of emission logic only.
2. **Shared HLSL include** `WaterParticleCommon.hlsl`: PCG hash, QUAD_CORNERS/billboard
   expansion, flipbook cell math, soft-depth fade, one copy of sentinels/feathers
   (single named constants; validator pair added to WaterWaveConstantsValidator like the
   BEAT pairs).
3. **One shore-field sampler** with an explicit degenerate-SDF policy (pick the roller's
   fail-fast contract; foam adapts).
4. **Extract the break-line solve** into one shared static (WaterSurfBreakLine.cs) used by
   curl + roller (they differ only smooth-vs-snap at the end — parameterize).
5. **Burst constants single-sourced**: move the six jitter constants into one C# static
   consumed by WaterSplashEmitter and mirrored into the compute with a validator pair.
6. Unify: foam gets the roller's LateUpdate guard + `targetCamera` resolution + parent
   lookup + AddComponentMenu naming; roller's `renderQueueOffset` applied on change, not
   only OnEnable.
7. **WaterSplashEmitter diet**: skip the Shuriken round-trip when zero alive particles;
   cache the body instead of per-droplet `BodyContaining`.

Not proposed (would violate "no readback / keep it simple" or change look): merging all
three into one mega-manager. Composition, not inheritance: three components, one shared
core.

---

## 4. Textures — enhancement plan

Current state (CONFIRMED):
- `DropletPacked.png` 64×64 procedural, KWS packing R=mass G=shine B=dissolve A=thickness
  (WaterBuildKit.cs:657–690). Works, but tiny and synthetic.
- `FoamParticleAtlas_2x2`, `FoamFlipbook_4x4(+Normal)`, `SplashFlipbook_8x8` are
  **load-only** in the staged build kit — shipped PNGs, no procedural source.
- FoamParticles/SurfRoller shaders sample a seed-picked atlas cell, optional flipbook over
  age; RGB multiplies lit colour → white-ish blobs if the atlas is flat.
- **Biggest gap**: `FoamDensityComposite.shader` samples *no texture at all* — the default
  ScreenSpaceDensity foam look is a pure density curve + dilation. No lace/pattern detail.

What the references do (to port):
- **KWS2 splash look = noise-erosion aging**, not alpha fade: `noise = saturate(noise −
  life*2 + 1); main *= noise; shine = (shine*noise)^3` — sprite dissolves through its own
  B channel. We already have the B channel; our shaders fade instead of erode.
- KWS2 uses a **4-column splash atlas** with per-particle random column + mip bias −1.5;
  soft-fade weighted by the **thickness (A) channel** so intersections stay rounded
  (`lerp(soft*pow10(depth)*5, 1, soft)`).
- KWS2 foam density shading = two-band transfer (`0.2*density + 0.5*density²*0.01`)
  tinted by light — plus a 4-tap **dilation** pass. Ours has dilation; adding a world-xz
  projected breakup texture in the composite is the single highest-visual-impact change.
- Crest surface foam: black-point feathered lookup `smoothstep(1-foam, 1-foam+feather,
  tex)` + normals from 2 offset taps — cheap "lace" upgrade for our surface foam gate.
- KWS2 size distribution: `pow5(rand)` → few hero sprites, many small — instant variety
  without new art.

### Fix F4 (textures) — proposed
1. Density composite: add optional world-projected breakup texture (reuse
   FoamFlipbook_4x4) + KWS2 two-band transfer; keep current path as fallback.
2. Particle shaders: switch alpha-over-life to **noise erosion** using the existing
   dissolve channel; add `pow5(seed)` hero-size distribution knob.
3. Regenerate `DropletPacked` at 128², and (optional) author/procure a real 4×4 foam
   atlas; grid sizes are already component knobs, drop-in.
4. Crest-style black-point feather for the surface foam pattern sample.

---

## 5. Reference techniques worth porting later (backlog)

- **Ping-pong stream compaction + indirect draw** (KWS2): survivors compacted each frame,
  `RenderPrimitivesIndirect` with GPU-written instance count → stops drawing the full
  pow2 pool every frame (we currently pay 393 k verts/frame at max pool, mostly dead).
  Fully WebGPU-safe (no readbacks, no append buffers). Biggest perf win available.
- **4-bucket time slicing** (KWS2): quarter of the foam pool updates per frame at 4× dt,
  rendering interpolates prev→pos — 15 Hz sim cost, 60 Hz look.
- **Screen-tile density cap** (KWS2): 64 px tile atomic counters, cap 15–50/tile by
  budget — prevents splash overdraw storms. We already count tiles for foam; extend to
  bursts.
- **Foam clumping** (KWS2): mid-life perlin attraction (`sin(π·life)` weighted) gathers
  foam into patches — kills the "uniform dust" look.
- **Free-moving vs surface-carried state flag** (KWS2): airborne ballistic + one-sided
  random-stickiness surface clamp vs hard `y = surface` — we already have the y-semantics
  split; formalizing it removes our dual-meaning `worldPos.y` hazard.
- **Crest Jacobian whitecap source** for FFT foam (`coverage − det`) with min-wavelength
  filter — cleaner crest detection than our shear estimate (and we can drop the 4 extra
  `Sim.Load`s per texel before the probability roll).
- KWS1 stride-skip LOD (stable subsets by index stride) if/when we do multi-window
  rollers.

---

## 6. Other flaws found (fix opportunistically inside the chunks)

- `SpawnBurst` bypasses `_MaxSpawnPerFrame` entirely (compute:541–578) — 16×64 droplets
  can recycle a quarter of the pool in one frame.
- OCEAN_CREST_FOAM variant with null `OceanFftSpatialTexture` → unbound resource on
  WebGPU (cs:470–478 vs compute:370–383).
- Dead knob `FOAM_NOISE_EPSILON` (never used); roller struct carries ~20/80 dead bytes
  (`dAcross`, `birthOverCap`, pad); `MaxEmitSlots=4096` unreachable; `PreviewGain=8`
  still "TEMP".
- Magic numbers: `0.6+0.4*r1` launch jitter, `lerp(0.85,1.15,…)` throw jitter, bare `17u`
  salt, `0.001` vs named `DEPTH_TO_MM`, `6.2831853` where `SURF_TWO_PI` exists.
- Foam density buffers keyed to `Camera.main` half-res — editor window resize thrashes;
  sim-pause pops density→quads for a frame.
- Both particle shaders are CGPROGRAM/UnityCG inside URP tags — works but off-pipeline.

---

## 7. Proposed execution order (each chunk reviewable, behavior-preserving unless noted)

| Chunk | Content | Risk | Visual change |
|---|---|---|---|
| **P1** | F1 foam world-anchoring (snap lattice, world-keyed randoms, fade band, LOD ticket, density-camera gate) | med | fixes the bug, otherwise same look |
| **P2** | F2 roller coverage (per-slot break depth, phase-window arrival, budget fairness) | med | fuller, per-wave-correct rows |
| **P3** | F3 harmonization (shared pool core, shared HLSL include, one break-line solve, one shore sampler, unified lifecycle, validator pairs, splash-emitter diet) | low | none (that's the point) |
| **P4** | F4 textures (erosion aging, density breakup texture, hero sizes, 128² droplet, black-point feather) | low | better, knob-gated |
| **P5** | Backlog: indirect draw + compaction, time slicing, clumping, Jacobian crest source, multi-window rollers | high | perf + coast-wide coverage |

Recommended: P1 → P2 (your two reported bugs), then P3 before anything else grows on top
of the duplicated code, then P4/P5.

---
*Sources: full agent audits (this session) of the 18 staged ThreeJS files and 21 staged
KWS2/KWS1/Crest files. Note: a `_stage_tmp` folder was created in the KWSWater project
root to work around a staging error — safe to delete.*
