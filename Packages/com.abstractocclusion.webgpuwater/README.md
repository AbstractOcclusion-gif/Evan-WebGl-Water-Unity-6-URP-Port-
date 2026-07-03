# AbstractOcclusion.WebGpuWater

GPU water for Unity URP: interactive ripple simulation, two-way buoyancy, surface +
edge foam, GPU foam particles, caustics, god rays, and hybrid planar/SSR/sky
reflections. Everything is authored from one window — the **Water Wizard**. A modern
URP port and expansion of Evan Wallace's
[WebGL Water](https://madebyevan.com/webgl-water/) (MIT).

**Version 1.0.0** | Unity 2022.2+ | URP 12+ | Desktop · WebGPU/WebGL · Mobile

## Scope

Built for **small and mid-size** water bodies — pools, ponds, small-to-mid lakes. Past roughly
**~20 m** of extent the interactive ripple grid gets coarse and the analytic wind waves stop
looking realistic at that scale. **Large lakes and oceans are out of scope for this version** and
are planned as a separate, dedicated system (spectral/FFT waves with their own wave foam, fog and
Unity-terrain handling). Very large, fully opaque water also needs a different shading model than
the transparent pool path.

**Unity Terrain support is experimental** — the bed-depth bake approximates a shoreline gradient
from a Terrain heightmap; full terrain integration is not there yet. Treat it as a preview.

## Requirements

- **Unity 2022.2 or newer** (Unity 6 fully supported).
- **URP 12+** for rendering. The base runtime assembly compiles without URP installed;
  URP-only code activates automatically via the `WEBGPUWATER_URP` define.
- On your **active URP asset**, enable **Depth Texture**, **Opaque Texture** (SSR and
  refraction), and **Transparent Receive Shadows** (god-ray shafts).

## Install

Add the package via **Package Manager > Install from disk/tarball** (or your registry),
then open **Package Manager > AbstractOcclusion.WebGpuWater > Samples** and import
**Demo Scenes** to try it immediately.

## Quick start

**Window > AbstractOcclusion > WebGpuWater > Water Wizard** builds a complete water
body — sim volume, surface renderers, splash emitter, quality asset, and a tweakable
material saved into your project. Configure size and features, press **Create Water
Surface**, then **Play**. Drag on the surface for ripples; drop a Rigidbody with
`WaterBuoyancy` in and it floats, rocks, and rides the wind waves.

## Quality tiers & mobile preview

The **WaterQuality** asset ships **High / Medium / Low** cost tiers (auto hardware
probe) that scale sim and caustic resolution, render scale, god-ray steps, wave count,
refraction, mesh detail, update intervals, and foam-particle caps.

Because those resolutions and scales differ per tier, **the High and Low tiers usually
need different visual-tuning values to look correct** — a look dialed in at High
(ripple radius/strength, foam thresholds/feather, wave amplitude) can read too strong,
too weak, or too coarse at Low. Tune per tier.

**To preview what will actually render on mobile, set the Quality asset to Force Low.**
Mobile runs the Low tier, so forcing Low in the editor is the only way to see the
resolution, render scale, and particle caps your device build will use.

## Documentation

Full docs — Getting Started, core components, scripting API, WebGPU/mobile notes, and
troubleshooting — open from **Package Manager > this package > View documentation**
(`Documentation~/index.md`).

## Support & license

abstractocclusion@outlook.com · SEE LICENSE IN [LICENSE.md](LICENSE.md)
