# Night Report — Coastline P0–P5 implemented (2026-07-15)

**No git operations were made** (per your instruction). Everything below is plain file edits on top
of your working tree — `git diff` shows the whole night, `git checkout -- <file>` reverts any part.
I could not compile (no Unity here): code was written blind against your existing patterns, then
adversarially reviewed by a second pass which found 5 real bugs — all fixed. Expect the *possibility*
of a trivial compile fix on first open; the architecture is sound.

## Files changed (13) + 1 new

| File | What |
|---|---|
| `Runtime/Shaders/WaterShore.hlsl` | REWRITTEN — ShoreData one-stop fetch, border feather (B5), smooth shoal band (B3), Green's-law gain, phase-compression warp, per-body gate |
| `Runtime/Shaders/WaterSurfWaves.hlsl` | **NEW** — the surf breaker-front layer: SDF-phase fronts, depth-driven break lifecycle, whitewash, sets, swash + wet line. Pure math, compiles in graphics AND compute |
| `Runtime/Shaders/WaterLargeWaves.hlsl` | Shoal transform in the band accumulator (refraction, compression, per-λ attenuation), FFT per-cascade shoal (B1), ambient fade + surf composition in every public function |
| `Runtime/Shaders/WaterSurface.shader` | Fragment: whitewash foam layer (3rd exclusive layer), breaker SSS glow, swash-breathing clip line + analytic wet-sand glaze; vertex: swash film lift onto the sand; debug branch updated to new depth semantics |
| `Runtime/Shaders/WaterSim.compute` | Foam kernel: surf whitewash/breaker/waterline-lace injection (hero-wave pattern) |
| `Runtime/Shaders/WaterShoreSwe.compute` | Depth-semantics update + ShoalWeight kept in lockstep (SWE itself untouched — it stays C1 debug-only) |
| `Runtime/WaterShoreDepthField.cs` | REWRITTEN — stores COLUMN DEPTH not absolute Y (B4 banding fix), SDF direction smoothing (B11), CPU arrays kept + `TrySampleShore` for buoyancy, publishes all `_Shore*`/`_Surf*` knobs, hard off-gate on `useBedDepth` |
| `Runtime/LargeWaveField.cs` | REWRITTEN — CPU mirror of the FULL shore transform + surf fronts (byte-for-byte, reviewed term-by-term), plus FFT-readback shore treatment |
| `Runtime/WaterSimulation.cs` | `ShoreFoamState` + binder on the Foam kernel |
| `Runtime/WaterVolume.cs` | New settings block (see knobs), accessors, `ShoreWaveCtx`, `PushShoreFoam`, FFT readback shore fix |
| `Runtime/WaterVolume.Query.cs` | Buoyancy vertical velocity gets the shore context |
| `Runtime/WaterUniformPublisher.cs` | Per-body `_ShoreBodyGate` so a pond overlapping the terrain field can never catch the lake's surf |
| `Editor/WaterVolumeEditor.Appearance.cs` | Bed Depth section: Shoal transform + Surf breaker fronts + SWE subsections |

## What you get, per phase

- **P0 repairs**: FFT ocean now shoals (per-cascade, it never did at all); the 4 m shoal wall is a
  smooth band; depth field no longer bands on gentle slopes; the terrain-rectangle seam is feathered
  away; SDF direction is smoothed and can steer things.
- **P1 shoal transform**: waves refract toward the beach (crests swing shore-parallel), compress
  (bunch) as they slow, GROW per Green's law before dying — the "waves die at the beach" look is gone.
- **P2 surf fronts (the money layer)**: sets of breaker fronts roll in parallel to ANY shoreline
  shape, steepen and lean, break exactly where depth says (H > 0.78·d — sandbars give outer break
  lines for free), collapse into whitewash bores that run shoreward. Fully art-directable, zero sim.
- **P3 foam**: whitewash renders analytically at any distance (whitecap-language texture pipeline)
  AND is injected into the interactive foam sim near-field so it advects/decays into organic trails;
  a standing waterline lace hugs the shore.
- **P4 swash + wet sand**: the waterline breathes with each arriving front (quick uprush, slow
  backwash); the surface hugs the sand as a thin film there, and the recently-covered band renders
  as a dark wet-sand glaze that dries until the next front — fully analytic, no sim, no extra RT.
- **P5-lite buoyancy**: the CPU mirror carries the whole transform + fronts (and the FFT readback
  path is corrected too), so floaters ride shoaled swell and breaker fronts and the waterline
  computations stay in agreement. The full Master-Base-Clean bake refactor is deferred to a live
  session (you approved it; I didn't want to delete your buoyancy mirrors blind).

## How to test (5 minutes)

1. Open **`3. EXPERIMENTALTerrain Lake`**. On the WaterVolume: **Bed Depth ON**, a Bed Terrain
   assigned. That's it — surf defaults ON once the field bakes.
2. You should see: swell aligning to the beach → fronts rolling in, breaking, whitewash sliding
   shoreward → waterline breathing + wet sand darkening → foam lace at the waterline.
3. Knobs live in **Bed Depth → Surf breaker fronts**: start with `Surf Amplitude` (0.8), `Surf
   Wavelength` (26), `Surf Period` (9), `Surf Band Depth` (6 — match it to your beach slope),
   `Shore Shoal Depth` (raise it to ~half your swell wavelength for the P1 transform to breathe).
4. Ocean scene: same toggle; FFT keeps deep-water texture, fronts own the coast.
5. Debug views unchanged: context menu → Toggle Shore Depth/SDF Debug.

## Known limitations / honest notes

- **One shore body per scene** (pre-existing Layer A constraint): the `_Shore*` globals are
  last-writer-wins. The new `_ShoreBodyGate` stops OTHER bodies from catching the field, but two
  terrain lakes would still fight. Fine for now.
- The **wet-sand glaze lives on the water film**, not on the terrain material — real terrain
  darkening needs a hook in your terrain shader (decal or splat lerp); the analytic
  `wetLevel` value is ready to feed it whenever you want that.
- Breaker **SSS glow** rides the existing crest-glow gate (`_SssEnabled`), which your editor enables
  for oceans — on a lake, enable Volume Scattering + crest glow to see lips light up.
- The **surf slope finite-difference** can produce a subtle normal seam line parallel to shore at
  front-cell boundaries when Set Strength is high (reviewer flag). If you see sparkle lines, lower
  `Surf Set Strength`; a proper fix (per-front-consistent FD) is a small follow-up.
- **SWE (your C1) is untouched** apart from semantics upkeep — it's now the optional P6
  interaction layer per the master plan, no longer responsible for the coastline. Its structural
  fixes (dynamic wet/dry etc.) are specced in the plan §5 P6 whenever you want them.
- Whitewash texture reuses `FoamFlipbook_4x4` via the pond pipeline — if it reads too lacy, a
  dedicated whitewash texture is a drop-in later.
- `docs/foam_audit_current.txt` / `swe_audit_current.txt` remain pre-cleanup and inaccurate.

## If something is broken on open

Shader errors will name `WaterSurfWaves.hlsl`, `WaterShore.hlsl` or the `WaterSurface.shader`
edits; C# errors will be in the new accessors or `ShoreWaveContext` call sites. Everything is
additive and gated — worst case `git checkout -- <file>` per file, or revert all 13. Nothing
touches scenes, prefabs, or serialized data (the new settings fields default sanely into the
existing `bedDepthSettings` block — no migration needed).
