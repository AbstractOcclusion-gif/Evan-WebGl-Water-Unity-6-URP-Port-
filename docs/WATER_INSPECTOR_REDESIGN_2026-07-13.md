# Water Inspector Redesign — Design Doc

**Date:** 2026-07-13
**Branch:** `cleanup/remove-swe-shoal-foam` (after SWE/shoal/shore-foam cleanup)
**Status:** design for approval — **no code until you sign off.**

## Decisions (from the design Q&A)

1. **Explicit `WaterBodyType` enum** (Pond / Lake / Ocean) added to `WaterVolume`.
2. **Type selector + smart sections** — a type picker at the top; existing foldout sections stay but grey by relevance to the chosen type. Per-instance editing.
3. **Grey out** irrelevant fields (visible but disabled), don't hide.
4. **Structure + coherence fixes together** in this pass.

---

## 1. The `WaterBodyType` model

New serialized enum on `WaterVolume`:

```
enum WaterBodyType { Pond, Lake, Ocean }
```

**Source-of-truth question (needs your nod — see Q-A below).** Today the *functional* behavior branches on three independent flags: `ocean.openWater`, `ocean.unboundedOcean`, and `enableLargeBodyWindow`/`_windowed`. Two clean options:

- **(A) Advisory type (recommended for this pass).** The enum drives the *inspector* (which sections grey) and a one-click **"Apply {Type} defaults"** button. The functional flags remain the real switches, still visible in their sections (greyed per type). Lowest risk — no change to the ocean-vs-pool render/runtime code.
- **(B) Authoritative type.** The enum becomes the single switch and *derives* `openWater`/`unbounded`/`window`. Cleaner conceptually, but it refactors the runtime branching that renders ocean vs pool — higher risk, bigger blast radius. Better as a follow-up once (A) is proven.

I recommend **(A)** now, **(B)** later. This keeps the risky runtime paths untouched while giving you the type-aware editor immediately.

**Default + migration.** New field defaults via a one-time `ISerializationCallbackReceiver`/version bump that **infers** the type for existing bodies: `unboundedOcean || openWater` → Ocean; `enableLargeBodyWindow` (or extent above `largeBodyThreshold`) → Lake; else → Pond. So no scene looks wrong after upgrade. Ties into the existing `CurrentSettingsVersion` migration.

**"Apply {Type} defaults"** button sets the sensible flag combo for the chosen type (e.g. Ocean → `openWater=true, unbounded=true, window=true`; Pond → all off) — explicit, never auto-clobbering on selection.

---

## 2. Applicability system (replaces the 3 gating idioms)

Today relevance is expressed three inconsistent ways: `SectionWithToggle` headers, `DrawOceanOnly` body-greying keyed off a *different* section, and hand-rolled greying (`activationDistance`). Unify into one:

- A `[Flags] Applicability { Pond, Lake, Ocean, All }` tag per section/field.
- One helper `DisabledGroupIf(Applicability applies, WaterBodyType current)` wrapping content in `EditorGUI.BeginDisabledGroup` — generalises `DrawOceanOnly` and the manual `activationDistance` greying.
- A `BodyTypeSelector` control (segmented Pond/Lake/Ocean) drawn near the top by `WaterEditorUI`.
- A new **`SubSection`** helper (nested foldout with its own state) so dense sections (Foam, Reflections, Ocean·Foam) get real collapsible sub-groups instead of flat `SubHeading` labels.

These are additive to `WaterEditorUI.cs`; nothing existing breaks.

---

## 3. Section plan (grey rules by type)

