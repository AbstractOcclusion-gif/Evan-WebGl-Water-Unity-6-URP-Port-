# Changelog

All notable changes to this package are documented here.

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
