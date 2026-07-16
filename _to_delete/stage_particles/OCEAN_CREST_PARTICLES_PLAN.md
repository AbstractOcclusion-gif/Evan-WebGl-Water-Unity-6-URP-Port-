# Ocean wave-crest rolling foam particles — design plan

**Drafted:** 2026-07-07. Status: awaiting approval. No code yet.

Goal: spawn GPU foam particles along BREAKING FFT wave crests on the ocean, tumbling/rolling with the
wave and sitting on the crest surface — so whitecaps read as 3D churn near the camera, not just the
flat surface-shader texture. Reuse the existing `WaterFoamParticles` GPU pool + renderer. Ocean-gated;
the pond/bounded particle path stays byte-for-byte.

## What exists today (reused)

`WaterFoamParticles.cs` + `WaterFoamParticles.compute` + `FoamParticles.shader`:
- Per-body, GPU-resident, fixed pool, ring-cursor spawn (no append buffers) — WebGPU-safe.
- **Spawn**: one thread per 2D-sim texel, reads the 2D sim foam mask (`FoamTex`), spawns where
  `foam > threshold` with probability ∝ `foam · texelArea · dt`, places the particle at the texel's
  world position on the surface plane, velocity = surface flow + wind drift. A fraction become
  ballistic `KIND_SPRAY`.
- **Update**: drift/relax toward flow+wind, spray falls under gravity and converts to surface foam on
  landing, kill on age-out or leaving the sim frame.
- **Render**: procedural quads pulled from the buffer by `SV_VertexID`; surface particles glue to
  `WaveHeight` (the small wind-wave layer) and tilt by the ripple normal; spray billboards stretch
  along velocity.

## Gaps for ocean crests

1. **Source** — it only reads the 2D sim foam mask (near-field interaction ripples). Ocean whitecaps
   live in the FFT cascade `.w` (the accumulated Jacobian foam we just built), which the particle
   system never samples.
2. **Height** — surface particles glue to `WaveHeight` (wind waves), NOT the FFT swell/chop, so on real
   ocean displacement they'd float at the wrong height and slide off the crest.
3. **Motion** — nothing rolls; foam just drifts with flow.
4. **Domain** — spawn iterates the 2D sim-window texels (near-field). Fine for V1 (particles matter
   most near the camera); far crests keep the surface-shader whitecap texture.
5. **Gate** — `LateUpdate` early-outs when the 2D foam sim is off (`!volume.Foam`); a pure ocean with no
   interaction sim would spawn nothing.

## Reference (KWS `KWS_DynamicWavesFoamParticlesCompute`)

KWS spawns foam particles on breaking water using the surface **divergence/Jacobian** as the emit
signal (`FOAM_MIN_DIVERGENCE_CHANCE`), offsets them slightly above the surface (`FOAM_HEIGHT_OFFSET`),
time-slices spawning across frames, and drifts them. Their pipeline is heavier (tiled spawn, indirect
args, screen-space accumulation, ping-pong buffers) than we need. We take the *ideas* — emit on the
breaking signal, sit slightly above the surface, roll with the flow — and keep our simpler fixed-pool
ring-cursor design.

## Design (V1, ocean-gated)

**A. Render glue to the FFT surface (foundation).**
`FoamParticles.shader`: when `_OceanFftActive`, glue surface particles to `LargeBodyWaveHeight` + the
FFT choppy displacement (from `WaterLargeWaves.hlsl`) instead of `WaveHeight`, so foam rides the real
crest and its horizontal choppiness. Include `WaterLargeWaves.hlsl`; the body already publishes the
ocean uniforms via `WriteBodyProps` in `Draw()`.

**B. Crest spawn source from FFT `.w`.**
Add an ocean branch to the `Spawn` kernel (gated by a new `_OceanCrestActive` uniform): per sim-window
texel, sample the FFT foam coverage at that texel's world XZ (same cascade `.w`, distance-faded like
the surface `OceanFftFoam`), and emit where `coverage > crestThreshold` with the existing
probability/budget/ring mechanism. Bind the cascade globals (`_OceanFftNormal`, `_OceanFftDomainSizes`,
`_OceanFftVisibleAreas`, `_OceanFftCascadeCount`) — already set as Shader globals by
`WaterOceanFft.Dispatch`. Particle `strength` = coverage; place at the crest (see A) with a small
height offset.

**C. Rolling / tumbling motion.**
- Spawn velocity = wave-propagation direction (wind heading) × `crestRollSpeed` + surface flow, so foam
  moves forward with the breaking crest.
- Per-particle sprite spin: rotate the quad by `seed·2π + age·rollSpin` for ocean surface particles, so
  the foam visibly tumbles as it rolls (the vertex shader already derives a yaw from seed — add the
  age term). Keep spray as-is.

**D. Gate / integration.**
- Run the sim when `volume.Foam || volume.OceanFftActive` (so a pure ocean spawns).
- Ocean crest spawn ADDS to the pool alongside any 2D-foam spawn; both feed the same buffer.
- New knobs on `WaterFoamParticles` (with the other particle knobs): crest spawn threshold, crest spawn
  rate, roll speed, roll spin, and a crest-spray chance. Ocean-gated; defaults subtle.

## WebGPU safety
- Cascade `.w` sampled via `SampleLevel` (half-float, filterable) in the compute — fine.
- No new append/consume buffers; same ring cursor + soft per-frame budget.
- Spawn dispatch size unchanged (per sim texel). No new groupshared.

## Staged increments (small, compile-and-look, OK per increment)

1. **Render glue** — ocean particles ride `LargeBodyWaveHeight` + FFT chop. Verify existing spray/foam
   on the ocean sits ON the crests (foundation; low risk; no spawn changes yet).
2. **Crest spawn** — emit from FFT `.w` in the Spawn kernel, gated; bind cascade globals; threshold +
   rate knobs; relax the `LateUpdate` gate. Verify particles appear on breaking crests, none on calm.
3. **Rolling motion** — wave-direction advection + age-based sprite spin; roll knobs. Verify tumbling.
4. **Polish** — spray off strong crests, distance/horizon fade, budget tuning; optional wetness/foam
   into the god-ray/caustic later.

## Deferred (backlog)
- Larger camera-centred spawn grid for mid-distance crest particles (V1 spawns within the near-field
  sim window only).
- Screen-space foam accumulation / soft merge (KWS-style) if individual sprites read too discretely.