| Section | Pond | Lake | Ocean | Change |
|---|---|---|---|---|
| Placement, Look(`sky`), Body, Performance, Reflections, Water Fog, Depth Attenuation, Simulation, Ripple, Object Interaction, Camera, Splash | ✓ | ✓ | ✓ | Common — unchanged grouping |
| Look → `tiles` (pool albedo) | ✓ | – | – | grey unless a pool renderer exists |
| Wiring → `oceanFftCompute`, `largeBodyCausticsShader` | – | – | ✓ | grey off-Ocean |
| Volume Scattering → `crestScatter` subgroup | – | – | ✓ | move to a SubSection, grey off-Ocean |
| Ripple → `conserveVolume`, `conserveMaxCorrection` | ✓ | ✓ | – | bounded-only; grey on Ocean |
| Foam → `foamBorderWidth`, `foamContactDepth` | ✓ | ✓ | – | pool-wall/contact; grey on Ocean |
| Bed Depth | ✓* | ✓ | ✓* | Lake-primary; keep available (a shored ocean/pond may use it) |
| Large-Water Sim Window | – | ✓ | ✓ | grey on Pond |
| Ocean · Open Water | – | ✓ | ✓ | `unboundedOcean` Ocean-only |
| Ocean · Clipmap / God Rays / Whitecaps | – | – | ✓ | grey off-Ocean (already `DrawOceanOnly`, now type-driven) |

`*` = allowed but off by default for that type.

---

## 4. Coherence fixes bundled in

- **Expose `rippleQuality`** — serialized but never drawn today; add it to Performance (or Simulation). One `PropertyField`.
- **Disambiguate the two swell controls** — group `largeWaveAmplitude` + `swellHeight` + `swellWavelength` under a **"Swell"** SubSection, and relabel `largeWaveAmplitude` → "Wind-wave height ×" and `swellHeight` → "Long-period swell (m)" so they read distinctly. (Relabel only; field names unchanged to avoid churn, or rename with `[FormerlySerializedAs]` — your call in Q-B.)
- **Tooltips + ranges** for `foamFromSpeed` / `foamFromCurvature` (the only undocumented foam knobs).
- **Rename pool-centric common field** `poolHalfExtentMeters` → `waveScaleMeters` with `[FormerlySerializedAs("poolHalfExtentMeters")]` (no scene-data loss) and update its readers.
- **Fix stale tooltips** that say Wind Waves is "below" (it's drawn above the Ocean sections).
- **Optional rename** `shorelineFadeDepth`/`shorelineStrength` → `bedFadeDepth`/`bedTintStrength` (they're bed-tint, not the removed shoreline foam) — with `[FormerlySerializedAs]`. Flagged as optional since it touches the publisher + baker readers.

---

## 5. Build order (reviewable chunks, compile+test each)

- **Chunk 1 — runtime.** Add `WaterBodyType` enum + serialized field + migration/inference; `[FormerlySerializedAs]` renames; tooltips; expose `rippleQuality` plumbing. Update any readers (Wizard, publisher, baker). → you compile + confirm scenes still open with correct inferred type.
- **Chunk 2 — editor helpers.** Add `BodyTypeSelector`, `Applicability` + `DisabledGroupIf`, `SubSection` to `WaterEditorUI`. → compiles, no visual change yet.
- **Chunk 3 — rebuild the inspector.** Draw the type selector; convert sections to applicability-driven greying; add the SubSections; fold in the field fixes. → you eyeball a Pond, a Lake, and an Ocean scene: right things greyed, every field visible once.
- **Chunk 4 — verify.** Full compile clean; each field appears exactly once; "Apply defaults" behaves; Wizard still builds bodies. Commit.

---

## 6. Risks

- **Enum serialization / migration** — a wrong inference makes an existing body show the wrong type. Mitigation: infer conservatively from the same flags the runtime already uses; verify on your demo scenes.
- **Field renames** — must carry `[FormerlySerializedAs]` and update *all* readers (Wizard, publisher, baker) or scene data / compiles break. That's why renames are opt-in (Q-B).
- **Two sources of truth** (enum vs functional flags) under option (A) — accepted for now; the "Apply defaults" button + greying keep them aligned; option (B) resolves it later.
- **Wizard drift** — the Wizard writes some of these settings directly; Chunk 1 should point it at the same fields so "Edge foam" etc. can't diverge.

---

## Open questions for you

- **Q-A:** Advisory type (A, recommended) or authoritative type (B, bigger refactor) for this pass?
- **Q-B:** Field *renames* now (with `FormerlySerializedAs`), or relabel-in-UI-only and defer renames? Renames are cleaner but touch more readers.
- **Q-C:** Put `rippleQuality` under Performance or Simulation?
