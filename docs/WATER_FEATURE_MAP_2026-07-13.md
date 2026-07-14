# Water Feature Map — post-cleanup baseline (2026-07-13)

**Branch:** `cleanup/remove-swe-shoal-foam` (after SWE / shoal / shore-foam removal).
**Purpose:** single-owner inventory of every water feature, so the upcoming **shore** system reuses existing pipelines and duplicates nothing. "All" = Pond + Lake + Ocean.

---

## 1. Height / wave sources

Final surface height is composited in `WaterSurface.shader` `vert` and mirrored on the CPU for buoyancy. Five stacked sources:

| Feature | Owner | Produces | Body |
|---|---|---|---|
| Interactive ripple sim | `WaterSimulation.cs` + `WaterSim.compute` | ripple height/vel/normal in `_WaterTex` | All (windowed on Lake/Ocean) |
| Wind waves (sum-of-sines bank) | `WaterWaveBank.cs` + `WaterWaves.hlsl` | ambient chop height + slope | All |
| FFT ocean (Tessendorf cascade) | `WaterOceanFft.cs` + `OceanFft.compute` | 4-cascade displacement + normal + Jacobian + foam | Ocean |
| Analytic large-wave (chop + swell Gerstner) | `WaterLargeWaves.hlsl` | world swell height + choppy xz; FFT fallback | Lake/Ocean |
| Hero wave (surfable breaker) | `WaterVolume.HeroWave.cs` + `WaterHeroWave.hlsl` | overturning offset + lip sheet | Lake/Ocean |

**CPU buoyancy mirror (load-bearing):** `WaterSurfaceSampler.cs` (ripple + wind) and `LargeWaveField.cs` (swell/chop, "byte-for-byte" with `WaterLargeWaves.hlsl`). FFT ocean instead uses a GPU bake + async readback. **Any new height source must mirror CPU-side or buoyancy desyncs.**

## 2. Surface shading (all in `WaterSurface.shader` frag)

Reflection (analytic sky → planar → SSR), refraction (analytic pool vs real SSR), volume scatter + crest SSS glow (crest = ocean-FFT only), depth attenuation, **deep-water color / bed tint — the one shoreline gradient that REMAINS** (`:1077–1093`, samples `_BedTex`, tints by real column depth, `clip()`s the dry beach), ocean FFT whitecaps (Ocean only), horizon haze (Ocean clipmap only).

## 3. Foam — every surviving path

- **Ripple-sim `_FoamMask`** (advect + turbulence-gen + bi-exp decay) — `WaterSim.compute Foam` kernel. The universal near-field foam field. All.
- **Surface pond foam** = `advected` + `border` + `contact` — surface frag. `border` (pool-wall) and `contact` (depth-test waterline) are now **bounded / non-windowed only**.
- **Hero-wave whitewater** — injected as *generation into `_FoamMask`* (reuses the pond-foam renderer). Lake/Ocean.
- **Ocean FFT whitecaps** — Jacobian-fold accumulation in `OceanNormal.w`. Ocean.
- **Particle / density composite + spray** — `WaterFoamParticles`; spawns from `_FoamMask` (pools) or FFT crest (ocean). All (opt-in component).
- **Splash bursts** — `QueueSplashBurst` on the unified spray path. All.

Shared helpers: `WaterFoamCommon.hlsl` (lighting, erosion, split-decay).

## 4. Bed / depth — the shore-relevant pipeline

Baked by `WaterBedBaker.cs` → pool-space depth `_BedTex`, published with `_DeepWaterColor` / `_ShorelineDepthScale` / `_ShorelineStrength`, bound to the sim via `SetBedDepth`.

**Only two live consumers today:**
1. **Sim dry-land reflect + open-shore drain** — `WaterSim.compute Update`: dry land (`depth<=0`) holds flat so ripples reflect off the waterline; outer band drains.
2. **Surface deep-water tint + dry-beach clip** — `WaterSurface.shader:1081–1093`.

**Dead bed declarations (declared, never sampled — removal residue, now free attach points):** `FoamParticles.shader`, `LargeBodyCaustics.shader`, `WaterUnderwaterFog.shader` all declare `_BedTex` and don't read it.

## 5. Underwater

Beer-Lambert volume fog (fullscreen), wavy waterline (per-pixel surface-crossing search — the existing "where does water meet X" primitive), pool caustics, ocean near-field caustics, pool god rays, ocean god rays.

## 6. Ocean-specific

Clipmap horizon mesh (`LargeWaterClipmap.cs`, world-locked, gated `openWater && unboundedOcean && _windowed`), horizon haze, scrolling sim window, `openWater` / `unboundedOcean` flags.

## 7. Buoyancy / physics / interaction

Floater buoyancy (`WaterBuoyancy.cs`, one batched `SampleHeights` per FixedUpdate through the `IWaterHeightSampler` seam). Height-query seam: `WaterVolume.Query.cs`. Interactor ripples: mouse-drops, footprint-delta obstacle, sphere-dipole wake, passive reflection. Splash emitter.

---

## Remaining duplication / redundancy (cleanup backlog — NOT shore work)

1. **Two competing height-mirror strategies:** hand-written CPU mirrors (`WaterWaveBank`↔`WaterWaves.hlsl`, `LargeWaveField`↔`WaterLargeWaves.hlsl`) vs the FFT GPU bake+readback. A new height source must pick one.
2. **`FoamDecayBlend` opposite-semantics** between `WaterSim.compute` (survival) and `OceanFft.compute` (rate) — documented foot-gun.
3. **`foamDecay` slider is a survival factor, not a decay rate** — inverted naming vs ocean foam's true rates.
4. **Dead `_BedTex` declarations** in 3 shaders (+ a fog comment still naming the removed shoaling).
5. **Waterline recomputed three ways** (vertex, CPU analytic, fog shader) — must stay in agreement.
6. **Wind-heading world vector re-derived** in 3+ places.
7. **Anti-tiling foam octave constants** shared by pond + ocean foam looks (a whitewash band would be a third consumer).

These are optional tidy-ups; flagged so shore work doesn't add an 8th instance.

---

## Shore gaps — what a coastline needs that does NOT exist

**Missing entirely (removed, nothing replaced):**
- **Shoreline foam band** — no standing waterline foam. Pool-wall `border` is keyed to the box edge, not the real bed waterline, and is bounded-only.
- **Depth-driven breaking / run-up foam** — nothing consumes bed column-depth to spawn foam as water shoals.
- **Wet-sand / beach darkening** — nothing writes to the terrain; bed is read-only, only tints the water.
- **Run-up / swash** — the sim only holds dry land flat + drains; no advancing wet/dry waterline, no thin-film run-up.
- **Wading physics** — buoyancy has no ground-clearance / shallow-water term; `SampleHeight` returns surface only.

**Reuse these four systems (don't duplicate):**
1. **Bed depth** — `WaterBedBaker` + `BedColumnDepthWorld` already give real column depth; extend the consumer set (the dead `_BedTex` decls are pre-wired revive points).
2. **`_FoamMask` generation-injection** — add shore/whitewash foam the way the hero wave does; it advects/decays/renders for free.
3. **Particle spawn/burst API** — `WaterFoamParticles` for whitewash spray.
4. **Single height-query seam** — add bed clearance to `WaterVolume.Query.cs TrySampleWorld` so wading reuses the one batched buoyancy path.
