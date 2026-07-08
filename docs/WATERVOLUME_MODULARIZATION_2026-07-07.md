# WaterVolume modularization — Phase 1 + Phase 2 COMPLETE

Date: 2026-07-07 (Phase 1 + recipe); **Phase 2 completed 2026-07-08**. Branch: `main`.
Package: `Packages/com.abstractocclusion.webgpuwater`.
Status: **Phase 1 (module lifecycle) and Phase 2 (all 9 feature settings blocks migrated) shipped and
editor-verified green by Bert, compiling and pushed after each increment.** Every change is meant to be
**byte-for-byte behaviour-preserving**; Bert's compile-and-look on each increment is the source of truth.

---

## What this refactor is

`WaterVolume` is a ~2,000-line god-class: ~80 serialized fields across 22 `[Header]` blocks **plus** all
runtime logic. Goal: a thin **master** that orchestrates optional, tickable **modules**, each owning its
own logic and its own settings, toggleable independently.

The work splits in two:

- **Phase 1 (DONE): formalise the lifecycle.** The 6 collaborators the master already constructed by hand
  are now `IWaterModule`s driven through a registry. No serialized field moved. This is "formalise first".
- **Phase 2 (DONE): migrate the settings off `WaterVolume`** into per-feature nested `Settings` blocks.
  All 9 feature `[Header]` groups are now `[Serializable]` foldouts; only base/master/wiring fields stay
  flat on the master. Done as 8 version-gated increments, each compiled + editor-verified + pushed. The
  `FormerlySerializedAs`-into-nested-class trap (silent data loss) was avoided with the legacy-field +
  version-gated `ISerializationCallbackReceiver` copy recipe below.

### Phase 2 increments shipped (CurrentSettingsVersion = 8)

| v  | block(s) → nested Settings                          | notes |
|----|-----------------------------------------------------|-------|
| v1 | Depth attenuation → `DepthAttenuationSettings`      | read-only accessors |
| v2 | Ocean (open water/clipmap/god rays/whitecaps, 26) → `OceanSettings` | consts + derived helpers stay on master |
| v3 | Water fog → `WaterFogSettings`                      | `WaterFog` write-through; `waterFog` private read |
| v4 | Wind waves → `WindWaveSettings`                     | `WindWaves` write-through; feeds buoyancy/wave bank/ocean swell |
| v5 | Foam → `FoamSettings`                               | `Foam` write-through; **`foamBorderWidth` get/set** (Water Wizard writes it via `InternalsVisibleTo`) |
| v6 | Object interaction + Ripple tuning → `ObjectInteractionSettings` + `RippleSettings` | `RippleStrength`/`RippleRadius` write-through |
| v7 | Reflections → `ReflectionSettings`                  | `Reflections` write-through; 2 nested enums |
| v8 | Bed depth → `BedDepthSettings`                      | incl. `Terrain` reference; read-only |

Still flat on the master (correct — base/identity/wiring): scene-builder refs (shaders/mesh/camera/sun),
`tiles`/`sky`, `volumeExtent`, the Large-water sim-window controls (the custom editor reads them), the
multi-instance renderers + `isPrimary`/`autoLinkReceivers`, `quality`/`enableCulling`/`activationDistance`,
`lightDir`/`causticResolution`, `orbit`/`configureCamera`, `splashEmitter`.

---

## Phase 1 — what changed (compile + Play should look identical)

### New files
- `Runtime/IWaterModule.cs` — lifecycle contract: `bool Enabled { get; }`, `Initialize(WaterContext)`,
  `Dispose()`. (`Tick`/`PublishUniforms` are deliberately **not** on the interface yet — they arrive
  when the per-frame schedule is restructured with you at the editor, so there are no dead no-op members.)
- `Runtime/WaterContext.cs` — the shared seam. Phase 1 carries only `Owner`; the per-frame fields
  (camera, wave time, wind, sim window, …) get lifted here as Tick migrates.
- `Runtime/WaterCollaboratorModules.cs` — 6 thin adapters wrapping the **untouched** collaborator
  classes: `SimulationModule`, `ObstacleModule`, `CausticsModule`, `SurfaceSamplerModule`,
  `OceanFftModule`, `SimWindowModule`. Each owns its instance; `Enabled` reproduces the original
  construction condition exactly (e.g. `ObstacleModule.Enabled => obstacleShader != null`;
  `OceanFftModule.Enabled => IsOceanClipmap && oceanFftCompute != null`).

