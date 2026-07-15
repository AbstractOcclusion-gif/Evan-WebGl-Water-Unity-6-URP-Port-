# Coastline Master Plan — shoal, swell, breakers, swash, foam

Date: 2026-07-15. Written against the **live tree** (staged from your machine tonight: `WaterShore.hlsl`, `WaterShoreDepthField.cs`, `WaterShoreSwe.cs/.compute`, `WaterLargeWaves.hlsl`, `LargeWaveField.cs`, `WaterVolume.cs` wiring, all shoreline docs), the **local KWS2 + Crest + Crest-ShallowWater source** in `KWSWater/`, Crest's public repo/docs/`experiment-sws` branch, and the 2007–2026 shoreline literature. **No code. Plan only.**

---

## 0. TL;DR — the road I recommend

Your instinct in `SURF_WAVE_OPTIONS` was right and your Layer plan is 80% right. The one strategic correction: **stop making the SWE responsible for the beauty.** Two SWE attempts have now failed or under-delivered, and that is not bad luck — *no shipped game makes emergent SWE produce its coastal waves*. HFW, God of War, Uncharted, Atlas, KWS1, Sea of Thieves all ship **analytic/authored shore waves driven by a shore-distance field**, and use simulation (if at all) only for swash/interaction/foam advection. Crest's own docs call realistic shoreline waves "a challenging open problem" and its SWS ships with heavy caveats ("not one-click instant shorelines").

So the backbone becomes:

1. **Keep Layer A** (depth + SDF substrate) — it's the right call and correctly built, with four fixable defects (§2).
2. **Upgrade Layer B from "attenuation" to a true shoal transform** — refraction toward shore, SDF-driven phase compression, Green's-law amplification, steepness increase, and a **breaking cap that emits a "breaker signal"** instead of silently clipping. Attenuation-only is why your shoal looks dead: real shoaling makes waves *grow and sharpen* before they die. All shader-only, WebGPU-safe, CPU-mirrorable (buoyancy stays exact — no readback).
3. **NEW Layer C′ — analytic breaker wavefronts** (the money layer): periodic wave fronts whose phase is a function of **SDF distance + time**, so crests are always shore-parallel on any coastline shape; each front runs the KSPS/Fournier–Reeves profile lifecycle (rise → steepen → curl → whitewash → dissolve). You already own the profile machinery: `WaterHeroWave.hlsl` *is* this renderer, hand-placed. C′ is "hero-wave profile math, auto-driven by the SDF". This is the KWS1 look **without the baked flipbook assets** and without manual placement.
4. **Demote the SWE to an optional swash/interaction enhancer** — thin band at the waterline (|sdf| < ~12 m), fixed after the structural bugs in §2 (as written, C1 *cannot* produce run-up — see B6). Run-up/wet-sand ships first as a cheap analytic swash that needs no sim at all.
5. **Foam rides the existing systems** — breaker signal + Jacobian whitecaps + shore band injected into `_FoamMask` (the hero-wave pattern), plus `WaterFoamParticles` spray at curl lips.

Everything above is GPU-texture + ALU only (no readback), half-float-safe, and each layer is independently killable — the shoal transform alone already looks like a coastline; each layer after it multiplies the believability.

---

## 1. Why the first implementation failed and why the re-clean is already buggy

Short version of the ledger, so the plan can be checked against it:

- **Old stack (removed):** `SwellShoalFactor` height seam (had to stay in sync across ~6 files — the mirror tax), ripple-sim SWE with no real wet/dry, shoreline foam keyed to the pool rectangle. Root causes: *no world-frame depth/SDF substrate* and *no single height source of truth*.
- **Re-clean (current):** Layer A largely healthy. Layer B/C1 have the concrete defects below — several are look-killers on day one, one is structural.

### Audit of the current start (verified against tonight's files)

