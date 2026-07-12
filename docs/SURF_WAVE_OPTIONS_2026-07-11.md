# Surfable Rolling Wave — Options Analysis (2026-07-11)

Goal: a believable, PLAYABLE breaking wave on top of the existing FFT ocean.
Lifecycle requirement (Bert): the wave must **RISE** out of the ocean, **ROLL** (overturning crest, ideally a ridable barrel), then **REMERGE** — dissolve back into the flat ocean. An **infinite-wave variant** (endless peeling wave, wave-pool style) is also of interest.
Platform: Unity 6 / URP / WebGPU, must hold up on mobile (ocean + buoyancy + particles + god rays already proven on phone).

No code has been written. This is the option study only.

---

## 1. The one hard constraint

A heightfield is `y = f(x, z)` — one height per column. **An overturning lip is unrepresentable in ANY heightfield**: not in our FFT cascades, not in the ripple sim, not in SWE. Every real-time system in existence resolves this the same two ways:

1. **Explicit geometry** for the ridable face + curling lip (parametric mesh or vertex curl with horizontal displacement), and/or
2. **Particles** for the part that leaves the surface (spray, whitewater).

So "which option" is really "how do we drive the explicit geometry, and how much simulation sits underneath it."

## 2. What every shipped surf game does (strong evidence)

All of them — Kelly Slater's Pro Surfer (PS2, 2002, still the benchmark), Virtual Surfing, True Surf (runs on phones), Barton Lynch Pro Surfing, the 2026 Unity indie "Swell" — use the same architecture:

- An **authored parametric wave mesh** (2D breaking profile swept along a peel line), separate from the ambient ocean, blended in via world-space normals.
- **Bathymetry drives the peel**: a seabed depth texture per spot decides where the wave rises, breaks left/right, sections, closes out.
- **Physics = analytic queries, never mesh colliders**: per-frame height + normal (+ velocity) at the board; the "pocket" push is a scripted tangential force keyed on a closed-form pocket coordinate, not emergent fluid force.
- Nobody ships emergent fluid-sim barrels.

Canonical algorithm (Kelly Slater / Intel patent US7561993, **expired — free to use**): sinusoid shoaled by depth → depth-ratio threshold triggers breaking → crest vertices pulled around a moving **attractor circle** traveling along the wave to form the curl.

Key sources: FinSaltSwell "Swell" breakdown (Unity forum thread "Realistic Breaking Wave" p.7 + Jettelly bathymetry article), US7561993 patent, Fournier–Reeves SIGGRAPH '86 (depth-squashed trochoids: rise + forward-lean for free), Surf's Up wave-rig PDF (profile-curve parameterization), Unity HDRP Water "Shore Wave" decal + 6.1 horizontal-deformation "rolling wave" sample.

## 3. What we already have (audit)

| Building block | Where | Status |
|---|---|---|
| Bed-depth bake (`_BedTex`) | `WaterBedBaker.cs` | Built + wired — exactly the bathymetry signal that drives peel/break |
| Shoaling (celerity ~ sqrt(depth)) + Froude breaking criterion | `WaterSim.compute` | Built — already detects WHERE a wave breaks (spawns foam only) |
| Swell shoal attenuation | `WaterSurface.shader` `SwellShoalFactor` | Built — amplitude only, no peaking/overturn |
| Camera-following world-pinned sim window | `WaterSimWindow.cs` | Built — natural host for a localized wave domain |
| FFT crest pinch + Jacobian foam | `OceanFft.compute` `.y`/`.w` | Built — crest signal for emitters |
| Gerstner steepening + 4-iter inversion, CPU↔GPU lockstep | `WaterLargeWaves.hlsl` ↔ `LargeWaveField.cs` | Built — peaking crest is a solved, queryable primitive |
| Runtime-renderer pattern (`_IsClipmap`/`_IsPatch` vertex branches) | `WaterVolume.cs` ~2098–2206 | Built — clean slot for a new `_IsHeroWave` mesh + branch |
| GPU foam particles + crest-spawn plan | `WaterFoamParticles.*`, `OCEAN_CREST_PARTICLES_PLAN.md` | Built/specced — the crash/whitewater layer |
| Batched CPU-analytic query facade design | `BUOYANCY_IMPLEMENTATION_PROMPT.md` | Specced — the physics substrate a surfer needs |

Missing (net-new): overturning geometry of any kind, a hero-wave mesh/material/driver, the rise→roll→remerge lifecycle controller, a CPU mirror of the hero wave, and a design doc. None of the reference assets (KWS2, Crest + shallow-water, Stylized Water 3, DWP2) has a barrel either — SW3's spline `ShorelineWaveSpawner` + height/foam decal shader is the closest authoring reference; Crest's `ShallowWaterSimulation` (Shoreline preset) is the reference for a real localized shoaling solver.

## 4. Options

### Option A — Parametric hero wave (attractor-curl profile swept along a peel line) ★ RECOMMENDED
A dedicated dense strip mesh, deformed each frame by a closed-form profile: Fournier–Reeves shoaling (rise + forward-lean, driven by `_BedTex`) → depth-ratio break trigger → attractor-circle curl on crest vertices (true overhanging lip, real tube interior) → lip collapse converts curl amplitude into foam particles + whitewater front → amplitude envelope → 0 remerges it into the FFT surface. FFT displacement is evaluated in the same vertex path so the wave reads as part of the ocean.

