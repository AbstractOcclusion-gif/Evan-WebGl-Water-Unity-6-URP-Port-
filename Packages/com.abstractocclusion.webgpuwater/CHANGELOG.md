# Changelog

All notable changes to this package are documented here.

## [1.0.0] - 2026-07-03

First Asset Store release.

### Added
- Interactive ripple simulation (compute-based heightfield) with frame-rate-independent
  stepping: identical wave speed at 30 fps on a tablet and 144 fps in the editor.
- Two-way object coupling: multi-point buoyancy with righting torque and wave drift
  (`WaterBuoyancy`), analytic drop / footprint-delta disturbance (`WaterInteractable`),
  entry splashes with drifting droplets and flipbook crown (`WaterSplash` + emitter).
- Ambient wind-wave layer (sum of sines) that floating objects ride, with wind speed,
  heading, and spread controls.
- Turbulence-driven surface foam (generation/decay/advection) plus fully GPU-resident
  foam/spray particles (compute spawn + procedural quads, no readback, WebGPU-safe).
- Caustics, hybrid god rays with real shadow shafts, and per-body reflections:
  SSR, planar, or sky, over a procedural sky or the scene's URP probe.
- Water fog (Beer-Lambert, HDR extinction), opacity dial, per-channel depth darkening,
  and terrain bed depth with a shoreline gradient.
- Multi-instance water bodies (per-body MaterialPropertyBlocks) with visibility/distance
  culling and a simulation budget; camera-following sim window for large bodies.
- Quality tiers (`WaterQuality` asset): High/Medium/Low with auto hardware probe —
  sim/caustic resolution, god-ray steps, wave count, render scale, refraction,
  mesh detail, update intervals, and foam-particle caps per tier.
- One-window authoring: **Window > AbstractOcclusion > WebGpuWater > Water Wizard**, plus
  8 ready-made demo scenes in `Samples~/Demos`.
- Scripting API: `WaterVolume` gameplay facade (`TryGetWaterHeight`, `TryGetSurface`,
  `TrySampleSubmersion`, `AddRipple`, `TryRaycastSurface`, `IsSubmerged`) and public
  properties for runtime look/behavior (fog, foam, wind waves, ripple strength/radius,
  reflections, quality, culling).

### Changed
- Public API surface minimized for release: inspector tuning and builder wiring are no
  longer public fields; runtime-scriptable settings are exposed as properties. All
  serialized names unchanged — existing scenes and prefabs upgrade untouched.
- All shaders now declare `"RenderPipeline" = "UniversalPipeline"` (SRP-compatibility,
  Unity 6.6 BIRP deprecation).
- Fast Enter Play Mode (Unity 6.6 default) fully supported: all scene-lifetime static
  state resets via `SubsystemRegistration` before each play session.

### Verified
- Mobile/WebGPU: 30 fps on Honor X6 and Redmi Pad SE, 30+ fps on Samsung Galaxy A17
  with foam, caustics, and god rays enabled (Low tier). Unsupported browsers/GPUs get
  a clear error message instead of a crash.

## [0.1.0] - 2026-07-02
### Added
- Initial extraction of the WebGpuWater system into a standalone UPM package
  (`com.abstractocclusion.webgpuwater`), split out of the host project's `Assets/WebGLWater`.
- Runtime and Editor assembly definitions (`AbstractOcclusion.WebGpuWater`,
  `AbstractOcclusion.WebGpuWater.Editor`).
- URP-specific planar reflection isolated behind the `WEBGPUWATER_URP` define so the base
  assembly compiles even when the Universal Render Pipeline is not installed.
- Namespaces rebranded `WebGLWater.*` -> `AbstractOcclusion.WebGpuWater.*`.
- Single authoring entry point: **AbstractOcclusion > WebGpuWater > Water Wizard**.

### Notes
- Compute shaders are loaded from the package via `PackageShadersRoot`; generated meshes,
  materials, textures and the sample prefab are still written into the consuming project's
  `Assets/` (the package stays read-only).
- URP 12+ is recommended for full visual fidelity (planar reflections, screen-space refraction).
