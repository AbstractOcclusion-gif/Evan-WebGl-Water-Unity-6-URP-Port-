# Shoreline Layer C — SWE shore zone (approach + C1 spec)

Date: 2026-07-13. Companion to `docs/SHORELINE_PLAN_2026-07-13.md` (§ Layer C) and the
Layer A approach `docs/SHORELINE_LAYER_A_APPROACH_2026-07-13.md`. Status: **C1 code chunk
written for Bert's Unity recompile + eyeball. Render-only, gated, revertible.**

Layers A + B are shipped and confirmed: a world-frame seabed depth field + jump-flood
shoreline SDF (`WaterShoreDepthField`), and depth shoaling of the analytic swell
(`WaterShore.hlsl`). Layer C adds a local shallow-water (Saint-Venant) sim near the
waterline that produces **emergent breaking + run-up**, consuming Layer A's depth + SDF.

## Decisions locked (Bert, 2026-07-13)

- **Start with C1** — solver + zone + debug viz, no surface coupling yet.
- **Distributed shoaled-swell pump** (not an offshore-edge wavemaker): the incoming swell
  is pushed shoreward across the whole near-shore band, gated by the SDF mask and shoaled
  by depth. C1 drives the pump from the **primary swell** (amplitude / wavelength / heading
  / shared wave clock) evaluated analytically and shoaled with the same `ShoalWeight` Layer
  B already uses — a compute-safe surrogate for the full analytic field. Layering the full
  multi-component wind-chop spectrum into the pump is a later refinement (needs a
  compute-safe entry into `WaterLargeWaves.hlsl`, which today uses `tex2Dlod` and is
  `#include`d only by `.shader`).
- **Render-only first** — no buoyancy reach. The SWE height is a rendering + downstream-foam
  signal; floaters do not physically feel run-up. Physical coupling is a later opt-in
  extension (and would need the height reachable without GPU readback).
