# WebGpuWater — Documentation

**Version 1.0.0** | Unity 2022.2+ | URP 12+ | Desktop · WebGPU/WebGL · Mobile

Support: abstractocclusion@outlook.com

---

GPU water for URP: interactive ripple simulation, two-way buoyancy, surface + edge
foam, GPU foam particles, caustics, god rays, and hybrid planar/SSR/sky reflections —
authored from a single window (**Window > AbstractOcclusion > WebGpuWater > Water
Wizard**). A modern URP port and expansion of Evan Wallace's
[WebGL Water](https://madebyevan.com/webgl-water/) (MIT).

## Where to start

- **[Getting Started](GettingStarted.md)** — requirements, install, the Water Wizard,
  core components, the scripting API, and troubleshooting. Read this first.
- **Quality tiers & mobile preview** — below.

## Quality tiers & visual tuning

The **WaterQuality** asset ships three cost tiers — **High**, **Medium**, **Low** —
selected automatically by a hardware probe, or forced manually. Each tier changes the
things that cost GPU time: simulation and caustic resolution, render scale, god-ray
step count, wind-wave count, refraction, mesh detail, update intervals, and
foam-particle caps.

Because those resolutions and scales differ from tier to tier, **the High and Low
tiers usually need different visual-tuning values to look correct**. A look dialed in
at High — ripple radius and strength, foam thresholds and feather, wave amplitude, and
similar surface settings — can read too strong, too weak, or too coarse at Low, where
the sim runs on a smaller grid and lower render scale. Treat per-tier tuning as
expected, not as a bug: set the look you want on the tier you are targeting.

> **To preview what will actually render on mobile, set the Quality asset to Force
> Low.** Mobile devices run the Low tier, so forcing Low in the editor is the only way
> to see the resolution, render scale, and particle caps your phone/tablet build will
> use. Tuning on High and shipping to a Low-tier device will not match.

### Resolution comparison

<!-- Placeholder: resolution demo + explanation to be supplied by Bert.
     Drop the comparison capture and its walkthrough here. -->

_A side-by-side resolution demo and walkthrough will be added here._

## Support & license

abstractocclusion@outlook.com · SEE LICENSE IN LICENSE.md

---

*WebGpuWater v1.0.0 — 2026 Abstract Occlusion*
