# Task — Per-pixel wave-aware underwater fog waterline ("Level 2")

WebGpuWater / ThreeJSWaterPort. Package: `Packages/com.abstractocclusion.webgpuwater`.

## Goal

Make the underwater fog's waterline follow the **actual displaced wave surface per pixel**, instead
of a single flat plane. This removes the remaining partial-submersion artifacts:

- Fog appearing above the water (a wave trough sits below the flat line, so air gets fogged).
- No fog just under a crest (a crest rises above the flat line, so submerged water gets no fog).
- A **distant, partially-submerged floater reading as fully immersed** (water drawn over its
  above-water part) because the flat waterline mis-classifies those pixels.

## What is already done (do NOT redo)

"Level 1" shipped: `WaterVolume.ComputeCameraSubmerged` already computes a **wave-aware surface
height at the camera** (`SurfaceHeightAtCamera()`, adds `SampleLargeWaveField` for open water) and
has **hysteresis** (`_wasCameraSubmerged`, `SubmergeHysteresis`) so the whole-screen fog no longer
toggles frame-to-frame. That fixes the camera-local case. This task is the **per-pixel** boundary,
which Level 1 does not address.

## Root cause (anchors)

- `Runtime/Shaders/WaterUnderwaterFog.shader` — fullscreen pass. `UnderwaterSegment()` uses a single
  flat `float _UnderwaterSurfaceY`:
  - Ocean (`_UnderwaterUnbounded > 0.5`): `WaterPathLength(sceneWorld, cam, _UnderwaterSurfaceY)` —
    a flat crossing `level`.
  - Pond: `IntersectCube(..., float3(-1,-1,-1), float3(1,0,1))` — the box top is pool-space y = 0
    (flat), ignoring wave displacement.
- `Runtime/Shaders/WaterFog.hlsl` — `WaterPathLength(fragWS, camWS, level)` crosses at a flat `level`.
- `_UnderwaterSurfaceY` is published from `WaterVolume.UpdateUnderwaterState` /
  `WaterUniformPublisher.PublishUnderwater` as one scalar.

## Reference (reference-first, written fresh — matches this codebase's convention)

Follow the same "reference, not copied" approach already used across this package (see the KWS/Crest
notes in `WaterOceanFft.cs`, `WaterLargeWaves.hlsl`, `LargeWaterClipmap.cs`):

- **KWS** underwater: a screen-space underwater mask evaluated against the *displaced* surface, not a
  flat plane — the waterline meniscus follows the real waves. Its `WaterUnderwaterFog.shader` header
  already notes: "camera-origin waterline (KWS half-line is later polish)". This task is that polish.
- **Crest** underwater: samples the wave displacement texture to reconstruct the surface height at the
  pixel, then classifies above/below against that height.

Write the implementation FRESH from these ideas. Do not paste KWS/Crest source. If you match a
published tuning constant, add a `// reference: KWS/Crest ...` comment (as elsewhere), or prefer a
value derived from this package's own constants.

## Approach

Per pixel, replace the flat `level` with the **local displaced surface height** at the relevant xz:

1. The wave surface functions are already in this package and can be reused:
   - Wind waves: `WaveHeight(poolXZ)` in `WaterWaves.hlsl` (uses `_WaveA/_WaveB/_WaveCount/_WaveTime/
     _WaveMetersPerUnit`), sampled via `WindWaveSampleXZ(poolXZ, worldXZ)` (see how
     `WaterSurface.shader` does it, e.g. its camera-surface line and the vertex displacement).
   - Large/open-water swell: `LargeBodyWaveHeight(worldXZ)` in `WaterLargeWaves.hlsl`.
   These uniforms and the volume frame (`_VolumeCenter/_VolumeExtent/_VolumeRot`, `WorldToPool`/
   `PoolToWorld`) are published as **globals** by the primary body (`PublishBodyGlobals`), so the
   fullscreen fog pass can read them after adding the includes. Verify they are bound when the fog
   pass runs (primary body publishes every frame); if any are missing, publish them from
   `WaterUniformPublisher` (do not hardcode).

2. Define one helper, e.g. `float SurfaceHeightAtXZ(float2 worldXZ)`, returning the world-space
   displaced surface height = rest (`_UnderwaterSurfaceY` as the base plane) + wind-wave height +
   (open-water) swell height. Single source of truth; reuse it everywhere the flat `level` was used.

3. Ocean path: replace the flat crossing in `WaterPathLength`. Because the surface is wavy, the
   camera→scene ray can cross it at a displaced height. Do a short, bounded search along the ray for
   the sign change of `(rayY - SurfaceHeightAtXZ(rayXZ))` and refine (a few iterations / a small
   fixed step count — use a named constant, no magic numbers), then measure the in-water length past
   the crossing. Keep it cheap and stable (this is a fullscreen pass).

4. Pond path: raise the box's top from the flat pool-space y = 0 to the local displaced height at the
   entry/exit xz (or clamp the entry to the displaced surface). Keep the `IntersectCube` walls/floor.

5. `deepestY` and `DownwellingAttenuation(deepestY, level)` should use the same displaced surface
   reference so depth darkening stays consistent with the wavy waterline.

Keep the two hardware-blend passes (Absorb / Inscatter) and the existing gating
(`WaterVolume.UnderwaterFogActive`) unchanged.

## Non-negotiable rules

- **File safety (this environment corrupts files via the Read/Write/Edit tools).** Edit source via
  bash `sed`/Python heredoc only; NEVER the Write/Edit tools on `.cs/.shader/.hlsl`. After every
  write verify: `wc -l`, brace balance (`{` == `}`), zero NUL bytes (`tr -cd '\0' < f | wc -c`), zero
  CRLF (`grep -c $'\r' f`), and `tail`. Keep a `/tmp` backup before editing.
- **No git in the sandbox.** The user commits from local PowerShell. Do not run git.
- **Incremental + verified.** Small steps; the user compiles and looks after each. Preserve behaviour
  for pools / non-submerged views (the seam is an open-water partial-submersion case).
- **Coding standards.** No magic numbers — every literal is a named constant (step count, search
  distance, epsilon, refine iterations). Short single-responsibility functions, early returns,
  reuse-first (call the existing wave functions; do not duplicate wave math). Comments explain WHY.
  Match the surrounding shader/C# style exactly.
- **Ask before coding** and confirm the approach with the user first (their standing rule).

## Acceptance / verification

- Camera bobbing at the waterline on open water: the meniscus follows the crests/troughs; no frames
  with fog above the water or clear water under a crest.
- A distant, partially-submerged floater: its above-water part is NOT fogged/covered as if immersed.
- Pools unchanged; non-submerged above-water views unchanged.
- No new per-frame allocations; fullscreen pass stays cheap (bounded, constant step count).
- Files compile; brace/NUL/CRLF checks clean; the user confirms visually in the editor.
