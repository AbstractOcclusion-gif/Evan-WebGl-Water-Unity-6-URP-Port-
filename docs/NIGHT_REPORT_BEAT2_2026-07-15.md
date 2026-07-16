# Night Report — BEAT-2: seam + vertex unify + WGSL hardening + inspector fold + ROLLER v1 (2026-07-15)

**No git operations were made.** Plain file edits on top of your working tree (which already
carries this afternoon's BEAT-1). `git diff` shows the whole night; revert per file if needed.
Code was written blind (no Unity here), then adversarially reviewed by an independent second
pass which verified every consumed API against your sources, checked CPU/GPU parity term by
term, balanced the braces, and found + fixed 1 real bug (see ROLLER notes). Expect at most a
trivial compile fix on first open.

## Files changed (8) + 3 NEW

| File | What |
|---|---|
| `Runtime/Shaders/WaterSurfWaves.hlsl` | SEAM: per-front amplitude cross-fades to the neighbour near cell edges (`SURF_EDGE_BLEND_START 0.35`) — kills the shore-parallel height/foam step + FD normal spike marching mid-way between bores (the "sparkle line") |
| `Runtime/LargeWaveField.cs` | SEAM mirror (lockstep, validator-guarded) |
| `Editor/WaterWaveConstantsValidator.cs` | + `SURF_EDGE_BLEND_START` pair |
| `Runtime/Shaders/WaterLargeWaves.hlsl` | NEW `LargeBodyWaveHeightDispShore()` — height + chop from ONE field evaluation (FFT cascades sampled once for both; analytic band loop runs once) |
| `Runtime/Shaders/WaterSurface.shader` | (a) vertex now hoists ONE ShoreSample + ONE EvaluateSurfWaves shared by height/chop/swash (was ~2.5x the whole field per vertex); (b) swash film gates now match the fragment exactly (`_BedValid` added — no more floating film if the pool-frame bed bake fails) and sample the SOURCE xz like the fragment (film + glaze breathe on the same phase under chop); (c) WGSL derivative hardening: every foam-pattern/whitecap/wall/caustic/sky sample reached from non-uniform branches now uses explicit gradients hoisted in uniform flow (tex2Dgrad / texCUBEgrad) — fixes potential mip sparkle at foam edges on WebGPU and satisfies strict Tint/naga validation |
| `Editor/WaterVolumeEditor.Appearance.cs` | Inspector fold: hero knobs (Enable, Amplitude + "Effective: X m" readout when the swell floor is raising it, Wavelength Auto/manual + derived readout, Period) always visible; everything else in a collapsible **Advanced** subsection (Shoal transform / Front shaping / Crest segmentation / Swash / Foam) |
| `Editor/WaterVolumeEditor.Inspector.cs` | `_showSurfAdvanced` foldout state |
| **NEW** `Runtime/WaterSurfRollerParticles.cs` | The dedicated rolling-wave particle system (component) |
| **NEW** `Runtime/Shaders/WaterSurfRoller.compute` | Emit/Update/ClearCounters kernels |
| **NEW** `Runtime/Shaders/SurfRollerParticles.shader` | No-stretch billboard renderer |

## ROLLER v1 — what it is

The dedicated surf particle system you asked for (KWS1-style read, procedural — no baked assets),
a SIBLING of the ambient foam particles, never fighting their budget:

- **Phase-locked emission**: a world-anchored 120 m window along the break line (solved on the
  CPU from the shore arrays, same pattern as the curl ribbon). A slot emits EXACTLY ONCE per
  front, at the moment that front's crest arrives — driven by the master beat, not by random
  rolls on the foam texture.
- **Motion IS the wave**: a roller particle re-derives its position from the front field every
  frame (2-Newton-step inversion of the compression warp tracks its crest) plus a tumble orbit —
  it rides the wave exactly, by construction. It cannot wash away; there is no drag, no wind, no
  flow advection. A share (default 25%, plunging fronts) is thrown as ballistic lip spray that
  dies on landing.
- **Lifetime = the front's life**: born at cresting, opacity follows whitewash/breaker, dies a
  tail after `broken` completes. Set lulls emit nothing.
- **No stretching, ever**: fixed-size camera-facing billboards (slow seed spin only) — the
  velocity-stretch block of the ambient system simply does not exist here. Same foam atlas +
  lighting helpers, so it matches the rest of the foam.

### Setup (2 minutes, Ocean Demo scene)
1. Select the ocean `WaterVolume` (bed depth + SDF baked, surf enabled — your current setup) and
   add **AbstractOcclusion → Water → Water Surf Roller Particles** (on the volume or a child).
2. Assign `particleCompute` = `WaterSurfRoller.compute`.
3. Create a material from **AbstractOcclusion/WebGpuWater/SurfRollerParticles**, give it your
   foam sprite atlas (`FoamParticleAtlas_2x2` works), assign it to `particleMaterial`.
4. Play: rollers should appear as churning clumps riding the breaking fronts near the camera,
   spray off plunging lips. Knobs: `particlesPerMeter`, `size`, `tumbleSpeed`, `spraySharePct`,
   `lifeTailSeconds`, `masterGain`.

### Reviewer-fixed bug (worth knowing)
The emit dedupe originally reconstructed "last frame's phase" from raw `Time.deltaTime`; with a
body `timeScale != 1` it would double-fire or skip fronts. Fixed: the kernel now gets the beat
clock's true per-dispatch delta (`_BeatDeltaTime`), which also makes the 3.2 h beat-wrap safe.

### Known limitations (honest)
- Roller height = still plane + front height (no FFT swell glue) — correct at the break line
  where the ambient fade owns the surface; don't expect them far offshore.
- The emission window follows the camera with 0.75 s smoothing; during fast camera moves a slot
  can rarely double-fire/miss one front (inherent to the follow design; self-limiting).
- v1 has ONE window (the coast near the camera) — distant coastline gets no particles yet.
- Spray dies on landing (does not convert into wash foam) — deliberate for v1 so the system
  stays self-contained; hand-over to the ambient system is a v2 option.

## Also inherited from this afternoon (already on disk, untested together with tonight)
BEAT-1: master beat clock + wrapped front index, one compression warp, Auto wavelength
(⚠ overrides hand-tuned `surfWavelength` until unticked), curl no-retract fix, whitecap
foam-gate alignment, validator pairs.

## How to test (10 min, Ocean Demo)
1. Open the scene, recompile — validator should stay silent.
2. Shore look: no shore-parallel seam line mid-way between bores even with Set Strength +
   Crest Variation high; normals clean at the whitewash edges.
3. Swash: film + wet glaze breathe together under choppy swell (they used to sample different
   points).
4. Inspector: Bed Depth → Surf breaker fronts shows 4 hero rows + Advanced fold; amplitude
   shows "Effective: …" when below the swell height.
5. Roller particles: setup above; check phase lock (particles arrive WITH each front, never
   drift behind), no elongated/stretched sprites anywhere, spray only off plunging sections.
6. Perf sanity: vertex cost should be measurably DOWN on shore-heavy views (field evaluated
   once per vertex instead of ~2.5x).

## If something is broken on open
Shader errors will name `WaterSurface.shader` (hoisted-gradient edits), `WaterSurfRoller.compute`
or `SurfRollerParticles.shader`; C# errors will be in `WaterSurfRollerParticles.cs` or the two
editor partials. Everything is additive/gated; the roller system is three self-contained files —
delete them and the package is back to BEAT-1.
