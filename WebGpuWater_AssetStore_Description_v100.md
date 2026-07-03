# WebGpuWater — Asset Store presentation (v1.0.0)

---

## Title

**WebGpuWater — Interactive GPU Water for URP (Desktop · WebGPU · Mobile)**

## One-liner (search result / social card)

Touch it, float on it, ship it anywhere: fully interactive GPU water that runs at 30 fps
on a budget phone — and proves it.

---

## Main description

**Water you can actually touch.**

Most water assets render a surface. WebGpuWater simulates one. Every click, every
raindrop, every hull that plows through it leaves real ripples in a GPU heightfield —
and every floating object feels them back. Boxes bob, lean into the wave slope,
self-right, and drift with the wind. No baked animation, no faked interaction.

**Built WebGPU-first, so it runs everywhere.**

This isn't a desktop showpiece that collapses on mobile. The entire pipeline — ripple
sim, foam, caustics, god rays, even the spray particles — was engineered for the
strictest budget first: browsers and entry-level phones. Verified on real hardware:
**30 fps on a Honor X6 and Redmi Pad SE, 30+ on a Samsung Galaxy A17** — foam,
caustics and god rays ON. Your desktop build gets the same water with everything
turned up.

**One window. One minute. Water.**

Open the **Water Wizard**, click, press Play. It builds the volume, the surface, the
splash system, the quality asset, and a tweakable material saved into *your* project.
Eight demo scenes included — classic pool, terrain lake with real shoreline depth,
multi-lake, underwater, open water, and more.

---

### Interaction & physics
- Real-time compute ripple simulation — click, drag, or drive it from script
- Multi-point buoyancy: righting torque, drag, wave drift — objects ride the waves
- Two-way coupling: floaters disturb the water that carries them
- Entry splashes: droplet bursts that land, stick, and drift on the live surface,
  plus a flipbook crown
- Full gameplay API: `TryGetWaterHeight`, `AddRipple`, `TrySampleSubmersion`,
  surface raycasts, submersion queries

### Looks
- Ambient wind waves (physically derived, art-directable) that objects ride
- Turbulence-driven surface foam + GPU spray particles (zero CPU readback)
- Caustics and volumetric god rays with **real shadow shafts**
- Hybrid reflections per body: SSR, planar, or sky — over a procedural sky or your
  scene's URP probe
- Beer-Lambert water fog with HDR extinction, opacity dial, per-channel depth
  darkening, terrain-driven shoreline gradient

### Scale & performance
- Multiple independent water bodies (lakes, pools, rivers) with automatic culling
  and a simulation budget
- Camera-following sim window keeps big water crisp without giant grids
- Quality tiers (High/Medium/Low) with automatic hardware probe — one asset,
  every device
- Frame-rate-independent simulation: identical wave speed at 30 and 144 fps
- Unsupported browsers/GPUs get a clean error message, never a crash

### Authoring & code quality
- Water Wizard: one-window setup, everything it generates is editable in your project
- Every setting tooltipped; clean minimal public API; full source included
- UPM package with proper asmdefs — compiles even without URP installed

---

### Requirements

- Unity **2022.2+** (Unity 6 supported), **URP 12+**
- URP asset: Depth Texture + Opaque Texture (refraction/SSR), Transparent Receive
  Shadows (god-ray shafts)
- WebGL builds require WebGPU-capable browsers
- Full C# + shader source included

*From the maker of **Luminex** volumetric fog — integration between the two is on the
roadmap.*

---

## Short description (store card, ~160 chars)

Interactive GPU water for URP: real ripples, buoyancy, foam, caustics, god rays.
WebGPU-first — verified 30 fps on budget phones. Wizard setup in one minute.

## Keyword/tag suggestions

water, interactive water, buoyancy, URP, WebGPU, WebGL, mobile water, foam, caustics,
god rays, ripple simulation, ocean, lake, pool, stylized water

## Screenshot caption ideas (docs/ has shots)

1. simplepool.png — "Click it. Real ripples, real reflections."
2. underwater.png — "Under the surface: fog, caustics, god-ray shafts."
3. multipool.png — "Independent bodies, one budget: culled and scheduled automatically."
4. depthextinction.png — "Per-channel depth darkening — deep water goes blue, not black."
5. (capture) phone running the demo — "The same water. A €150 phone. 30 fps."
