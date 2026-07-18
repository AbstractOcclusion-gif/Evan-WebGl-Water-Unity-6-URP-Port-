# WaterSurface.shader — Clean / Reduce / Split (implementation prompt)

**Target:** `Packages/com.abstractocclusion.webgpuwater/Runtime/Shaders/WaterSurface.shader`
**State when written (2026-07-18, post-"WOW pass" commit):** 1943 lines, ONE SubShader, ONE Pass, one CGPROGRAM. `frag()` = 758 lines, `vert()` = 112. Line numbers below are from this version and will drift — re-locate by symbol name, never by line alone.

## Mission

Reduce the file to ~550 lines through three phases: (1) quick de-duplication wins, (2) a mechanical split into `#include` files, (3) decomposition of `frag()` into named stages. **Every phase must be behavior-identical — this is a refactor, not a rework.** The shader just shipped a validated visual milestone; any pixel change is a bug.

## Non-negotiable rules

1. **Behavior-identical, verified.** After each phase: Unity must compile clean for d3d11 AND the WebGPU/WGSL path, and the compiled shader should be compared before/after (Inspector → "Compile and show code"; the instruction streams should match modulo register renumbering). Run the demo scenes (pool, pond, islandwebgpu ocean, underwater) as a smoke test. One commit per phase so any regression bisects instantly.
2. **WGSL derivative-uniformity contracts are load-bearing.** Any `tex2D`/`texCUBE` with IMPLICIT derivatives must stay in uniform control flow (branches gated only on uniforms). Explicit-LOD (`tex2Dlod`, `texCUBElod`, `SampleLevel`) is safe anywhere. The comments in the file documenting this ("WGSL derivative uniformity: ...") must move WITH their functions — they are contracts, not decoration. Do not "simplify" a hoisted-gradient pattern away.
3. **D3D11 ps_4_0 sampler cap: the pass sits AT 16 sampler registers.** Do not add any `sampler2D`/`samplerCUBE` or inline `SamplerState`. In-file count is 8 `sampler2D` + 1 `SamplerState sampler_PointClamp` (shared by depth + shadow); the rest live in the includes. If a change needs a new texture, it must share an existing sampler (Texture2D + existing SamplerState).
4. **Do NOT fix look-affecting bugs during cleanup.** These are known, queued, and deliberately excluded because the artist's current tuning compensates for them (fixing changes the image and needs a retune session):
   - `GGX_EPSILON` (1e-5) clamps the GGX NDF denominator (π·r⁸ at the lobe peak) for all roughness < ~0.2 — sun core dimmed ~1900× at r=0.08, response inverted vs roughness. Correct fix: ~1e-9 for the NDF denominator only (keep 1e-5 for the visibility denominator). **Separate task, explicit approval required.**
   - `SampleSkyEnvironmentAniso`: `normalize(worldRay + (0, spread·offset, 0))` can be a zero vector when the reflected ray is exactly (0,1,0) and spread = 1 (slider maxima). `SunSpecular`: `normalize(viewDir + _LightDir)` zero when sun exactly antipodal. Guards queued, separate task.
   - Planar bodies show the mirrored sun in the RT **plus** the GGX lobe (double sun), and the GGX streak is not occluded by SSR/planar-reflected geometry. Design decision pending, separate task.
   - Horizon lift (`REFLECTION_MIN_UP_Y`) discards below-horizon cubemap content for ALL bodies incl. indoor-probe pools. Possible `_LargeBody` gate pending, separate task.
5. **Uniform names, Property names, and keyword variants must not change** — C# (`WaterUniformPublisher`) binds by name, materials serialize by name, and the shadow `multi_compile` set stays pass-level.
6. **The Properties block stays in the .shader** (Unity requirement). All new includes live in the same folder, `#ifndef`-guarded, included INSIDE the CGPROGRAM after the existing six includes (WaterCommon/Fog/Waves/Volume/LargeWaves/FoamCommon), in the dependency order given below.
7. Project standards apply to everything touched: no magic numbers, no dead code, comments explain WHY, single-responsibility functions, early returns.

## Phase 1 — Quick wins (one commit)

Each ≤10 lines; together they remove the copy-paste clusters found in review:

