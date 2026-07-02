# WebGpuWater — release punch list (tweak / clean / debug)

The system is **feature-complete** for contained water: per-body `WaterVolume` with
`MaterialPropertyBlock` de-globalization, gameplay API (`AddRipple`, `TryGetWaterHeight`,
`TrySampleSubmersion`, `TrySampleHeight`, `IsSubmerged` + static helpers), `WaterProbe`
submerge/emerge events, frustum + activation-distance culling with an active-sim budget, and
`WaterQuality` tiers. It's now a UPM package with a sample and MIT licence.

So what's left is **not new features** — it's tweak, clean, and debug. Verified against the packaged
source on 2026-07-02.

## Debug (real risks to fix)

- **Buoyancy sinks on WebGPU/mobile.** `AsyncGPUReadback` of the height texture isn't guaranteed on
  those backends, so objects sink instead of float. Add a CPU analytic-waterline fallback (wind-wave
  bank + rest height) when readback is unavailable.
- **Mobile WebGPU crash + capability gate.** The deployed WebGPU build has stack-overflowed on some
  phones. Probe `navigator.gpu` / capability and pick a safe `WaterQuality` tier (or disable the compute
  sim) instead of crashing.
- **Verify the newer features on the WebGPU build.** GPU foam particles and the wind-wave layer ship but
  aren't confirmed on the WebGPU/mobile build (a particle build-rendering issue is open) — confirm or
  gate per tier.
- **Intermittent editor freeze** (suspected experimental WebGPU device, not a code loop) — repro/track.

## Clean

- **#14** `public` serialized fields → `[SerializeField] private` on `WaterVolume` / `OrbitCamera` now
  that assembly boundaries exist (only expose what the editor genuinely writes).
- **#11** per-frame body-resolution cache — optional; revisit only if object counts grow.
- Confirm a **clean compile after reimport**, both with and without URP installed.

## Tweak (nice polish, not blocking)

- **Scene-view gizmo/handles** for the volume (no `OnDrawGizmos` today) — oriented box + TRS handles so
  designers size/rotate visually.
- **Ship a first-class `WaterVolume` prefab** (currently only inside the demo sample) and tidy the
  `WaterVolumeEditor` inspector grouping.
- **Gameplay API examples/docs** — the primitives + probe events exist; a short usage snippet (footstep
  ripples, boat wake via `WaterRippleEmitter`) is all that's missing.

## Suggested order

1. The two WebGPU/mobile debug items (buoyancy fallback + capability gate) — these are what make the
   cross-platform story true.
2. Verify foam particles / wind waves on the WebGPU build.
3. Clean pass (#14), then the gizmo/prefab tweaks.