- **Zone frame = dedicated camera-following zone** (Claude's call, Bert deferred). Its own
  extent + resolution, texel-snapped and integer-scrolled reusing the proven
  `WaterSimWindow` idiom (the ripple sim already snaps + scrolls this way, so the
  scroll-seam risk is solved machinery, not new). Rejected: world-fixed-over-terrain (blows
  up memory/resolution on a 1000 m terrain) and piggybacking the ripple window (tangles SWE
  tuning into ripple tuning).

## Gate (identical to Layer B)

Runs only when `useBedDepth` is on **and** a `bedTerrain` is present (so Layer A's field
exists) **and** a `sweCompute` is assigned. Any other body skips it entirely and is
byte-for-byte unchanged — same discipline as `OceanFftModule` (ocean-only) and Layer B.

## C1 architecture

New files, mirroring the Layer A / OceanFft patterns:

- `Runtime/Shaders/WaterShoreSwe.compute` — the solver. WebGPU-safe: state is read through a
  sampled `Texture2D Src` (`.Load`) and written to a write-only `RWTexture2D Dst` (only
  *read-write* storage is r32-limited on WebGPU; a write-only float4 target is fine, exactly
  as `WaterSim.compute` does). Depth/SDF are half-float → hardware-filterable, sampled with a
  linear sampler.
- `Runtime/WaterShoreSwe.cs` — the driver: owns the ping-pong RTs + the camera-following
  zone frame (texel snap + integer scroll), dispatches the kernels, publishes the debug
  globals.
- `ShoreSweModule` in `WaterCollaboratorModules.cs` — the `IWaterModule` seam, gated as above.

### State & formats

- `_stateA/_stateB` — `ARGBFloat` ping-pong, channels `(waterHeight h, velX, velY, foam)`.
  Height is metres of water column above the seabed; velocity is depth-averaged flow (m/s);
  foam is a scratch accumulator reserved for Layer D (unused by rendering in C1).
- Published to the surface as `_ShoreSweTex` (the state RT) + frame uniforms; C1 only
  *point*-samples it in the debug branch. C2 adds an `RHalf` height export for filtered
  surface sampling.

### Solver (per substep, two Jacobi passes — the staggered explicit scheme)

Explicit shallow water needs velocity updated from the old height field, then height updated
from the *new* velocity divergence — so it is two kernels with a ping-pong between (KWS2 and
Crest both do this):

1. **`StepVelocity`** — pressure-gradient acceleration of the total surface
   `η = h + seabed`: `v += dt · (−g · ∇η)`; Manning-style friction
   `v /= (1 + g·n²·|v|/h^(4/3)·dt)`; hard CFL clamp `|v| ≤ 0.5·texel/dt`. Dry cells
   (`depth ≤ 0`) and cells draining from a dry neighbour zero the crossing velocity
   (run-up / wall handling). Height copied through unchanged.
2. **`StepHeight`** — continuity `h += dt · −∇·(h·v)` (upwind flux) + the distributed swell
   pump (relax `h` toward the shoaled swell target, add a shoreward velocity push along
   `shoreDir` from the SDF, both gated by the near-shore mask) + `OceanRelax` bleeding excess
   height back to sea level offshore. Depth-limiter and `isfinite` + magnitude clamps
   (mirroring `WaterSim.compute`'s `Sanitize`) keep the explicit integrator bounded.

Auxiliary kernels: `Clear` (init/resize/re-enable), `Scroll` (integer-texel shift of the
whole state for camera-follow, exactly like `WaterSim.Scroll`).

Overshoot-reduction as a dedicated pass (Crest's `HeightOvershootReduction`, λ = 2·texel) is
**deferred to C2** — C1 relies on the CFL velocity clamp + height clamp + depth-limiter for
stability, which is enough to get a stable, visibly breaking field to eyeball first.

### Camera-following zone

Driver projects the camera onto the water plane, snaps the zone centre to the SWE texel
lattice, and `Scroll`s the state by the integer texel delta so features stay world-anchored
(the `WaterSimWindow` idiom). The zone is world-axis-aligned (like Layer A's field), so a
cell's world XZ is `center + (uv − 0.5)·2·halfSize` — no rotation math. Each cell samples the
Layer A depth + SDF by its own world XZ (the two frames are independent; the depth field is
the whole static terrain, the SWE zone is a small moving window inside it).

### dt / cadence

Fixed-dt accumulator (Crest model): accumulate frame time, run up to `N` substeps of a small
fixed `dt` to stay CFL-stable regardless of frame rate. Not gated on volume-conserve (the
open-shore drain + relax handle the boundary, like the Layer-B shoreline sim path).

### WebGPU

All compute + textures, readback-free. `Src` sampled / `Dst` write-only for the float4
state; half-float depth/SDF sampled linearly; velocity read point (`.Load`). A texture is
always bound (black fallback) so the backend never sees an unbound sampler — same rule the
rest of the package follows.

## C1 wiring points (grounded against the live tree)

- Serialized field `sweCompute` beside `simCompute`/`oceanFftCompute` (`WaterVolume.cs:54-58`);
  NOT part of `HasRequiredWiring` (bodies run without it).
- `ShoreSweModule` in `WaterCollaboratorModules.cs`, registered with the other modules
  (`WaterVolume.cs:1300-1303`).
- Dispatch in `Step`, beside the ripple `StepSimulation` loop (`WaterVolume.cs:2753`); publish
  beside `ShoreDepth.EnsureBakedAndPublish` (`:2044`).
- Debug toggle as a `[ContextMenu]` mirroring the Layer A toggles (`WaterVolume.cs:3214-3229`).
- Debug uniforms in `WaterShore.hlsl`; debug colour branch in `WaterSurface.shader` beside the
  Layer A depth/SDF debug (`:1135-1170`).
- A few knobs on `BedDepthSettings` (`WaterVolume.cs:1032-1054`) + one editor line
  (`WaterVolumeEditor.Appearance.cs:84-90`).

## Verification (C1 gate)

1. Assign `sweCompute`, enable `Use Bed Depth` + a `Bed Terrain` on a coast/lake body; the
   Layer A field must be baked (SDF debug already confirms this).
2. Context-menu **Toggle Shore SWE Debug** → the zone tints by water height/velocity near the
   waterline; waves visibly build and break as they run into the shallows.
3. World-locked: the pattern does not swim when the camera moves (texel snap + scroll).
4. Stable over a long run: no NaN blow-up, no plane pop (the clamps + drain hold).
5. Non-terrain / no-`sweCompute` bodies unchanged; deep water unaffected.

## Next (not in C1)

- **C2** — add the shore-band height to the surface (`WaterSurface.shader:572`), faded into the
  open-water swell; RHalf height export; dedicated overshoot-reduction pass if the eyeball
  shows edge artefacts.
- **C3** — export the breaking/shore foam source for Layer D.
