# Large-water camera-following sim window — implementation plan

Status: planned (build next session). Decisions locked: **world-anchored scrolling window**, **auto-enable above a size threshold**, small pools unchanged.

## Goal

Run the interactive ripple simulation in a bounded window that follows the camera, so a
large water body has full-detail ripples where the camera is and cheap analytic wind waves
everywhere else. The window scrolls with the camera so ripples stay pinned in world space.
Bodies below a size threshold keep today's whole-body sim exactly (zero regression).

## Why big water is coarse today

- The interactive sim is a fixed `_simRes` grid stretched over `[-1,1] × extent`; large
  `extent` means each texel covers a big world patch, so click/object ripples look blocky.
- Wind waves are analytic (metres, `WaterWaveBank`) and scale-independent — fine at any size,
  **provided** `poolHalfExtentMeters` is set to the body's real half-size (else they stretch).
- Caustics and god rays span the whole pool box; for large open water they are normally off
  (no floor), so they are out of scope for the windowed path.

## Core idea: separate the "sim frame" from the "visual frame"

- **Visual frame** = the existing volume frame (`VolumeCenter`, `VolumeRotation`,
  `VolumeExtentSafe`). Still drives the big surface mesh, wind waves and placement. Unchanged.
- **Sim frame** (new, windowed bodies only): centre = camera projected onto the water plane,
  snapped to sim-texel increments; rotation = body rotation; horizontal half-size =
  `simWindowExtent` (world); vertical = `extent.y` (ripple height scale unchanged).
- `_WaterTex` now represents the sim **window**, not the whole body.

## New uniforms (global + per-body MPB, mirroring the existing frame publish)

- `_SimWindowed` (0/1) — branch flag; 0 restores today's behaviour.
- `_SimCenter` (world), `_SimExtent` (world half-size xz, y), rotation reuses `_VolumeRot`.
- Shared HLSL helper `WorldToSimUV(worldPos)` in a new/extended include (mirrors
  `WaterVolume.hlsl`'s `WorldToPool`).
- `_SimEdgeFadeTexels` — border falloff width.

## Shader changes (`WaterSurface.shader`, shared helpers)

- **Vertex**: today it samples `_WaterTex` at the visual pool UV. For windowed bodies compute
  `worldPos` (already available) → `simUV = WorldToSimUV(worldPos)`. If `simUV ∈ [0,1]`, sample
  ripple height/normal with an edge falloff; else ripple = 0. Analytic `WaveHeight` is added on
  top as today. Non-windowed path is unchanged (branch on `_SimWindowed`).
- **Fragment**: same `simUV` mapping for the ripple normal; blend the ripple-normal to flat over
  the last `_SimEdgeFadeTexels` so there is no seam where the window meets analytic-only water.
- **Caustics / god rays / receiver caustics**: out of scope for windowed bodies — guard so they
  do not sample the window. Documented as a known limitation for large open water.

## C# changes (`WaterVolume`)

- New fields: `largeBodyThreshold` (world size that flips to windowed), `simWindowMeters`
  (window half-size), derived `bool _windowed = max(extentWorld.x, extentWorld.z) > threshold`.
- Track `_simCenter` per frame: project `targetCamera` position onto the surface plane
  (`VolumeUp`, `VolumeCenter.y`), clamp into the body footprint, snap to the sim-texel grid
  (`texel = 2 * simWindowExtent / _simRes`).
- **Scroll**: integer texel delta from the previous centre; if non-zero, shift `_a` and `_foamA`
  by `(dx, dz)` texels and clear the newly exposed rows/cols to rest state, then Step as usual.
- Injection mapping: `AddRipple` / obstacle currently map world → visual pool; for windowed
  bodies map world → **sim-window** UV instead. Injections outside the window are ignored.
- Publish the sim-frame uniforms (global + MPB) next to the volume-frame publish.
- Buoyancy (`TrySampleSubmersion`, `TryGetWaterHeight`): ripple height from `_heightCpu` via
  world → sim UV inside the window; analytic wave via body pool xz (unchanged); ripple 0 outside.

## `WaterSim.compute` change

- Add a `Scroll` kernel: `Dst[id] = inBounds(id - offset) ? Src[id - offset] : rest` where rest =
  height 0, velocity 0, normal up (and foam 0 for the foam buffer). Offset in texels via a
  uniform; ping-pong like the other kernels. Expose `WaterSimulation.Scroll(dxTexels, dzTexels)`.

## Per-frame windowed loop

1. `desired = project(cameraPos onto plane)`, clamp to footprint.
2. `texel = 2 * simWindowExtent / _simRes`.
3. `snapped = round(desired / texel) * texel`.
4. `deltaTexels = round((snapped - _simCenter) / texel)`.
5. if `deltaTexels != 0`: `Scroll(_a, delta)`, `Scroll(_foamA, delta)`, `_simCenter = snapped`.
6. Step sim / normals / foam / conserve (as today).
7. Publish `_SimCenter`, `_SimExtent`, `_SimWindowed`.

## Edge cases & fallback

- Below threshold → `_SimWindowed = 0`, identical to today (regression-safe).
- Clamp the window so it stays within the footprint (or allow partial, analytic-only beyond).
- Rotation/tilt: project along `VolumeUp`; snap in sim-frame axes.
- Volume conservation runs within the window only (acceptable).
- Keep the manual bilinear (`_WaterTexel`) for the window (WebGPU float-filter fix still applies).

## Test plan

- Small-pool demo unchanged (regression check).
- Large Open Water demo with windowing on: fine ripples near camera, analytic waves everywhere,
  no seam at the window edge, ripples stay world-anchored while orbiting/moving.
- Buoyancy: a prop near the camera bobs on ripples; a far prop rides analytic waves only.

## Open questions

- Default `simWindowMeters` and `largeBodyThreshold`?
- Is snap-to-texel jitter acceptable, or add sub-texel phase correction?
- Optional editor gizmo drawing the active window box.
- Long-term: a floor-relative caustics/god-ray scheme for large bodies (separate effort).

---

## Next session (separate from the sim window)

- Clean `Generated/` and temp assets; consolidate demo materials under `Generated/Demos/`.
- Create a dedicated demo-scenes folder and save each demo as its own `.unity` scene.
- Encapsulate the project as a distributable UPM package (`package.json`, asmdefs, `Samples~`)
  with the abstraction/occlusion layer ("abstractocclusion" — confirm the intended name/scope).
