# Foam Particles — Full Audit, 2 Bug Diagnoses & Unification Plan (2026-07-16, evening)

Scope: every particle-foam element as it exists ON DISK right now (post P1–P4, post
density-mirror fix, including the UNCOMMITTED wave-occlusion WIP in
`WaterFoamParticles.compute`). Scene of record: `12. Ocean Demo.unity`.

ANALYSIS ONLY — no code touched. Each chunk needs your GO.

---

## 0. The foam inventory (what draws foam, with what, where)

| # | Element | Owner | Draws with | Texture(s) | Occlusion vs waves |
|---|---|---|---|---|---|
| 1 | Floating foam veil (density mode) | WaterFoamParticles | FoamDensityComposite.shader, fullscreen, **ZTest Always** | OceanWhitecap (breakup lace) | ❌ **none in HEAD** (WIP ray-march uncommitted) |
| 2 | Floating foam quads (fallback mode) | WaterFoamParticles | FoamParticles.shader, ZTest LEqual | FoamParticleAtlas_2x2 | ✅ hardware Z (water writes depth) |
| 3 | Ballistic spray (always quads) | WaterFoamParticles | FoamParticles.shader | FoamParticleAtlas_2x2 | ✅ hardware Z |
| 4 | Burst droplets (splash impacts) | WaterFoamParticles pool (via QueueSplashBurst) | FoamParticles.shader | FoamParticleAtlas_2x2 | ✅ hardware Z |
| 5 | Surf roller foam + lip spray | WaterSurfRollerParticles | SurfRollerParticles.shader, Transparent+11 | waveroller.mat atlas | ✅ hardware Z (**disabled in Ocean Demo**) |
| 6 | Splash crown flipbook | WaterSplashEmitter (Shuriken) | SplashParticles.shader | SplashFlipbook_8x8 | ✅ hardware Z |
| 7 | Legacy Shuriken droplets (no-GPU fallback) | WaterSplashEmitter | SplashParticles.shader | DropletPacked | ✅ hardware Z |
| 8 | Surface whitewash / whitecap lace | WaterSurface.shader + sims | (surface itself) | OceanWhitecap etc. | n/a |