**B1 — The FFT ocean never shoals.** `WaterLargeWaves.hlsl`: `LargeBodyWaveHeight/Displacement/ApplyLargeBodyWaveNormal` early-return into the `_OceanFftActive` branch *before* `ShoalWeight` is ever applied; shoaling only exists in the analytic fallback. On the one body type a coastline is for, depth changes nothing. (Crest solves this per wave-band at spectrum-input time; for our sampled cascades the equivalent is per-cascade attenuation using each cascade's representative wavelength — §5 P0.)

**B2 — Attenuation-only shoal reads as "waves dying", because physically it's half the phenomenon backwards.** Real shoaling: phase speed drops (`ω² = gk·tanh(kd)`), wavelength shortens, crests bunch and **amplitude RISES** (Green's law `a ∝ d^-1/4`) until `H ≈ 0.78·d` breaks it. `ShoalWeight` only ramps amplitude to zero. That's why "simple shore is easy but beautiful one…" — the beauty *is* the grow-sharpen-break part.

**B3 — `_ShoreShoalDepth` default 4 m turns the whole effect into a razor band.** `ShoalWeight` lerps the Crest ramp to 1 at `depth ≥ shoreShoalDepth` (`WaterShore.hlsl:53`, default 4 in `WaterVolume.cs:1057`). A 30–60 m swell then goes from untouched to dead across the last ~4 m of depth — a visible wall, with a derivative kink at exactly d = 4 m. (Crest's `MaximumAttenuationDepth` defaults to effectively *off*, and their docs explicitly warn about the "step + line of foam" this clamp creates.)

**B4 — RHalf world-height precision banding.** `WaterShoreDepthField` stores **absolute seabed world Y** in RHalf. Half precision near e.g. Y=60 m is ~3 cm, near 500 m it's 25 cm; `depth = level − seabed` subtracts two similar numbers, so shallow depths quantize into visible terraces → banded shoal rings and jittery SDF seeds on gentle beaches. Store `waterLevel − seabedY` (small numbers near the shore, where precision matters) or R16-unorm-normalized depth instead.

**B5 — Hard seam at the depth-field border.** Outside the field, `ShoreShoalDepth()` returns the deep sentinel → weight snaps to 1. On an ocean with a terrain island, the terrain's bounding rectangle prints itself into the swell. Needs a border feather (fade shoal/refraction influence to zero over the outer ~10% of field UV).

**B6 — STRUCTURAL: the C1 SWE cannot produce run-up, ever.** Wet/dry is decided by the **static** still-water test `ColumnDepth(w) <= 0` — and dry cells are hard-reset to zero state every substep (`WaterShoreSwe.compute:164,219`), while `StepVelocity` zeroes any velocity into a "dry" neighbour. So the sim's water can never cross the still waterline: run-up — the *point* of Layer C — is forbidden by construction. Wetness must be **dynamic** (a cell is dry when `h ≈ 0` *now*, not when it sits above sea level): Crest SWS zeroes velocity only when *both* neighbouring columns are < 1 mm; KWS2 keeps terrain in `.w` and lets water flow onto beach cells. This is the same "hold flat at the waterline" behaviour the removed sim had — rebuilt.

**B7 — The pump feeds the SWE pre-killed waves.** `SwellTarget()` multiplies by the *Layer-B* `ShoalWeight` — the same function that goes to zero at the shore — so precisely where the zone should receive energy to break, the target is ~0. KWS2 injects the **raw FFT displacement** (`AddFFTWaves`: `crestWave = clamp(fftDisp.y * 0.01 * influence)`, pushed along `shoreDir`, verified in `KWS_DynamicWavesHelpers.cginc:294-312`) and lets the SWE itself do the shallow-water physics. Also: the pump's `omega = sqrt(g·k)` is deep-water dispersion inside a shallow zone, and it models only the primary analytic swell — on an FFT ocean the zone's waves won't phase-match the rendered surface, so C2's blend would double-crest.

**B8 — Collocated grid with no smoothing pass.** The forward-diff pressure gradient + averaged-face upwind flux on a collocated grid is exactly the scheme that grows checkerboard noise; C1 deliberately deferred Crest's `HOvershootReduction`/`BlurH`. KWS2 additionally runs vorticity confinement, Manning + linear drag, and renders the zone **bicubically**. C1 as written will read spiky/noisy at 0.75 m texels.

**B9 — The camera-following zone eats its own waves.** The edge drain multiplies displacement every substep (up to 120×/s) in the outer 6% UV band — but the band *moves with the camera*. Walk toward the beach and the drain band sweeps across your surf, visibly deleting it. Crest SWS uses a world-anchored placement for shorelines (its camera-follow mode is explicitly shoreline-only + reset-prone) and an FFT-blend mask rather than a hard drain.

**B10 — Buoyancy/waterline split at the beach (known, accepted, but list it).** `LargeWaveField.cs` has zero shoal terms, and the waterline is computed three ways (vertex / CPU analytic / fog shader). Any render-only shore height means floaters and the underwater waterline disagree near shore. Fine for C1 debug; not fine silently in the shipped feature — the analytic road in this plan keeps everything mirrorable, which is the cure.

**B11 — SDF direction quality.** Direction = vector to nearest JFA seed: it's piecewise-constant per Voronoi cell and flips hard on the medial axis. Good enough for masks; too harsh to steer refraction/wavefronts directly. One 3–5-tap smoothing of the direction field (KWS2 runs a dedicated blur pass on its SDF) makes it usable everywhere.

Also noted in passing (not shore, but adjacent): Layer A's bake is CPU whole-terrain (fine as v1 — static, one-time, honest about its `useBedDepth` gate), single-`Terrain` only; the plan keeps it and only upgrades content/precision, not the mechanism.

---

## 2. What the references actually do (validated, with sources)

### KWS2 (source in `KWSWater/Assets/KriptoFX/WaterSystem2/`)
Ortho seabed depth → jump-flood SDF (+ direction blur) → **local Saint-Venant zone**: state `(velX, velY, height, extra)`, semi-staggered averages, Manning + linear drag, vorticity confinement, `OceanRelax` bleeding displacement to sea level *outside* the shoreline mask, and FFT injection along `shoreDir` scaled `lerp(2, 0.5, windIncoming)` (`KWS_DynamicWavesHelpers.cginc:278-312`). Foam (`GetFoamMask:327-…`) is the best single reference in either package: **advected** previous foam (velocity + noise, advection scale fading with `sdfDepth`), then sources = crest peak (height-above-neighbours × curvature), **breaking front = `smoothstep(0.015,0.08,slope) × compression(−divergence)`**, turbulence (|curl| + shear, damped where compressing), shoreline shallow band gated by **Froude number** `speed/√(g·h)`, all decayed with `DecayFoamFastThenSlow`. Zone is sampled **bicubically** by the surface with normals derived from the zone (`KWS_WaterHelpers.cginc:1978-2056`). Breakers are *spilling* fronts + foam — no overturning lip; KWS1's baked flipbook quads were how Kripto got plunging visuals.

### Crest (packages in `KWSWater/Packages/com.waveharmonic.crest*`, repo + docs)
- Shoaling = **wavelength-dependent amplitude ramp only**: `weight = saturate(2·depth/λ_band)` at wave-input time, optional `MaximumAttenuationDepth` floor (`AnimatedWavesGenerate.compute:188-206`; docs: "affected by the seabed when depth < λ/2", and the warning about depth-cache steps producing a foam line). No refraction, no phase change, no amplification — which is exactly why bare Crest shorelines look like waves fading out, i.e. what you have now.
- **Crest SWS** (`ShallowWaterSimulation.compute` + the public `experiment/sws` branch): explicit SWE, semi-Lagrangian advection (optional MacCormack), `UpdateH` continuity with upwind flux + boundary drain, `UpdateVels` gravity on free-surface gradient with **wet/dry = both-columns-under-1mm** rule (this is what makes its beach run-up work), `HOvershootReduction` + `BlurH` stabilizers, friction `∝ dt/h^(4/3)`. Ocean→sim coupling: pump where FFT surface > sim surface, `maskWeight = mask·4·(1−mask)` peaking mid-blend-band; sim→ocean: sim **replaces** FFT near shore (alpha = mask²), plus Flow + Foam injection. Caveats in their own manual: terrain shape dominates, resets on property change, TAA shimmer, "water wall creep" at edges (they ship a `WaterEdgeMargin`).
- Foam sim (`UpdateFoam.compute`): Jacobian pinch + shoreline term `saturate(1 − depth/maxDepth)` sampled at the **displaced** position, flow-advected, exponential decay — almost identical to KWS2's, confirming Layer D's shape.
- Wave Splines: ribbon mesh extruded along a spline, per-vertex axis ⊥ spline + `invNormDistToShoreline`, feathered, **Blend mode replaces global waves** so open-ocean waves don't fight the authored shore waves. (Their answer to alignment is *authoring*, not simulation.)

### KWS1, and what it teaches
Baked breaker flipbooks (displacement/normal/alpha VAT, 14×15 @ 18 fps) on GPU-instanced quads rendered into a 2048² camera-area displacement RT the surface adds; foam as pre-baked particle-splat buffers; **manual spline placement**. Lesson: the *look* of a plunging breaker is a **profile animation problem**, not a fluid-dynamics problem. We reproduce the profile procedurally (below) instead of shipping their baked assets.

### Industry & papers (the menu, condensed)
- **HFW (SIGGRAPH 2022, Malan)** — the best single industry reference for this plan: beach waves = periodic wavefronts generated **from the shore-distance field** (phase = f(distance-to-shore, time)), profile + foam textures on top, blended into the FFT ocean. Exactly C′.
- **Uncharted (GDC 2012)** / God of War / Atlas (GDC 2019) — same family: authored/SDF shore waves + wave particles for interaction; Atlas layers an interactive displacement sim *on top of* FFT.
- **Kelly Slater's Pro Surfer patent US7561993 (expired)** + Fournier–Reeves '86 — depth-squashed trochoid + attractor-circle curl = the parametric plunging profile; your `SURF_WAVE_OPTIONS` Option A, and the math already living in `WaterHeroWave.hlsl`.
- **Jeschke & Wojtan 2020 "Boundary-aware procedural waves" (wave cages)** — post-process ANY procedural wave field with an SDF so waves attenuate/reflect correctly at boundaries; near-free; academically blesses the SDF-warp road. (Their 2018 Water Surface Wavelets is the heavyweight alternative — real refraction/diffraction — but 4D amplitude grids; not for WebGPU v1.)
- **Thürey et al. 2007** — SWE + breaking-front particle sheets: the reference *if* we later want emergent breakers from the SWE band.
- **NVIDIA 2023 "SWE with dispersive surface waves"** — the one paper that unifies deep dispersion + shallow flooding in one heightfield sim; flag for the far future.
- Foam: Jacobian whitecaps (standard since Tessendorf), accumulation/decay buffers (Sea of Thieves 2018) — you already run both patterns.

---

## 3. The fork, decided honestly

| | SWE-emergent breakers (KWS2/Crest-SWS road) | Analytic wavefronts + shoal transform (HFW/KWS1/KSPS road) |
|---|---|---|
| Breaker look | Spilling fronts + foam only; never a curl (heightfield) | Full profile control incl. plunging curl; art-directable |
| Alignment to shore | Emergent (good) but noisy | Perfect by construction (phase from SDF) |
| Stability risk | High — two failures already; CFL, wet/dry, seams, TAA | Near-zero — pure functions of (xz, t) |
| Buoyancy | Blocked by WebGPU no-readback | **CPU-mirrorable exactly** (pure ALU) — floats can ride shore waves |
| Cost | 2 kernels × substeps × 256–512² every frame | Vertex/fragment ALU only where near shore |
| What it's genuinely best at | Swash film, backwash, interaction, foam-advection velocity | The visible coastline: shoal, swell alignment, breakers |

**Decision proposed:** analytic backbone (P0–P4) ships the coastline; the SWE returns later (P6, optional) as a *narrow swash/interaction band* with the §2 fixes — or never, if analytic swash (P4) already satisfies. This also honors "reuse, never rewrite": the profile renderer (hero wave), foam mask/injection, particle system, sim-window idiom, clipmap — all reused; nothing thrown away, including C1 (it becomes the P6 seed, fixed).

---

## 4. Target architecture

```
              Terrain (static)
                    │  one-time CPU bake (existing WaterShoreDepthField)
                    ▼
   [Layer A]  _ShoreDepthTex (depth-from-level, banded-precision-fixed)
              _ShoreSDFTex   (smoothed dir, signed dist, mask)        ── P0 repairs
                    │
        ┌───────────┼──────────────────────────────┐
        ▼           ▼                              ▼
 [P1 Shoal xform] [P2 Breaker wavefronts]   [P4 Analytic swash]
  refraction       phase = f(sdfDist, t)      waterline oscillation
  phase compress   profile: rise→curl→foam    + wet-sand mask RT
  Green's amp      (reuses HeroWave math)     (no sim)
  break cap ──────► "breaker signal" ──┐
        │                              ▼
        │                    [P3 Foam] whitewash band + Jacobian caps
        │                     → _FoamMask injection (existing sim advect/decay)
        │                     → WaterFoamParticles spray at lips
        ▼
  CPU mirror (LargeWaveField + WaveBank): SAME closed-form terms → buoyancy exact
                    │
 [P6 optional] SWE swash band (C1 fixed: dynamic wet/dry, raw-wave pump,
               overshoot+blur, world-anchored band) — interaction & backwash only
```

Composition rule (the anti-double-crest rule, from Crest SWS & Wave Splines): near shore the breaker layer **replaces** ambient waves rather than adding — ambient (FFT/analytic) height is faded by the same mask that fades the breaker layer in (`w = smoothstep` on sdfDist), so total energy stays sane. One mask, published once, used by every layer.

---

## 5. Phased plan (each phase compiles, revertible, gated; fresh `_Shore*`/`_Surf*` names only)

### P0 — Repair pass on A+B (small, 1 session)
Fix B1 (per-cascade FFT attenuation using each cascade's representative wavelength — apply in `OceanFftDisplacement`/normal/foam sampling path, weight per cascade like Crest's per-band input weighting), B3 (raise default `shoreShoalDepth` to ~0.5× dominant swell λ and smooth the clamp; expose as "shoal band depth"), B4 (store depth-relative values, re-gate precision), B5 (field-border feather), B11 (direction smoothing pass in the CPU bake — 3×3 few iterations is enough at bake time).
*Gate:* shoal responds on the FFT ocean; no banding on a 1:50 slope; no rectangle seam; debug SDF direction field is smooth.
*WebGPU:* unchanged (textures + ALU). *Buoyancy:* unchanged (still render-only until P5).

### P1 — Shoal transform (the "swell becomes coastal" phase)
In `LbwAccumulateBand` (and the FFT-path equivalent for direction/phase where feasible — see note), per component:
1. **Refraction:** `dir' = normalize(lerp(dir, −shoreDir, r(kd)))`, `r` ramping in as `depth/λ` drops (Snell-flavoured heuristic; every reference uses a variant).
2. **Phase compression:** near shore, replace plane phase `k·dot(dir, x)` with `k·(dot(dir,x)·w + s(sdfDist)·(1−w))` where `s` accumulates shortened wavelengths as depth drops (finite-depth `k(d)` from 2-term Padé of `tanh`); `w` = same shore mask. Crests turn shore-parallel and bunch — the single strongest visual cue.
3. **Green's law:** `a *= clamp(pow(d_ref/d, 0.25), 1, ampCap)` inside the shoal band.
4. **Breaking cap:** where `2a > 0.78·d`, clamp the height and write the **excess** to a `breakerSignal` (varying → fragment; also a small RT if Layer D wants it advected). Nothing visual yet — it just stops the punch-through and becomes P2/P3 fuel.
5. **Sharpening:** raise effective Gerstner Q with `1/(kd)` (clamped) so crests cusp before the cap hits.
*FFT note:* the FFT cascades can take amplitude + a modest phase-warp via sampling-position warp, but not per-component refraction; on ocean bodies the analytic swell band (which layers *on top of* FFT already via the same interface) carries the refracted/compressed shore swell, while FFT keeps deep-water texture. This division (FFT = deep texture, analytic = shore behaviour) is exactly HFW's.
*Gate:* on the terrain-lake and an FFT ocean island: crests align to any beach shape, compress, visibly grow then cap; pools byte-identical; no seams camera-moving.
*WebGPU:* ALU only. *Buoyancy:* not yet mirrored (P5) — note the known waterline split.

### P2 — Breaker wavefront layer (the money layer)
A new `WaterSurfWaves.hlsl` evaluated in the same vertex path (and mirrored in the fragment for normals), gated by the shore mask:
- **Wavefront field:** `phase = (sdfDist / L_surf + t / T_surf)`; front index = `floor`, front-local coordinate = `frac`. Every front is automatically shore-parallel, spacing `L_surf` compresses with depth (reuse P1's `k(d)`), period `T_surf` sets the set rhythm; a per-front hash (front index + coarse alongshore coordinate from the SDF seed direction) varies amplitude so sets feel natural (waves come in sets — cheap and hugely convincing).
- **Profile:** front-local coordinate drives the Fournier–Reeves/KSPS profile you already have in `WaterHeroWave.hlsl` — rise → forward-lean → curl (with horizontal displacement; true overhang optional per quality tier since it needs the dense near-shore tessellation you already get from the clipmap near rings) → collapse into a **whitewash bore** (a foam-carrying rounded step that keeps travelling shoreward, shrinking with depth) → dissolve at the waterline into P4's swash.
- **Lifecycle by depth, not by time:** the profile parameter is driven by *local depth* (`H/d` ratio), so each front breaks exactly where the bathymetry says — sandbars produce outer break lines for free.
- **Blend:** the mask fades ambient waves down as fronts fade in (§4 composition rule; Crest Wave-Splines "Blend" semantics).
*Gate:* on an irregular coastline, sets of waves roll in parallel to shore, break over the sandbar and again at the beach, whitewash slides shoreward; horizon and deep ocean untouched; hero wave still works on top; stable while the camera flies along the coast.
*WebGPU:* ALU only. *Perf:* bounded by the shore mask (skip all of it where `sdfDist > band`).

### P3 — Foam: whitewash + waterline band (rides on P1/P2 signals)
Adopt the KWS2 foam-source recipe with our plumbing: sources = P1/P2 `breakerSignal` + front-profile whitewash zone + Jacobian pinch (already computed for FFT; the analytic band's `dispDeriv` gives the same determinant) + shallow-depth waterline band `saturate(1 − depth/maxDepth)`; **inject as generation into `_FoamMask`** exactly like the hero wave does today, so advection/decay/rendering are free; keep ocean whitecaps separate (they already work) and use the mask split so the two never double-count. Spray: `WaterFoamParticles` spawn from the curl-lip signal (the crest-particles plan doc already specs this). Foam texture: reuse `FoamFlipbook_4x4` + whitecap octaves (anti-tiling constants already shared).
*Gate:* foam is born on breaking fronts, rolls shoreward with the bore, accumulates in a live waterline band that hugs any terrain shape, decays offshore; no pool-rectangle artifact; whitecaps unchanged.
*WebGPU:* existing foam path (already WebGPU-hardened, incl. the W1 manual-bilinear fix from the audit — do W1 while in there).

### P4 — Analytic swash + wet sand (run-up without a sim)
- **Swash:** the waterline itself oscillates — clip/extend the surface's dry-beach `clip()` line by `runup(t, alongshore) = R·profileTail(phase at sdf=0)`, i.e. the P2 wavefront field evaluated at the waterline drives a thin "sheet" of water sliding up and back over the beach (a few metres of extra skirt geometry along the waterline, or simply the existing surface with the clip threshold animated — decide in phase 0 of P4). God-of-War-class beaches do exactly this.
- **Wet sand:** a small persistent world-frame RT (`_ShoreWetTex`, R8, same frame as Layer A) — each frame, texels under the current swash line saturate to 1, then exponential dry-out; the bed/terrain tint path (`WaterSurface.shader:1081-1093` + the revived dead `_BedTex` attach points) darkens by it. This is a 1-kernel compute, no readback, and it's the single cheapest "expensive-looking" feature on the list.
*Gate:* waves arrive → waterline advances/retreats in rhythm with P2 fronts; sand darkens and dries; buoyancy unaffected (swash is above the still line).

### P5 — CPU mirror / buoyancy truth (the Wall-1 decision, scoped down)
P1/P2 are pure closed-form functions of (xz, t) — mirror the *shoal band + wavefront* terms in `LargeWaveField.cs`/`WaveBank` (or land the Master-Base-Clean Option 1 bake first if you still want to delete the mirrors — this plan works with either; the shore terms are one function either way). Then floaters feel shoaled swell and breaker fronts, and the three waterline computations re-agree.
*Gate:* buoy on the beach rides an incoming front; waterline/fog/vertex agree; pools unchanged.

### P6 (optional, later) — SWE swash/interaction band, fixed
Only if P4's analytic swash isn't enough (interaction: boat wakes surging up the beach, objects pushed by bores). Reforge C1 with: **dynamic wet/dry** (Crest SWS both-columns rule) [B6], pump = **raw incoming wave** (P2 front field, not the attenuated target) along smoothed `shoreDir` [B7/B11], `HOvershootReduction` + light blur [B8], **world-anchored band along the shoreline** (allocate over `|sdf| < 12 m` of the *visible* coast, not a camera square; drain only at the offshore edge, not wherever the camera wanders) [B9], bicubic zone sampling for the surface, and Crest's `maskWeight = mask·4·(1−mask)` coupling window. Foam then optionally advects by SWE velocity instead of ripple-sim velocity near the waterline.
*Gate:* long-run stable (hours), no NaN, no camera-follow artefacts, interaction visibly superior to P4 — otherwise it stays off and C1's files remain the seed.

---

## 6. Bulletproofing — every recorded failure → its guard in this plan

| Recorded failure | Guard here |
|---|---|
| SwellShoalFactor 6-file mirror tax / height seam | Shore terms live in ONE hlsl include; CPU mirror deferred to a single dedicated phase (P5) with a parity gate — or the Option-1 bake removes mirrors entirely |
| SWE saga (×2): no run-up, instability, seams | SWE demoted to optional P6; structural bugs (B6–B9) named with the exact reference mechanics that fix each; coastline no longer depends on it |
| Coordinate-frame saga | Everything consumes Layer A's one world frame; the only new frame (P4 wet RT) reuses it verbatim |
| Pool-rectangle "shoreline" foam | All foam keyed to SDF/depth, never body bounds; bounded pools keep their border band untouched |
| WebGPU plane-vanish (NaN poisoning) | P1/P2 are pure ALU (no state to poison); P4's RT is saturate-clamped; P6 inherits C1's isfinite/clamp discipline + overshoot pass |
| WebGPU float32-filter / readback walls | New textures: R8/half only; zero readback anywhere; every sampler fallback-bound (existing rule) |
| `_Shoreline*` naming collision | New prefixes `_Shore*` (already established) + `_Surf*` for P2; grep-verified free |
| Foam decay semantics foot-gun | P3 reuses `_FoamMask` generation-injection only — never touches the decay knobs |
| "Distance-empty water" / clipmap gating | P2 band is masked by sdfDist, evaluated in the same vertex path on clipmap and patch alike — no second gating system |
| Waterline triplication | P5 gate explicitly re-verifies all three agree after shore terms land |

Perf budget (phone-honest): P0/P1 ≈ a few extra texture reads + ~30 ALU per near-shore vertex; P2 ≈ profile math only inside the mask; P3 ≈ existing foam cost + 1 injected source; P4 ≈ one tiny R8 kernel. The only frame-persistent new sim before P6 is the wet-sand RT. Quality tiers: curl-overhang off / front count reduced / swash amplitude only, on the existing `WaterQuality` tier plumbing.

Suggested review order per phase stays your rule: Phase-0 read-only grounding (verify hook points against live tree) → smallest reviewable chunk → in-editor eyeball gate → commit.

---

## 7. Questions (end, as requested — none block P0/P1)

1. **P2 fronts vs FFT choppiness:** when a breaker front replaces ambient waves near shore, do you want the FFT's *small* detail (ripples/normal texture) kept on the front faces (HFW does: detail rides the front)? Recommended yes; costs one extra normal blend.
2. **Curl overhang tier:** true overhanging lips need the dense near-ring tessellation — is current clipmap near-ring density acceptable to lean on, or should P2 ship spilling-profile-only first and add the curl in a P2b?
3. **P5 vs Master-Base-Clean:** do you want the Option-1 unified GPU bake *before* P5 (delete mirrors once, then shore terms are bake-only), or keep the hand mirror one more round? Plan works either way; my lean is bake-first if you're touching buoyancy anyway.
4. **KWS1 hero flipbooks:** with P2 delivering procedural breakers, the hero layer likely stays your existing hand-placed `WaterHeroWave` (now sharing the P2 profile). OK to drop the baked-flipbook idea entirely?
5. Confirm the default `shoreShoalDepth` semantics change in P0 (from "4 m hard band" to "λ-derived band") is fine to alter saved scenes' look.