- Lifecycle rise→roll→remerge: **yes, fully controllable** (it's an animated parameter, not an emergent event).
- Barrel: **yes** — real geometry, camera can go inside.
- Mobile: trivial — O(strip vertices) in vertex/compute shader; this is 2002-PS2-class math; True Surf runs it on phones.
- Playability: **best of all options** — closed-form CPU mirror slots straight into the buoyancy facade; pocket coordinate is free.
- Risk: art-direction effort to make the profile beautiful; multi-valued surface inside the tube (physics queries clamp to the lower/face branch — standard trick).

### Option B — SWE momentum sim (emergent breaking)
Extend `WaterSim.compute` toward momentum SWE (already on the shoreline backlog); waves shoal, surge, and spill emergently; Chentanez–Müller-style particle conversion for the overhang.

- Lifecycle: rise yes, spilling roll partially, remerge yes — but **never a barrel** (heightfield limit) and breaking is emergent = hard to art-direct and noisy for board physics.
- Mobile: coarse window feasible; grid+particle mass exchange is the expensive part.
- Playability: poor as a primary ridable surface (sim noise); games sample smoothed analytic layers instead.
- Verdict: **not the ridable wave** — but excellent later for post-break whitewater surge/backwash at the beach.

### Option C — Deformation-decal wave (HDRP-style "Shore Wave" in our write chain)
Port Unity HDRP's approach: a decal region adding Y + XZ displacement to the existing surface (our ripple/sim-window write chain can composite it), with a scripted Breaking Range lifecycle (peak → foam → −70% amplitude). SW3's spline spawner is the authoring model.

- Lifecycle: yes (scripted), roll: **steep curling face but no true overhang** unless the decal also drives a curl pass on dense geometry.
- Mobile: cheap. Playability: good (still near-heightfield; HDRP itself warns queries degrade under horizontal deformation).
- Verdict: great for **ambient shore waves** (background sets of breaking waves rolling in along a spline) and as the "rise" phase feeding Option A — not sufficient alone for a ridable barrel.

### Option D — Full 3D fluid (FLIP / Niagara-style)
Film/offline only. Minutes per frame, no queryable surface, no determinism. Useful solely as look reference and for baking crest/spray flipbooks. **Rejected for runtime.**

## 5. Recommended architecture (A as spine, C and B as layers)

1. **FFT ocean untouched** — ambient field.
2. **Hero wave module** (Option A): new runtime renderer + dense strip mesh + `_IsHeroWave` vertex branch in `WaterSurface.shader` (mirrors the existing clipmap/patch pattern). Profile params animated over the wave's life; peel position per-station from `_BedTex`.
3. **Lifecycle controller**: spawn (blend weight 0→1 out of FFT surface) → shoal/steepen → break trigger → curl travels along peel line → lip collapse → foam particles + whitewater (reuse `OCEAN_CREST_PARTICLES_PLAN`) → envelope → 0 → despawn. **Infinite-wave variant** (Bert-approved direction): hold the mid-lifecycle state — the break/curl parameters loop and the peel point either travels a long spline or holds station while the world/surfer moves; same module, just a different lifecycle driver (endless peel instead of envelope-out). Cheapest possible playable prototype, wave-pool style.
4. **Physics**: extend the planned buoyancy query facade with the hero-wave closed-form (face branch only) + a pocket coordinate for the gameplay push force. Query, never collide.
5. **Later polish**: Option C ambient spline shore-waves for background sets; Option B momentum-SWE whitewater at the beach.

## 6. Suggested phasing (no code yet — pending approval)

- **P0 — Profile prototype**: single static profile curve on a strip mesh over the flat ocean; tune the silhouette (face/lip/tube/foam-pile cross-sections). Pure look test.
- **P1 — Lifecycle + peel**: animate break parameter along the strip from `_BedTex`; rise→roll→remerge envelope; infinite-peel mode toggle.
- **P2 — Integration**: FFT blend in the hero vertex path, crest foam-particle emitter, whitewater front.
- **P3 — Playability**: CPU mirror + facade queries + pocket force; ride it with the existing buoyancy/boat controller.

## 7. Key references

- Unity forum "Realistic Breaking Wave" megathread (esp. FinSaltSwell posts, p.7): https://discussions.unity.com/t/realistic-breaking-wave/748291
- Jettelly — Simulating surf breaks in Unity with bathymetry depthmaps: https://jettelly.com/blog/simulating-surf-breaks-in-unity-with-bathymetry-depthmaps
- Intel patent US7561993 (Kelly Slater curl-attractor, expired): https://patents.google.com/patent/US7561993B2/en
- Fournier & Reeves, "A Simple Model of Ocean Waves", SIGGRAPH '86: https://dl.acm.org/doi/10.1145/15886.15894
- Surf's Up wave rig (profile-curve parameterization): https://library.imageworks.com/pdfs/imageworks-library-Surfs-Up-the-making-of-an-animated-documentary.pdf
- HDRP water deformers / Shore Wave / rolling-wave sample: https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.3/manual/water-deform-a-water-surface.html
- Chentanez & Müller, SWE + particles: https://matthias-research.github.io/pages/publications/hfFluid.pdf
- Yuksel, Wave Particles (whitewater front): https://www.cemyuksel.com/research/waveparticles/
- Local reference code: `KWSWater/Assets/Stylized Water 3/Runtime/DynamicEffects/ShorelineWaveSpawner.cs` (spline authoring), `KWSWater/Packages/com.waveharmonic.crest.shallow-water/Runtime/Scripts/ShallowWaterSimulation.cs` (localized shoaling solver)