Key structural fact discovered: **WaterSurface.shader is ZWrite On** — so every
*quad-based* foam element is already correctly hidden by nearer wave crests via the
ordinary hardware depth test. Exactly one element ignores the wave depth: the veil (#1).

Ocean Demo settings that matter: ocean body foam = capacity **65536**, spawnThreshold
**0.034** (near-zero → foam sources almost everywhere), maxSpawnPerFrame 4096,
spawnMaxDistance 400, renderMode ScreenSpaceDensity, flipbookFps 0. A second, default
foam system sits on the small pool body. Roller particles: present but disabled.

---

## 1. BUG — "foam is visible behind waves" = the density veil only

`FoamDensityComposite.shader` renders **ZTest Always** and occludes only against
`_CameraDepthTexture` (opaques: terrain, rocks). The water surface never enters that
texture, so veil foam behind a nearer crest composites on top of it. Sprites (#2–#7)
are fine — they z-test against the water's own written depth. Diagnosis: the bug is
100% localized to one pass.

The uncommitted WIP in `WaterFoamParticles.compute` (OCCLUDE_RAY_SAMPLES = 4 march of
`SurfaceWorldY` along the camera→particle ray, splat-time soft reject) is one way to do
it, but it is the expensive and approximate way:

- cost: 4 extra `SurfaceWorldY` per live floating particle per frame — each is a full
  shore-field fetch + surf-front evaluation + 4-cascade FFT loop (~5× the splat's
  previous cost, on up to 65k particles);
- correctness: 4 samples on t∈[0.35, 0.95] straddle ~one swell wavelength at mid
  distance — a crest between samples is missed, so the bug survives at grazing angles
  (the exact view where it is most visible);
- coverage: fixes the splat only; the composite's *dilated* texels can still bleed a
  couple of pixels past a crest silhouette.

### Recommended fix F-A (exact, cheap): fragment-depth z-test on the composite
The splat already stores per-texel **min eye depth**. Let the composite write it out:

1. `ZTest LEqual` instead of Always;
2. fragment outputs `SV_Depth` converted from `foamEye` (standard `_ZBufferParams`
   inversion, reversed-Z aware);
3. delete the WIP ray march (revert the +32 uncommitted lines) — the hardware test
   against the REAL rendered depth (water surface + terrain + islands + hero wave,
   whatever actually rasterized) replaces it exactly;
4. keep the existing opaque soft-fade as-is for the soft edge against rocks.

Because the veil draws at Transparent+5 — after the ZWrite-On water at +0 — the depth
buffer at that moment already contains every wave crest. Per-pixel exact, zero extra
ALU in the splat, WebGPU-safe (`frag_depth` is core WGSL). Trade-off: the cut at a
crest silhouette is hard (1 px), not a 0.35 m soft band. For a low-res dilated veil
this reads as the wave edge, which is what KWS ships too. If you want softness later,
a 1-tap version of the ray test can be layered back on top — but test F-A alone first.

---

## 2. BUG — "weird round semi-transparent spheres"

Those are the **KIND_SPRAY billboards** (and any quads-mode foam), and they are round
for four compounding reasons — measured on the real `FoamParticleAtlas_2x2.png`
(see `docs/erosion_proof.png`):

1. **The erosion function saturates the sprite into a disc.**
   `FoamErosionAlpha = saturate((a − (1−env)) / 0.35)`. At a fresh envelope this is
   `saturate(a / 0.35)`: **57% of the sprite's in-shape pixels clamp to fully opaque**
   (measured). The lacy interior becomes a solid white core with a soft rim = a ball.
   KWS's dissolve never contrast-boosts the fresh sprite; it only *thresholds away*
   pixels as life decays.
2. **Mips average the lace into a radial blob.** At 4 mip levels down (a particle a
   few metres away) the measured saturated fraction rises to 62% and the silhouette
   rounds off completely. KWS counter-measure: **mip bias −1.5** on the sprite sample.
3. **A camera-facing billboard with radial-ish alpha is a circle by construction**, and
   spray at its apex has ~zero velocity → `_VelocityStretch` does nothing exactly when
   you look at it.
4. **flipbookFps = 0** in the scene → each droplet keeps one frozen sprite for its
   whole life; a static shape reads as an object ("a sphere"), a churning one reads as
   foam.

### Recommended fix F-B (one shared function + defaults; no new art needed)
1. In `WaterFoamCommon.hlsl` (ONE change fixes foam quads, roller AND legacy splash —
   they all call it): make erosion texture-preserving —
   `alpha = spriteA * saturate((spriteA − (1−env)) / EROSION_SOFTNESS)` — fresh
   particles show the actual lace, decay still dissolves thin regions first.
4. Sprite samples get `bias −1.5` (tex2Dbias) in FoamParticles + SurfRollerParticles.
2. Spray-specific shaping in FoamParticles.shader: a small view-dependent minimum
   stretch (KWS droplets are never perfect circles), and opacity for KIND_SPRAY scaled
   by `particle.strength` (already carried) so distant spray thins instead of popping
   round dots.
3. Scene/default: flipbookFps ≥ 4 on foam (the roller already uses 4), and consider
   sprayChance 0.15 → ~0.08 on the ocean body (with threshold 0.034 the spray budget
   saturates: more spray = more spheres).
5. Optional art pass: regenerate the atlas with a higher lace threshold (more holes,
   less mid-alpha) — 20 lines in `gen_foam_particle_atlas.py`.

---

## 3. Harmonization — "each part needs its specific foam, but one place to drive it"

P3 already unified the plumbing (pool alloc, hash, billboards, shore sampler, break
solve, validator pairs). What is still scattered is the **look** and the **entry
point**:

- Look constants live in 3 places: WaterFoamCommon.hlsl (shared ✅), per-material
  sliders (×6 materials), per-component fields (×3 components).
- 4 copies of `FoamParticles.mat` exist with silently different tunings (Generated:
  opacity 1.0 / stretch 0.38; Common: opacity 0.174 / stretch 3.0; +2 more under
  Materials/). Which foam you get depends on which .mat a scene happens to reference.
- Tint/atlas/flipbook/heroPower/opacity are set per system; retuning "the foam" means
  visiting WaterFoamParticles, WaterSurfRollerParticles, 2–4 materials and the splash
  emitter.

### Proposed F-C: one **WaterFoamProfile** ScriptableObject (matches your 07-13
"foam → one master" decision)

```
WaterFoamProfile (asset)
├── Shared look: tint, sunWrap already shared; atlas, flipbook grid+fps,
│   heroPower, master opacity, erosion softness
├── Ambient foam: threshold, rate, life, size, LOD distance, veil gains + breakup
├── Spray: chance, launch, gravity, stretch
├── Roller: density, burst, tumble, tail, sizes
└── Splash: crown size/lifetime, droplet counts
```

- Each component keeps working standalone (profile field optional: null = current
  behaviour, zero migration risk).
- `WaterFoamProfile.ApplyTo(volume)` pushes the shared block into every foam component
  + material instance under that body — ONE inspector to tune a body's whole foam.
- The wizard writes one default profile; demo scenes reference it; the duplicate .mat
  copies collapse to the canonical Generated/ pair (the other demos get their deltas
  as profile assets, not divergent materials).
- Later (your "table/matrix editor" idea) a custom editor can render the profile as
  the matrix; the data model is ready for it.

### F-D: small consistency debts found this pass
- Composite still binds `_DensityInvViewProj` from the LateUpdate matrix while the
  splat uses the render-time one (documented 1-frame lace lag — fold into F-A since
  the same uniforms move).
- `FOAM_NOISE_EPSILON` still dead; roller struct still carries `dAcross`/
  `birthOverCap`/`pad` (20 dead bytes × pool) — fold into any roller-touching chunk.
- Ocean Demo: capacity 65536 = 393k verts drawn per frame mostly for dead slots
  (quads draw always runs, even in density mode, for spray). Until P5 indirect draw,
  16k is plenty (spray count is bounded by tile caps anyway).
- `SpawnBurst` still bypasses `_MaxSpawnPerFrame` (known-open from the plan).

---

## 4. Execution order (each reviewable, GO per chunk)

| Chunk | Content | Risk | Expected visible change |
|---|---|---|---|
| **B1** | F-A veil depth-test (SV_Depth + ZTest LEqual, drop WIP march) | low | foam no longer shows through waves; splat gets *cheaper* than the WIP |
| **B2** | F-B erosion + mip bias + spray shaping (+ scene: flipbookFps 4, sprayChance ↓) | low | spheres become lacy churning foam bits |
| **B3** | F-C WaterFoamProfile + material dedup | med (touch many refs) | none by default — one tuning point |
| **B4** | F-D debt sweep (dead bytes, dead knob, capacity note, burst budget) | low | none |

Recommended: B1 → B2 (your two bugs, both small), then B3 when you next want to retune.

---
*Method note: erosion numbers measured on the real atlas (`docs/erosion_proof.png`,
generated from FoamParticleAtlas_2x2.png with the shader's exact math). Veil diagnosis
from FoamDensityComposite.shader (ZTest Always, opaque-only depth tap) vs
WaterSurface.shader:62 (ZWrite On). The wave-occlusion ray march exists ONLY in the
working tree (not in HEAD `ff5b769 "foam particles clean step 1"`).*