1. Delete the dead uniform `float _UseUrpProbe` (~line 346). Keep its Property (line ~22) — C# seeds from it; add a one-line comment there saying it is read by C# only.
2. Add `float2 ScreenUV(float4 screenPos)` = `screenPos.xy / max(screenPos.w, 1e-5)` — replace the 5 duplicate sites (~440, 836, 1295, 1438, 1574).
3. Add `float RoughnessToSkyMip(float roughness)` wrapping the `r * (SKY_MIP_CURVE_SCALE − SKY_MIP_CURVE_BIAS·r) * SKY_MIP_STEPS` formula — replace both sites (`SamplePlanarReflection` ~838, `SampleSkyEnvironmentRough` ~899).
4. Add `float3 ApplyFoamTiltToNormal(float3 normal, float2 tilt)` (tangent = `normalize(cross(normal, float3(0,0,1)))`, bitangent, renormalized add) — replace the 3 copies (~1534, ~1602, ~1672).
5. Add `float FoamDissolve(float patternValue, float coverage, float feather, float extraThreshold)` implementing the shared KWS law (contrast lerp → pow → `1 − sqrt(coverage)` threshold + extraThreshold → smoothstep) — replace the 3 near-identical blocks (whitecap ~1503, whitewash ~1654, swash ~1834; pass 0 extra for the whitecap, the trail/reflux erosion for the others). Verify each block's inputs map 1:1 BEFORE unifying; if any block genuinely differs, leave it and note why.
6. Name the shallow-clarity constants (`#define SHALLOW_CLARITY_DEPTH 0.6`, `SHALLOW_CLARITY_BLEND 0.5`) — fixes the twice-written 0.6 at ~1713–1718 that must stay in sync.
7. Name the wet-sand glaze weights (~1780–1786): `WET_FILM_MIN_TRANSPARENCY 0.6`, `WET_FILM_DEPTH_GAIN 0.3`, `WET_GLAZE_EDGE 0.25`, `WET_GLAZE_REFRACT 0.7`, `WET_GLAZE_REFLECT 0.12`, `WET_GLAZE_STRENGTH 0.85`.
8. Rename `getSurfaceRayColor` → `GetSurfaceRayColor` (lone camelCase function; 2 call sites).

## Phase 2 — Split into includes (one commit; verbatim moves only)

Create in `Runtime/Shaders/`, include in THIS order (each depends on the previous):

| # | File | Moves (verbatim) | ~Lines |
|---|------|------------------|--------|
| 1 | `WaterSurfaceScreen.hlsl` | `_CameraOpaqueTexture`, `Texture2D _CameraDepthTexture`, `SamplerState sampler_PointClamp`, `RawSceneDepth` (+ Phase-1 `ScreenUV`, and an `EyeDepthOf(worldPos)` helper for the 3 `−mul(UNITY_MATRIX_V, …).z` sites) | ~45 |
| 2 | `WaterSurfaceShadow.hlsl` | Shadow uniforms + both `WaterMainLightShadow` variants incl. the `#if defined(_MAIN_LIGHT_SHADOWS…)` block (~1020–1068). Needs `sampler_PointClamp` → after Screen | ~55 |
| 3 | `WaterSurfaceSpecular.hlsl` | Fresnel/GGX/aniso/sky-mip/horizon defines (~142–204) + `ANISO_TAP_OFFSETS/WEIGHTS` arrays + spec uniforms (`_ReflectionStrength`, `_FresnelFloor/_FresnelPower`, `_SunRoughness/_RoughnessFar/_RoughnessFarDistance/_RoughnessFalloff`, `_ReflectionAnisoStretch`, `_SunSheen/_SunSheenRoughness/_SunGrazeBoost`, `_SunColor`, `_SSR*`, `_UsePlanar/_UseSSR/_RealRefraction`, `_EnvReflectionIntensity`, `_PlanarReflectionTex`, `_ReflectionDistortion`) + functions `SampleOpaqueSmeared`, `MarchSSR`, `SamplePlanarReflection`, `LegacySunGlint`, `SampleSkyEnvironmentGrad`, `SampleEnvironmentGrad`, `SampleEnvironment`, `SampleSkyEnvironmentRough`, `SampleSkyEnvironmentAniso`, `EffectiveWaterRoughness`, `GgxLobeDistribution`, `GgxLobe`, `SunSpecular` | ~300 |
| 4 | `WaterSurfacePoolTrace.hlsl` | `_ProceduralPool`, `DeepWaterColor`, `GetWallShadeSplitGrad`, `GetWallColorShadowedGrad`, `GetSurfaceRayColor` (~1070–1179). Needs Specular + Shadow | ~120 |
| 5 | `WaterSurfaceFoamSampling.hlsl` | Surf-foam uniforms/defines (~119–136), foam/whitecap defines (~213–271), foam uniforms (~358–384), `SampleFoamMaskBilinear`, `FoamFlipbookFrames`, `SampleFlipbookCell`, `SampleFoamPattern`, `EvaluateFoam`, `SampleOceanWhitecapPatternTiled/Pattern`, `SampleOceanWhitecapTiltTiled/Tilt` (+ Phase-1 `FoamDissolve`, `ApplyFoamTiltToNormal`) | ~340 |
| 6 | `WaterSurfaceDetailNormal.hlsl` (optional) | `DETAIL_NORMAL_*` defines, `_DetailNormal*` uniforms, `DetailNormalTilt` (~904–932) | ~55 |

