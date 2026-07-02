# WebGpuWater → UPM package migration plan

Move the water system out of `Assets/WebGLWater` and into a **separate sibling UPM package**
next to Luminex, with proper assembly definitions, URP isolated behind a define, and namespaces
rebranded. Prepared for review — **no code will be moved or changed until you green-light it.**

## Decisions locked in
- **Layout:** new package `com.abstractocclusion.webgpuwater`, a sibling of
  `com.abstractocclusion.luminex` in the same `Packages/` folder. Fog users don't pull water; own
  `package.json` and version.
- **URP:** isolated behind a `WEBGPUWATER_URP` define + version-define, so the base assembly
  compiles even when URP isn't installed.
- **Namespaces:** rebrand `WebGLWater.*` → `AbstractOcclusion.WebGpuWater.*`.

## Findings that drive the plan
- **Runtime is location-independent.** At runtime the compute/shaders come from serialized
  references or `Shader.Find(name)`, so moving files doesn't break play mode.
- **Two editor-time loads use hardcoded `Assets/WebGLWater/...` paths** and WILL break in a
  read-only package:
  - `WaterBuildKit.cs:729` — `Root + "/Shaders/WaterSim.compute"` (required sim compute)
  - `WaterBuildKit.cs:68/209` — `Root + "/Shaders/WaterFoamParticles.compute"` (foam particles)
- **Everything the tools *write*** already targets `Assets/WebGLWater/Generated` — correct for a
  package (output lands in the consumer project, not the immutable package).
- **Dependencies:** only `PlanarReflection.cs` needs URP. No Burst / Jobs / Collections /
  Mathematics, no `unsafe`.
- **Coupling snag:** the base runtime references the URP-only `PlanarReflection` type directly
  (`WaterVolume.cs:606`) and the editor adds it (`WaterBuildKit.cs:415`). This decides how URP is
  isolated (see step 4).

## Target package structure
```
Packages/com.abstractocclusion.webgpuwater/
  package.json
  CHANGELOG.md
  Runtime/
    AbstractOcclusion.WebGpuWater.asmdef          # name + rootNamespace AbstractOcclusion.WebGpuWater
    (15 non-URP scripts: WaterVolume, WaterSimulation, WaterBuoyancy, WaterFoamParticles,
     WaterInteractable, WaterMembership, WaterObstacle, WaterProbe, WaterQuality,
     WaterRippleEmitter, WaterSplash, WaterSplashEmitter, WaterWaveBank, OrbitCamera)
    Platform/URP/
      AbstractOcclusion.WebGpuWater.URP.asmdef    # refs base + URP; defineConstraint WEBGPUWATER_URP
      PlanarReflection.cs                          # namespace AbstractOcclusion.WebGpuWater.URP
    Shaders/
      (all .shader, .compute, .hlsl)
  Editor/
    AbstractOcclusion.WebGpuWater.Editor.asmdef   # includePlatforms: Editor; refs base
    WaterBuildKit.cs, WaterSceneBuilder.cs, WaterVolumeEditor.cs, WaterWizardWindow.cs
    Platform/URP/                                  # only if editor code must touch PlanarReflection
      AbstractOcclusion.WebGpuWater.Editor.URP.asmdef
  Samples~/
    Demos/                                         # optional — see step 6
```

## Assembly definitions (following Luminex conventions)
1. **`AbstractOcclusion.WebGpuWater`** (Runtime): `rootNamespace` `AbstractOcclusion.WebGpuWater`,
   no URP references, autoReferenced true.
2. **`AbstractOcclusion.WebGpuWater.URP`** (Runtime/Platform/URP): references the base plus
   `Unity.RenderPipelines.Core.Runtime` and `Unity.RenderPipelines.Universal.Runtime`;
   `defineConstraints: ["WEBGPUWATER_URP"]`; `versionDefines` maps
   `com.unity.render-pipelines.universal >= 12.0.0` → `WEBGPUWATER_URP`.
