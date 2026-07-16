# Particles P1–P4 — implementation report (2026-07-16)

All four approved chunks are implemented and written to disk. **Nothing is tested in Unity yet**
— recompile + the checklist at the bottom. Plan/citations: `PARTICLES_HARMONIZATION_PLAN_2026-07-16.md`.

## P1 — Foam world-anchoring (the "foam moves with the camera" fix)
`WaterFoamParticles.compute` + `.cs`

- Spawn decisions are now keyed on a **fixed world lattice** (`WorldSpawnKey`, cell = one sim
  texel, 0.25 m floor) instead of window texel ids — the emission pattern belongs to the water,
  not to wherever the camera-following window sits.
- The distance-LOD roll is a **fixed per-world-cell ticket** (no more per-frame re-roll around
  the camera) + a KWS keep-alive floor (`SPAWN_LOD_KEEP_ALIVE 0.02`) so a sparse dusting exists
  beyond 80 m before you get there.
- The hard window-edge kill now has a **fade band** (`FRAME_FADE_BAND 0.15`): aging accelerates
  up to 3× toward the edge, so the moving kill line reads as a gradual envelope fade. Hard kill
  stays as the out-of-data backstop.
- Density composite is **gated to the camera that projected it** (`RenderParams.camera`) — the
  scene view no longer shows a foam layer translating with the game camera (it shows the
  world-anchored spray billboards instead).
- Foam now honours `volume.targetCamera` (falls back to `Camera.main`) like roller/curl.
- Bonus fix: `OCEAN_CREST_FOAM` can no longer dispatch with a null spatial cascade
  (unbound-resource error class on WebGPU).

## P2 — Roller coverage (the "only some points spawn" fix)
`WaterSurfRoller.compute`

- **Per-front break solve**: overCap factorizes as `setAmp × meanOverCap(depth, slope)`, so each
  slot solves each candidate front's own break distance in closed form (offshore
  finite-difference gradient, clamped to ±1 wavelength) and triggers when the front's crest
  crosses **its own break point** — big set waves emit offshore of the mean line, small ones
  inshore, every wave at its cresting moment.
- **Interval crossing test** (prev-beat → now): a hitched frame emits late instead of never; the
  crossing is strictly monotonic so the (slot, front) dedupe stays exact with zero state.
- **Partial-burst budget**: a slot at the frame-cap boundary emits what fits instead of dropping
  its whole burst; per-front break offsets also de-synchronize arrivals, so a long front no
  longer claims the budget in one frame.
- Cleanup: dead `spacing2` removed.

## P3 — Harmonization (behavior-preserving)
3 new files + 8 edited

- **`WaterParticleCommon.hlsl`** (new): PCG hash + `Rand01`, procedural quad corner expansion,
  flipbook atlas cell math, `PARTICLE_TWO_PI`, and (opt-in `WATER_PARTICLE_SHORE_FIELD`) the
  Layer A shore-field uniforms/textures + **one** `ParticleSampleShoreField` with the roller's
  fail-fast degenerate-SDF contract. Both computes and both particle shaders now include it;
  every duplicated copy deleted. NOTE: foam's shore fetch now goes inert on a degenerate SDF
  (deliberate contract unification — the only intentional behavior delta in P3).
- **`WaterSurfBreakLine.cs`** (new): the ONE break-line march+bisect, used by curl + roller
  (continuity flip stays at the call sites, which own their smoothing state).
- **`WaterParticlePool.cs`** (new): shared tier-capped pow2 pool + counters allocation and the
  flipbook MPB plumbing; both particle systems use it.
- Validator (`WaterWaveConstantsValidator.cs`): now also guards the six splash-burst constants
  CPU↔GPU (`BURST_*` in WaterFoamParticles.compute vs consts in WaterSplashEmitter.cs) and
  parses `static const` HLSL constants, not just `#define`s.
- Lifecycle unify: foam gets the roller's LateUpdate dead-pool guard, `GetComponentInParent`,
  and the "AbstractOcclusion/Water" menu path; roller's `renderQueueOffset` now applies live
  via OnValidate.
- Splash emitter idle diet: zero alive particles → no more per-frame Shuriken
  GetParticles/SetParticles round-trip.

## P4 — Textures
- **Density composite breakup lace** (`FoamDensityComposite.shader` + `.cs`): the biggest visual
  gap — the screen-space foam veil now erodes through a **tileable pattern sampled in world XZ**
  (foam-layer world position reconstructed from the splatted min-depth along the camera ray, via
  the same view-projection the splat used — the lace never swims with the camera). Dense cores
  stay solid white. `_BreakupStrength` defaults to **0 = exact old look**; assign a tileable
  pattern (e.g. FoamTestTile) on the FoamDensityComposite material and raise the slider.
  Also names the `0.001` depth constant (`DEPTH_MM_TO_METERS`, paired with the compute's
  `DEPTH_TO_MM`).
- **Hero-size distribution** (`sizeHeroPower`, foam + roller, default 1 = unchanged): KWS pow
  bias — most particles small, rare large heroes. Applied to turbulence spawns, splash-burst
  droplets and roller emissions.
- **DropletPacked 128²** (WaterBuildKit): sharper packed droplet; delete
  `Assets/WebGLWater/Generated/DropletPacked.png` to regenerate.
- No change needed for erosion aging: SplashParticles' packed path and the foam/roller erosion
  (`FoamErosionAlpha`) already implement the KWS dissolve idiom.

## Known-open (not in scope, listed in the plan)
SpawnBurst bypasses `_MaxSpawnPerFrame`; roller struct dead bytes (`dAcross`/`birthOverCap`);
density buffers keyed to camera half-res (editor resize thrash); pause pops density→quads;
CGPROGRAM-in-URP-tags shaders; P5 backlog (indirect draw + compaction, time slicing, clumping,
Jacobian crest source, multi-window rollers); Crest black-point feather for the SURFACE foam
pattern (WaterSurface.shader — too risky blind, needs its own pass).

## Test checklist (in order)
1. **Recompile**: 3 new files (WaterParticleCommon.hlsl, WaterSurfBreakLine.cs,
   WaterParticlePool.cs) — Unity will generate .meta files. Watch the console for the
   validator's report (it now reads WaterFoamParticles.compute + WaterSplashEmitter.cs too).
2. **P1**: ocean FFT scene, foam on, fly the camera sideways/backwards — the foam field should
   stay put in the world (population turns over at the trailing edge as a fade, not a sweep).
   Check the scene view during play: no more translating foam layer (spray quads only).
3. **P2**: coastline scene, rollers on — every arriving front should now dress with foam at its
   own break point (big waves further out, small ones closer in), full rows on big sets.
4. **P3**: everything should look **identical** to before (that's the point) — foam, roller,
   curl ribbon placement, splashes. If the curl ribbon or roller window flips/spins, shout
   (continuity-flip relocation would be the suspect).
5. **P4**: leave `_BreakupStrength` 0 first (verify no change), then assign a tileable pattern
   texture on the density material and raise it; try `sizeHeroPower` ~4 on foam.
6. The `KWSWater/_stage_tmp` folder in the KWS project root is scratch from this session —
   safe to delete.