Constraints verified in review — keep them true:
- `ANISO_TAP_*` arrays + `SKY_ANISO_TAP_COUNT` must live in the SAME file as all three smear loops (they do: Specular).
- `FRESNEL_F0_WATER` textually expands `IOR_WATER/IOR_AIR` (defined in WaterShared via WaterCommon) — Specular must come after the existing includes (it does).
- Stays in the .shader: Properties, pass state/pragmas, geometry uniforms (`_IsPatch/_IsClipmap/_Patch*/_Clipmap*/_Horizon*`), `_Underwater`, `_BedTex` block, `SampleRipple`, `appdata`/`v2f`, `WindWaveSampleXZ`, `vert`, `frag`. Residual ≈ 1030 lines after this phase.

## Phase 3 — frag() decomposition (one or two commits)

Extract stages in RISK ORDER (lowest first), verifying after each:

1. `float4 UnderwaterStage(v2f i, <geom>, float clarity)` — the whole `_Underwater > 0.5` branch (~1272–1352). Self-contained, ends in `return` — cleanest first extraction.
2. `float3 ReflectionStage(...)` — fresnel + sky/planar/SSR ladder (~1355–1383 + 1422–1431).
3. `float3 RefractionStage(...)` — real/analytic refraction + clarity (~1413–1460).
4. `FinalCompositeStage(...)` — composite lerp + sun specular + SSS add + foam composite + haze + debug (~1684–1707, 1861–1934).
5. `FoamLayersStage(...)` — whitecap/pond/surf layers (~1462–1687), internally three sub-functions (the blocks are already independent).
6. `ShorelineStage(...)` — the bed-depth block (~1721–1860) incl. the `clip()` (legal in a helper; it executes unconditionally on this path).

Carrier struct (per review): `WaterGeomStage { float3 normal; float2 nxz; float3 incomingRay; float viewDist; float roughness; ShoreData shore; SurfWaveSample surf; float surfGeomFoam; }` built by `EvaluateSurfaceGeometry(v2f i)` (~1183–1255). Foam layers return `FoamLayer { float alpha; float3 look; }`.

End state: `frag` ≈ 40 lines of stage calls in render order; whole .shader ≈ 550 lines (optionally move the stages to `WaterSurfaceFragStages.hlsl`).

## Verification checklist (every phase)

- [ ] Unity compiles: d3d11 + WebGPU, all shadow variants (`_MAIN_LIGHT_SHADOWS`, `_CASCADE`, none).
- [ ] Compiled-code diff vs pre-phase (Compile and show code) — expect identity modulo register numbers.
- [ ] Demo smoke: pool (above + underwater), pond with foam, islandwebgpu ocean (planar body, sun path, whitecaps, shore), ripple interaction.
- [ ] No new sampler registers (compile error on d3d11 would say "maximum ps_4_0 sampler register index exceeded").
- [ ] `git diff --stat` shows moves, not rewrites (big deletions in .shader matched by additions in .hlsl).
- [ ] One commit per phase, message prefix `SHADER-SPLIT-<phase>`.
