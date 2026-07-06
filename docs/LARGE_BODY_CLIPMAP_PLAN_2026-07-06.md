# Large-Body Water — Step 2: Clipmap Ocean Extent (PLAN, awaiting OK)

Branch: `feature/large-body-water` · Date: 2026-07-06 · Status: **PROPOSED — no code yet**

## Goal

Make an open-water body's surface reach the **horizon** (an ocean), camera-following, without
disturbing the near-field look (ripples, chop, caustics, refraction) and with pools / bounded
lakes byte-for-byte unchanged.

## Reference basis (checked, not guessed)

- **Crest** ocean geometry (`Runtime/Scripts/Surface/WaterBuilder.cs`, `Runtime/Shaders/Surface/Geometry.hlsl`):
  - **Texel snapping** (so the mesh doesn't swim): `gridOffset = frac(objectPosXZ / (2·gridSize)) · (2·gridSize); worldPos.xz -= gridOffset`.
  - **LOD-seam morphing** between concentric rings (`SnapAndTransitionVertLayout`, `lodAlpha`).
  - **Horizon skirt**: outermost ring vertices multiplied by `_ExtentsSizeMultiplier` (default 100)
    to reach the far plane.
- **Our existing files** (`Runtime/LargeWaterClipmap.cs`, `LargeWaterClipmapDriver.cs`, behind the
  `WEBGPUWATER_LARGE_BODY` define): a **radial** ring mesh — centre + geometric-radius rings — that is
  ONE continuous mesh, so it has **no LOD-ring seams** and needs **no morphing** (simpler than Crest;
  the trade is slightly less even texel density, fine for this pass). The driver texel-snaps a
  transform to the camera. This is what we wire up.

## Current architecture (from source)

- Surface = authored `waterMesh` grid `[-1,1]` (XY plane, z=0) on `surfaceAbove`/`surfaceUnder`
  renderers, transform at `VolumeCenter`, scaled by `VolumeExtent` → covers exactly the footprint.
- Windowed bodies also spawn a runtime **"Sim Window Patch"** (`CreateSimWindowPatch`), a dense grid
  that follows the camera (`WaterSimWindow.Track`, texel-scrolled) and is remapped in-shader via
  `_IsPatch`/`_PatchPoolCenter`/`_PatchPoolHalf`. Ripples fade to flat at the window border
  (`_SimEdgeFadeTexels`, `WorldToSim`).
- `openWater` today only sets `_LargeBody`; the surface stays the bounded plane.
- Transforms exist in both HLSL and C#: `PoolToWorld`/`WorldToPool`/`WorldToSim`/`PoolNormalToWorld`.
- Renderer wiring: `ApplyBodyBlock()` pushes the MPB to each renderer + `ApplyPatchBlock()`;
  `SetRenderersEnabled()` toggles them; `Update()` runs per frame.

## Design — the clipmap IS the open-water surface for "ocean" bodies

Add a new per-body flag **`unboundedOcean`** (default **false**). It only does anything when
`openWater` is also true. When on, the body renders its surface with the **camera-following radial
clipmap** instead of the bounded plane; when off (every existing body), nothing changes.

### 1. New gated shader vertex path — `_IsClipmap`
`WaterSurface.shader` gets a third branch alongside full-plane and `_IsPatch`:
- Clipmap mesh verts are authored in **world metres in the XZ plane** (`x,0,z`), object placed at the
  camera (snapped). So `worldPos = mul(unity_ObjectToWorld, v.vertex)`, then `worldPos.y = surfaceY`.
- Everything else **reuses the existing large-body path**: `LargeBodyWaveHeight` + `LargeBodyWaveDisplacement`
  for swell/chop; ripples via the SAME `SampleRipple` + `WorldToSim` fade (so the near field inside the
  sim window shows interactive ripples and fades to pure swell farther out); fragment normal /
  deep-water refraction / reflection all unchanged.
- Pool/patch paths are untouched — `_IsClipmap` defaults 0.

### 2. Camera follow + texel snap (reuse, don't reinvent)
Reuse `WaterSimWindow`'s camera-onto-plane projection for the follow position, and snap it to the
clipmap's innermost quad size (Crest's `frac`-snap, which our driver already implements). Manage this
in `WaterVolume` next to `ApplyPatchBlock` (one place owns camera-follow) — so the standalone
`LargeWaterClipmapDriver` MonoBehaviour is **retired** to avoid two parallel follow paths (no dead code).

