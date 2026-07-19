# Water Exclusion Volumes — Design Plan

Status: **PROPOSAL — no code until authorized.**
Scope: prevent water from being drawn (and felt) inside a region: dry boat interiors first,
then submarines, underwater houses, caves.
References mined: Crest Portals module (`com.waveharmonic.crest.portals`, KWSWater project)
and KWS2 clip-mask zones (`Assets/KriptoFX/WaterSystem2`). Both solve exactly this.

---

## 1. What the references actually do

### Crest Portals (primary reference — "they nail this elegantly")

Crest renders the exclusion geometry into **two full-screen depth-only render targets**:

- `_Crest_PortalFogAfterTexture` — front faces of the volume mesh (depth of the nearest wall)
- `_Crest_PortalFogBeforeTexture` — back faces (depth of the farthest wall)

A global int `_Crest_Portal` encodes the active mode (none / 2D portal / volume /
negative volume / tunnel). Every water consumer then compares its own raw depth against the
front/back pair:

- **Surface** (`Library/Portals.hlsl` → `EvaluateMask`): a surface pixel whose depth lies
  between front and back is inside the volume → `discard`. Guards handle "camera inside the
  volume" (front depth reads 0) and "back face off screen".
- **Underwater fog** (`EvaluateFog`): the fog distance is clamped/offset by the front/back
  depths — i.e. the dry segment of the view ray is *subtracted from the fog integral*.
- **Refraction** (`EvaluateRefraction`): refracted samples that land off the volume cancel
  back to the unrefracted depth so the dry region never smears.
- **Meniscus** (`Meniscus.hlsl`): a screen-space edge-detect marches 3 pixels along the
  horizon normal in the water mask and darkens the crossing — the waterline seal.

Our use case ("dry inside a ship") is exactly Crest's **negative volume** (Volume + Invert),
rendered with inverted culling. Crest's own docs note the Tunnel/negative caveat that
matters to us: *"the walls must be covered by geometry (e.g. cave) for this to look
correct"* — the hull mesh hides the cut edge. A boat hull does this for free.

Extra machinery Crest carries that we do **not** need: stencil ref 5 for fly-through
volumes, a legacy-mask compatibility path, XR slices, HDRP/URP/Built-in triple SubShaders,
and a displaced-surface heightmap patch (`_Crest_WaterLinePortal`) so the mask knows the
wavy waterline on arbitrary meshes.

### KWS2 clip-mask zones

Same screen-space idea, zone-driven: `KWS_LocalWaterZone` objects with a `ClipMesh` register
into a visible-zone list; when the list is non-empty (`KWS_USE_CLIP_MASKING` keyword — zero
cost otherwise), `WaterPrePass.cs` draws each zone's clip mesh into
`_waterClipMaskDepthFront` / `_waterClipMaskDepthBack` (Depth24, colorFormat None) plus an
R8 `_waterClipMaskRT`. Consumption is sample-based with a sanity guard
(`if (back > front) front = 0`), and the underwater pass derives `insideAir =
(front == 0 && back > 0)`. KWS also keeps a dead-simple stencil variant
(`WaterCutoutMask`: ColorMask 0, ZTest Always, stencil bit 2) for plain surface cutouts.

### What we adopt / reject

| Idea | Verdict |
| --- | --- |
| Front/back boundary pair defining a dry segment per pixel | **Adopt** — it is *the* contract; both references converge on it |
| Fog = subtract the dry segment from the fog distance | **Adopt** (we already box-clip pond fog; same math, inverted) |
| Keyword/gate so zero volumes = zero cost | **Adopt** (KWS pattern; matches our `UnderwaterFogActive` gating style) |
| Screen-space depth RTs as the *first* implementation | **Reject for Phase 1** — our volumes are boxes; analytic beats RTs (below) |
| Stencil, fly-through positive portals, XR | **Reject** — out of scope, we only ever exclude |
| Meniscus edge march | **Defer to Phase 3** — hull geometry covers the seam in the boat case |

---

## 2. Our design: analytic OBB exclusion first

Both references pay for arbitrary meshes: two full-screen depth targets, an extra
geometry pass per volume per camera, and texture reads in every consumer. Our Phase-1
need is boxes (a cockpit, a sub interior, a room). For an oriented box the same
front/back information is **closed-form**: `WaterShared.hlsl` already owns
`IntersectCube` + `RAY_SLAB_EPSILON` for exactly this ray/box slab math (the pool
interior tracing uses it today). So Phase 1 ships with:

- **zero render textures, zero new passes, zero new samplers** — the 16/16 d3d11
  sampler cap on the WaterSurface pass (H7) is untouched;
- **no WGSL risk** — a fixed-count uniform loop is derivative-uniform by construction,
  no depth-texture sampling quirks;
- per-pixel cost = N point-in-OBB tests (surface) or N ray-slab tests (fog), N ≤ 4.

The screen-space mask (the full Crest approach) becomes the **Phase 3 upgrade path** for
arbitrary hull meshes, and slots in behind the same HLSL helper signatures, so consumers
don't change when it lands.

### 2.1 Component (runtime)

`Runtime/WaterExclusionVolume.cs` — new MonoBehaviour, deliberately shaped like our
existing registry components (`WaterObstacle` pattern):

- Transform defines the OBB: position = center, rotation = orientation,
  `size` field (Vector3, default 1×1×1 like BoxCollider) scaled by lossyScale.
- `OnEnable`/`OnDisable` register into a static `List<WaterExclusionVolume>` +
  `ActiveVolumes` accessor. No per-frame allocation.
- Global registry, consumed by every body (a dry room is dry in whichever body
  intersects it). A per-body membership filter is a later nicety, not Phase 1.
- Gizmo: wire box in the package cyan, editor-only.

### 2.2 Uniform contract (publisher)

`WaterUniformPublisher.WriteBodyUniforms` (or a sibling `WriteExclusionUniforms` called
from the same place) publishes, per frame:

- `_ExclusionCount` (int, 0..EXCLUSION_MAX_VOLUMES)
- `_ExclusionWorldToBox[EXCLUSION_MAX_VOLUMES]` (float4x4 array — world → unit-box space,
  so the shader test is `abs(local) <= 0.5` and one matrix carries center+rotation+size)

Over-limit volumes: nearest-to-camera win, one warning (editor only), count clamped —
**no silent cap** without a log, per project rules.

Names go through `WaterShaderNames` / `Shader.PropertyToID` statics like every other
uniform — no inline strings.

### 2.3 Shader contract (single implementation, reused everywhere)

`Runtime/Shaders/WaterShared.hlsl` gains ONE helper block (reuse-never-rewrite —
every consumer includes these, nobody hand-copies the loop):

```hlsl
#define EXCLUSION_MAX_VOLUMES 4   // C# pair: WaterExclusionVolume.MaxVolumes (validator-checked)

// True when world-space point p lies inside any active exclusion volume.
bool InsideExclusion(float3 p);

// Total length of ray [origin, origin + dir * maxDist] that lies inside exclusion
// volumes (slab test per box via the existing IntersectCube math). Used to subtract
// the dry segment from fog / god-ray integrals.
float ExclusionRayLength(float3 origin, float3 dir, float maxDist);
```

Consumers, Phase by Phase:

| Consumer | File | Change | Phase |
| --- | --- | --- | --- |
| Water surface | `WaterSurface.shader` frag (via `WaterSurfaceFragStages.hlsl`) | `if (InsideExclusion(worldPos)) discard;` — kills the sheet inside the hull | 1 |
| Underwater fog | `WaterUnderwaterFog.shader` | subtract `ExclusionRayLength` from the fog distance; camera-inside-volume → fog pass sees zero submerged depth | 2 |
| CPU submerge gate | `WaterVolume.Underwater.cs` `ComputeCameraSubmerged` | camera inside a volume → not submerged (dry sub interior stays dry even below sea level) | 2 |
| God rays | `GodRays.shader` / `LargeBodyGodRays.shader` | same segment subtraction along the shaft march | 2 |
| Foam/splash particles | `WaterFoamParticles.compute` | kill particles spawned inside a volume (point test) | 2 (cheap, optional) |
| Caustics | — | deliberately untouched: caustic RT is top-down onto pool floor/walls; boat hulls don't sample it. Revisit only if a house floor shows caustics | note only |
| Buoyancy/physics | — | deliberately untouched: the hull still floats; exclusion is visual + camera-state only | note only |