3. **`AbstractOcclusion.WebGpuWater.Editor`** (Editor): `includePlatforms: ["Editor"]`, references
   the base. Add the URP editor asmdef only if editor code keeps a compile-time `PlanarReflection`
   reference.

## Step-by-step

**1. Scaffold the package.** Create `com.abstractocclusion.webgpuwater/` with `package.json`
(name, displayName "AbstractOcclusion.WebGpuWater", unity 2022.2 to match Luminex, author block)
and a `CHANGELOG.md`.

**2. Move source with their `.meta` files.** Copy each `.cs`, `.shader`, `.compute`, `.hlsl`
**together with its `.meta`** so GUIDs are preserved and the `WaterVolume.prefab` + generated
materials keep resolving their script/shader references. Files land in the tree above.

**3. Rebrand namespaces.** `WebGLWater` → `AbstractOcclusion.WebGpuWater`;
`WebGLWater.EditorTools` → `AbstractOcclusion.WebGpuWater.Editor`; `PlanarReflection` →
`AbstractOcclusion.WebGpuWater.URP`. Update every `using WebGLWater;` /
`using static WebGLWater.EditorTools.WaterBuildKit;` accordingly. The wizard's menu path is
already `AbstractOcclusion/WebGpuWater/Water Wizard`, so no menu change.

**4. Fix the two package-path loads + isolate URP.**
- Replace the hardcoded `Assets/WebGLWater/Shaders/*.compute` loads with a location-robust lookup:
  either a `PackageShadersRoot` const
  (`Packages/com.abstractocclusion.webgpuwater/Runtime/Shaders`) or `AssetDatabase.FindAssets`
  by name. Split `Root` into `PackageRoot` (immutable: shaders/compute) vs a consumer-writable
  `ProjectRoot`/`Gen` (unchanged, stays under the consumer's `Assets/`).
- URP isolation — the `WaterVolume → PlanarReflection` coupling forces a choice:
  - **Option A (recommended, lighter):** keep `PlanarReflection` in the base assembly but wrap its
    URP body in `#if WEBGPUWATER_URP` (class still declared when URP absent, so
    `GetComponent<PlanarReflection>()` always compiles). Base asmdef lists URP as a define-gated
    reference. Fewest moving parts; fully satisfies "compiles without URP." No separate physical
    URP asmdef.
  - **Option B (purist, true sub-asmdef):** move `PlanarReflection` into the URP sub-asmdef and
    break the two direct references (interface/`SendMessage`/typed lookup) so the base no longer
    names the type. Cleaner separation, more churn.

**5. Handle generated assets.** No change to write targets — the tools keep writing to the
consumer's `Assets/WebGpuWater/Generated` (created on first Wizard run). Worth renaming
`Assets/WebGLWater` → `Assets/WebGpuWater` for brand consistency in the generated path (one const).

**6. Demos.** The demo *builder* is already removed. Decide the leftover demo scenes/materials
under `Assets/WebGLWater/Demos`: **ship as `Samples~/Demos`** (optional import, matches Luminex) or
**drop** them from the package. Recommendation: Samples~ so buyers get examples without bloating
the core.

**7. Consume + verify** (your build test).
- Open the `luminexfromstrore` project → package compiles, Console clean, **with and without** the
  URP package present (validates the define gating).
- Add the package to `ThreeJSWaterPort` (local `file:` path in `Packages/manifest.json`), remove
  `Assets/WebGLWater`, run **AbstractOcclusion ▸ WebGpuWater ▸ Water Wizard** → water builds, Play
  works, foam particles + planar reflection function.
- Re-run the WebGPU build check (Mobile_RPAsset: Opaque + Depth Texture ON) per prior notes.

## Open sub-decision for green-light
Only one thing left to pick: **Option A vs Option B** for the PlanarReflection/URP split (step 4).
Everything else is settled. Tell me A or B and give the go-ahead, and I'll execute the migration
into `Packages/com.abstractocclusion.webgpuwater`.

## Not touched by this plan
No gameplay/sim behavior changes; no shader logic changes; the Water Wizard UI stays as-is. This is
packaging, path-robustness, and namespacing only.