### `WaterVolume.cs` edits
1. The 6 eager collaborator **fields** became private **module fields** + read-only **forwarding
   accessors** with the same names (`_water`, `_obstacle`, `_caustics`, `_sampler`, `_oceanFft`,
   `_simWindow`). So every existing reader (Update, the ripple/sampling facade, caustics render, the
   `internal` accessors) compiles and behaves unchanged. The lazy trio (`_bedBaker`, `_publisher`,
   `_inputRouter`) was left as-is by design.
2. Added `internal int SimResolution => _simRes;` (modules read it at Initialize).
3. `TryInitialize`: the 6 inline `new …()` constructions replaced by one `BuildAndInitializeModules()`
   call, **relocated** to just after `_windowed = ShouldWindow()` and before `ApplySimAnisotropy()`.
   - Why there: `OceanFftModule.Enabled` gates on `_windowed` (via `IsOceanClipmap`), so it must run
     after `_windowed` is set; `ApplySimAnisotropy()` reads `_water`, so the sim must exist before it.
   - **Verified safe:** `ShouldWindow()` (line ~2047) reads only config
     (`enableLargeBodyWindow`, `openWater`, `unboundedOcean`, `VolumeExtentSafe`, `largeBodyThreshold`) —
     no collaborator — so moving the three previously-early constructions (sim/sampler/simWindow) to
     just after it changes nothing.
4. `OnDisable`: the 4 inline `?.Dispose()` lines + the `_sampler/_simWindow = null` lines replaced by
   one `DisposeModules()`. Same GPU resources released; the lazy `_bedBaker?.Dispose()` and
   `_inputRouter = null` are unchanged.
5. Added helpers `BuildAndInitializeModules()` and `DisposeModules()`.

### Verify (please)
- Clean compile of `AbstractOcclusion.WebGpuWater` (+ Editor).
- Play `Assets/ocean test.unity`: pool/ocean look, ripples, buoyancy, caustics, foam identical to before.
- Toggle a body off/on in play (OnDisable→OnEnable) to exercise dispose+rebuild of the registry.

### Heads-up: the sandbox mount was stale
While working, the bash view of `WaterVolume.cs` was truncated (1949 lines, `ShouldWindow` invisible);
the authoritative file is longer and intact. All edits were made against the authoritative file. If
anything looks off after you "save the project", ping me.

---

## Phase 2 — settings migration (recipe — APPLIED across v1–v8)

> This recipe was followed for all 8 increments above. Kept here as the reference for future settings
> moves and for the Phase-3 work. Per-feature rule learned: an accessor is **read-only** unless a public
> setter or the Water Wizard writes the field, in which case it's **get/set** (e.g. `foamBorderWidth`,
> `WaterFog`, `WindWaves`, `RippleStrength`/`RippleRadius`, `Reflections`).

### The trap (why we can't just move fields + `[FormerlySerializedAs]`)
`[FormerlySerializedAs]` only renames a field **within the same serialized container**. Moving
`windSpeed` from `WaterVolume` into `windSettings.windSpeed` changes the serialization **path**
(`windSpeed` → `windSettings.windSpeed`), which crosses a container boundary — `FormerlySerializedAs`
does **not** bridge it, so old scenes load the **default** and your tuned values are lost silently.
(There is zero precedent in the package: no `FormerlySerializedAs`, no nested `[Serializable]`, no
`ISerializationCallbackReceiver` — so this is new ground and worth verifying on one field first.)

### The working pattern (per feature)
Two things make it safe and keep the tree compiling after **each** feature:

1. **Nested settings + compatibility accessors.** Move the fields (with their `[Tooltip]`/`[Range]`/…)
   into a nested `[System.Serializable] class XxxSettings`, add `[SerializeField] XxxSettings xxx = new();`
   Keep a same-named forwarding accessor on `WaterVolume` (`internal float windSpeed => wind.windSpeed;`)
   so the ~25 referencing files (publisher, buoyancy, editor, shaders' C# feeders) keep compiling
   unchanged. The default inspector auto-draws the nested block as a foldout — **`WaterVolumeEditor`
   needs no change** (it only does scene gizmos and reads *placement* fields, which stay on the master).

2. **Legacy capture + one-time copy (this is what preserves scene values).**
   ```csharp
   // hidden, keeps the OLD serialized name so existing scenes still deserialize into it
   [SerializeField, HideInInspector, FormerlySerializedAs("windSpeed")] float _legacyWindSpeed = 3f;
   [SerializeField, HideInInspector] int _settingsVersion = 0;   // 0 = pre-migration scene

   public void OnAfterDeserialize() {
       if (_settingsVersion >= CurrentSettingsVersion) return;    // new/already-migrated: skip
       wind.windSpeed = _legacyWindSpeed;                         // copy legacy -> nested (plain field ops only)
       // …one line per migrated field…
       _settingsVersion = CurrentSettingsVersion;
   }
   public void OnBeforeSerialize() { }
   ```
   Here `FormerlySerializedAs("windSpeed")` **is** valid — `_legacyWindSpeed` is still top-level on
   `WaterVolume` (same container, just a C# rename), so it captures the old scene value; the callback
   then copies it into the nested block once. New objects serialize with the current version and skip.
   `WaterVolume` implements `ISerializationCallbackReceiver`.

   Verify per feature: open the scene, confirm the foldout shows the *tuned* values (not defaults),
   then let Unity re-save.

### Field → module map (base stays on master)
- **Master (do NOT move):** `Assigned by the scene builder`, `Look / surfaces`, `Water volume (placement)`,
  `Large-water sim window`, `Water body (multi-instance)`, `Performance`, `Camera`, `Splash`,
  `Simulation`(`lightDir`,`causticResolution`). These are placement/identity/schedule and some are read
  by `WaterVolumeEditor`.
- **OceanModule** (largest, and inactive on the pool test scene → lowest regression risk, good first
  target): `Open water`, `Ocean clipmap`, `Ocean god rays`, `Ocean foam` (+ the swell/heading derived
  `internal` accessors already grouped near them).
- **WaterFogModule:** `Water fog (Beer-Lambert)` (`waterFog`,`fogColor`,`fogExtinction`,`fogDensity`,`waterOpacity`).
- **DepthAttenuationModule:** `Depth attenuation (downwelling)` (publisher-only reads, no public API — clean 2nd target).
- **BedDepthModule:** `Bed depth` (pairs with the existing `WaterBedBaker` / lazy `BedBaker`).
- **WindWaveModule:** `Wind waves (spectral)` (read by publisher, buoyancy, `WaterWaveBank`).
- **FoamModule:** `Foam` (read heavily by `WaterUniformPublisher`).
- **RippleModule:** `Ripple tuning` + `Object interaction`.
- **ReflectionModule:** `Reflections` (`reflectionMode` has public `Reflections` prop + `ApplyReflections`).

Suggested order: DepthAttenuation (smallest clean proof) → Ocean (biggest win, low risk) → WaterFog →
BedDepth → WindWaves → Foam → Ripple → Reflections. Tree compiles after each; stop anywhere.

### After settings move, Phase 3 (optional): per-frame Tick
Add `Tick(WaterContext, float)` to `IWaterModule`, lift camera/waveTime/wind/simWindow onto
`WaterContext` (refresh once per frame at the top of `Update`), and move each module's per-frame call
off `Update` into its `Tick` — done **with the editor open**, one module at a time, because it touches
the render/sim schedule and only pixels can confirm the interleaving stayed identical. The master keeps
owning the multi-collaborator orchestration (`Step`, `RenderCausticsForThisBody`) as "the sim schedule".

---

## TL;DR
**Phase 1 + Phase 2 are DONE and green.** The `WaterVolume` god-class went from ~80 flat serialized fields
to 9 nested-Settings foldouts (Depth Attenuation, Ocean, Water Fog, Wind Waves, Foam, Object Interaction,
Ripple, Reflections, Bed Depth), values preserved via the versioned migration, on top of the Phase-1 module
lifecycle registry. Only base/wiring/placement fields remain flat on the master. Behaviour is meant to be
byte-identical — Bert's per-increment compile-and-look is the source of truth.

---

## Next session (2026-07-08+) — planned with Bert
1. **Deep test everything** — full pass across all demo scenes; save each so the migrated (v8) values bake in.
2. **Editor script** — a proper custom inspector for `WaterVolume` (the settings are auto-foldouts now; a
   dedicated editor can group/label them, hide the legacy fields explicitly, add per-module enable toggles).
3. **Cleanup** — small details: redundant `[Header]` above auto-named foldouts, any leftover doc/comment drift.
4. **Fix flagged issues / edge cases / workflow flaws** — Bert has a list to walk through (capture them here
   as we go). Candidates to keep in mind: the `Reset()`/new-object migration edge, prefab-override migration,
   whether `causticResolution`/`lightDir` should become module settings, legacy-field pruning once all scenes
   are re-saved past v8.
5. **Phase 3 (later)** — per-frame `Tick` + uniform publishing onto `IWaterModule` (see section above),
   editor-in-the-loop.