The surface `discard` sits in a loop over `_ExclusionCount` — a **uniform** branch
(uniform count, uniform matrices), so WGSL derivative-uniformity is safe; the discard
itself happens after all implicit-derivative samples or uses explicit-grad paths already
in place (verify point in Phase 1 review).

### 2.4 Editor integration

- `WaterVolumeEditor` Core tab: exclusion volumes listed read-only (count + ping), since
  the component lives on its own GameObjects.
- `WaterBuildKit.CreateBoat`: new optional step — add a "Dry Interior" child with
  `WaterExclusionVolume` fitted to the hull's inner cockpit (primitive hull: derived from
  `BoatHullScale` minus wall thickness; custom mesh: fitted to renderer bounds shrunk by a
  constant factor, user-adjustable afterwards). Undo-wrapped like every other build step.
- `WaterWizardWindow` boat section: "Dry interior" toggle (default on).
- Menu: `GameObject > AbstractOcclusion > Water Exclusion Volume` (or BuildKit `MenuRoot`
  equivalent) for standalone rooms/houses.
- `WaterWaveConstantsValidator`: new pair `EXCLUSION_MAX_VOLUMES` ↔
  `WaterExclusionVolume.MaxVolumes`.

---

## 3. Phases

**Phase 0 — sampler headroom (optional, decoupled).** Convert
`_SurfCrestFoamLut` / `_ShoreDepthTex` / `_ShoreSDFTex` from `sampler2D` to
`Texture2D` + shared sampler to free 2–3 of the 16/16 slots. The analytic Phase 1 no
longer *requires* this (zero new samplers), so it reverts to what it always was: hygiene
+ headroom for future features (including a Phase-3 mask texture, which would itself be
Load-based anyway). Can run any time as its own authorized batch.

**Phase 1 — dry surface (the "boat doesn't fill with water" moment).**
`WaterExclusionVolume` component + registry + gizmo; publisher uniforms; `WaterShared.hlsl`
helpers; `WaterSurface.shader` discard; validator pair; BuildKit/Wizard boat wiring.
Acceptance: drive the wizard boat — no water sheet inside the cockpit, from every camera
angle, above and at eye level; zero volumes in scene = shader path fully skipped
(`_ExclusionCount == 0` early-out); WebGL + WebGPU both clean.

**Phase 2 — dry *feel* (subs, houses).**
Fog segment subtraction; CPU submerge gate point test (camera in a submerged dry room:
no fog pass, no underwater tint); god-ray subtraction; particle kill.
Acceptance: sink a box room below an ocean, walk the camera in — interior renders bone
dry while windows still show fogged water outside; leaving the room re-arms fog with the
existing hysteresis (no flicker at the doorway).

**Phase 3 — polish + arbitrary meshes (only if boxes prove insufficient).**
Screen-space front/back mask pass (Crest-style, but URP RenderGraph like our
`WaterUnderwaterFogFeature`, Load-based reads, no stencil) behind the same
`InsideExclusion` / `ExclusionRayLength` signatures; meniscus/waterline seal at cut
edges; convex-mesh volumes. Each its own authorized batch.

---

## 4. Risks & guard-rails

- **Sampler cap (H7):** Phase 1 adds 0 samplers. Phase 3's mask, if ever built, uses
  `Texture2D` + `Load` (KWS/Crest both Load their masks) — also 0 samplers. The cap is
  respected by construction, not by luck.
- **WGSL uniformity:** all new branches are uniform (uniform count/matrices) or happen in
  full-screen passes with no implicit derivatives. The surface `discard` placement gets an
  explicit review against the existing tex2Dgrad paths.
- **Waterline seam:** the surface cut edge is visually covered by the hull/room walls
  (Crest's own stated requirement for tunnels). Documented as a content rule; meniscus
  seal is the Phase 3 answer if a seam ever shows.
- **Multiple bodies:** helpers read global volume uniforms, so behavior is identical for
  every body — no per-body drift pair is introduced.
- **Perf:** N ≤ 4 slab tests per fog step is measurable; Phase 2 includes a
  before/after GPU timing check on the fog pass at 4 volumes.

---

*Next step after sign-off: Phase 1 as one batch (component + uniforms + helpers +
surface discard + validator pair + boat wiring), compile + WebGPU check, then Phase 2.*