### 3. Horizon reach
`LargeWaterClipmap.BuildRadialGrid(rings, segments, innerRadius, outerRadius)` already grows radii
geometrically; set `outerRadius` near the camera far plane (Crest's skirt idea). Inner rings dense for
chop/ripples, outer rings large for the long swell the eye sees at distance.

### 4. Don't double-draw
For an `unboundedOcean` body, the bounded `[-1,1]` plane is **disabled** (it would z-fight / double the
surface). Increment 1 does this for `surfaceAbove` only; `surfaceUnder` (underwater view) stays bounded
until Increment 3 (underwater horizon matters far less).

### 5. Graduation from the scripting define
To let you toggle it from the inspector with no define fiddling (matching how `openWater`/`choppiness`
ship), **graduate `LargeWaterClipmap.BuildRadialGrid` to always-compiled** (pure geometry, no side
effects) and gate at runtime by `openWater && unboundedOcean`. Same safety as the define: default-off
bool ⇒ existing build byte-for-byte. (Decision point below.)

## Buoyancy — unaffected

Height stays a pure function of world XZ (`LargeWaveField`), so submersion/float/ride are unchanged.
The clipmap only extends where the surface is DRAWN, not the wave field. No readback, no new sampling.

## Constants / naming (no magic numbers)

New: `unboundedOcean` (bool), `_IsClipmap` (uniform string), clipmap `rings`/`segments`/`innerRadius`/
`outerRadius` as serialized `[Min]`-guarded fields with named defaults, snap step = innermost quad size
(derived, not authored). Reuse `LBW_*`, `_LargeBody`, the sim-window uniforms.

## Gating guarantees

- Pools / small / bounded-lake bodies: `unboundedOcean = false` ⇒ zero code-path change.
- Open-water bounded bodies (today's verified look): default `unboundedOcean = false` ⇒ unchanged.
- Only `openWater && unboundedOcean` takes the clipmap path.

## Risks / watch-list

- **Lake vs ocean**: clipmap draws water to the horizon — correct for ocean, wrong for a bounded lake
  with shores. Hence the opt-in flag; lakes keep the plane.
- **Underwater surface** (`surfaceUnder`) extent deferred to Increment 3; until then the underwater
  horizon is the old bounded plane.
- **Refraction at distance**: the deep-water fallback already covers no-geometry rays; verify the
  `_REAL_REFRACTION` scene-sample path degrades gracefully out to the horizon.
- **Reflection / sky seam** at the horizon line (planar reflection vs skybox) — check the join.
- **Perf**: ring×segment counts + per-vertex swell over a horizon mesh; keep counts modest, verify in
  the WebGPU build (per [[webgpu-build-urp-asset]]).
- **Snap vs chop**: snapping is to the innermost quad; confirm fine chop doesn't visibly swim.

## Proposed increments (small, with you at the editor)

1. **Shader path + wire the mesh, no follow.** Add `_IsClipmap` vertex path; graduate `BuildRadialGrid`;
   create a clipmap renderer for `openWater && unboundedOcean`, positioned at `VolumeCenter` (static);
   disable the bounded `surfaceAbove` plane for those bodies. Confirm: default bodies unchanged; an
   ocean body shows swell out to `outerRadius`, ripples still near-field.
2. **Camera follow + texel snap.** Drive the clipmap transform from the camera projection + snap; retire
   the standalone driver. Confirm no swimming as the camera moves; horizon holds.
3. **Underwater + polish.** Extend `surfaceUnder`, check reflection/horizon seam, tune ring counts and
   `outerRadius`, verify in the WebGPU build.
4. Commit per increment (you run git; commands one per line).

## Decisions I need from you

- **A. Extent flag default & scope** — add `unboundedOcean` (default off), lakes keep the bounded plane?
  (Recommended.) Or make open-water always unbounded?
- **B. Define vs runtime** — graduate the clipmap to always-compiled + runtime bool (inspector-friendly,
  recommended), or keep it behind `WEBGPUWATER_LARGE_BODY`?
