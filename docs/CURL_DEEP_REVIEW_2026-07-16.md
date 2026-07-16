# Surf Curl deep review — "overlaps and renders trashy" (2026-07-16)

Read-only pass. No code was touched. Files reviewed: `WaterSurfCurl.hlsl` / `WaterSurfCurl.cs` /
`WaterLipSheetRig.cs` / `WaterSurfBreakLine.cs` / `WaterSurfWaves.hlsl` / `WaterSurface.shader`
(vert + frag curl paths, render state) / `WaterShoreDepthField.cs` (publish) / the Ocean sample
materials, plus the serialized state of `Assets/ribbontest.unity`.

**Verdict up front: the curl math itself is sound (the CURL-2.1 construction you confirmed).
What you are seeing is dominated by ONE stale-scene problem and ONE real systemic rendering
defect.** Removal is not needed to make the artifact go away — the component is fully optional and
self-cleaning (`OnDisable → DestroyStrip`), so you can also just disable it per scene at any time.

---

## Finding 1 — ribbontest.unity is running CURL-1-era serialized values (CONFIRMED, highest impact)

The scene was saved **before** the CURL-2/2.1 fields existed, so Unity kept the old values and
defaults the missing ones:

| Field | Your scene | Current intent | Effect |
|---|---|---|---|
| `stripAcrossSegments` | **160** | 384 | see below — THE folding artifact |
| `acrossHalfWidth` | **30** | 15 | doubles the across spacing again |
| `mode` | *(absent → StaticTestRibbon)* | FollowBreakLine for live | you're on the synthetic test path |
| `renderFullFront` | **1** | test-only knob | full front + curl on the strip |
| `pivotAheadFraction` | 0.248 | 0.35 recommended | tighter, more self-colliding tube |
| `rollSpeed`, `lipBaseThickness`, `lipTipThickness` | *(absent → 1 / 1 / 0.4)* | — | fine |

**Across vertex spacing = 2 × 30 m / 160 = 37.5 cm.** The documented requirement through the curl
(tooltip on the field itself) is **under ~10 cm**, and the lip is only ~2–3 m across at your
wavelength 26 (faceLen = 0.1 × 26 = 2.6 m, sharpened ×0.6 while plunging). So the whole 200°
spiral is being drawn with roughly **6–8 vertices**. That is exactly the known failure mode noted
in the code: "below ~20 vertices through the curl the spiral reads as jittering, self-crossing
facets **that fold over each other**" — i.e. *overlapping, trashy*. Current defaults give 7.8 cm
(2 × 15 / 384); your scene has ~4.8× that.

## Finding 2 — full-front test ribbon over a NON-flat ocean (CONFIRMED, second driver)

`renderFullFront = 1` + StaticTestRibbon is the CURL-1 look test, which is only valid **over a
flat calm ocean** (that's how CURL-1 was judged). Your ribbontest volume has real animated water
under the ribbon: FFT `largeWaveAmplitude 0.5`, `choppiness 0.5`, swell 0.5 m / 140 m.

Consequences, all by construction:

- In full mode the strip's keep-window is `smoothstep(0.02, 0.08, frontHeight)` — so a ~3 m-wide
  band around every synthetic front tail survives the discard while sitting only **2–8 cm** above
  the base ocean. Two opaque ZWrite-On surfaces, tessellated differently (strip grid vs clipmap
  grid + geomorph), a few cm apart, both animated: they interleave and shimmer. The 2 cm
  view-space sheet bias suppresses it head-on but not at grazing angles.
- The synthetic test field hard-codes `fieldMask = 1` and the shoulder fade is **along-crest
  only** — the ribbon has **no across fade**, so fronts pop into existence as a hard line at the
  ribbon's offshore edge (d = +30 m, where the synthetic depth is 4.5 m and fronts are already
  near full height).
- The ribbon renders synthetic waves from a *synthetic* beach in the middle of real water — it can
  never agree with the surface under it. That is "a second water surface overlapping the ocean",
  literally.

## Finding 3 — the barrel interior renders with the UNDERWATER material (CONFIRMED, systemic — affects live mode too)

The rig spawns **two** strips: one with `surfaceAbove`'s material (`_Cull = Back`,
`_Underwater = 0`) and one with `surfaceUnder`'s (`_Cull = Front`, `_Underwater = 1`). Once the
lip rolls past ~90°, the overturned part of the sheet shows its underside to the camera → its
screen winding flips → the **above** strip culls those pixels and the **under** strip draws them
instead. The under material shades as "camera is underwater": negated normal, underwater tint,
refraction from `_CameraOpaqueTexture` (which does not contain the water surface), frequent
TIR fallback. So the inside of the tube — the most visible part of a 200° plunging curl seen from
the beach — renders as dark, glassy, wrong-looking patches. Inherited verbatim from the hero-sheet
rig; the hero wave presumably never showed enough >90° interior for this to be judged.

This one is a real code defect for the curl's use-case and survives any inspector tuning. Fix
direction (needs your go): shade the sheet double-sided in the above strip (per-strip `Cull Off`
via the property block + `SV_IsFrontFace` normal flip in the fragment when `_IsSurfCurl`), and
stop spawning the under strip for the curl — the interior of an overturning lip is still an
air→water interface, never an underwater view.

## Finding 4 — opaque discard fade + self-intersection at high roll (design limits, cosmetic)

- The sheet is opaque (ZWrite On, no blend); its "fade" through the break is a **discard sweep**
  (`weight < 0.02` kills fragments). The finished tube therefore shrinks and pops out rather than
  dissolving — reads as flicker/trash right at hand-over. The whitewash dressing (0.9) covers a
  lot of it, but only where foam is visible.
- With `theta = maxRoll × rollGates` per vertex, the map is intentionally a spiral, and at 200°+
  the thrown tip legitimately passes **through** the face/foot near splash-down (real water does
  hit there). CURL-3's landing lobe + lip spray exist precisely to dress that moment; with the
  density of Finding 1 it instead reads as raw intersecting polygons. At correct density and with
  foam on, this is acceptable; if you still dislike it, capping visible roll ~140–160° is the
  cheap knob (`curlMaxRollDegrees`).

Minor notes, verified non-issues: the strip's `_IsClipmap` path is safe (morph uniforms unset on
the strip's property block → morph = 0); the delta-mode foot lands on the rendered water (same
masked height composition, foot ease + 2 cm bias); `_Surf*` globals including `_SurfBeatTime` are
published even while the surf layer is inactive, so the test ribbon animates correctly; the
break-line solve + continuity flip are correct; the roll clock is monotonic (no un-roll regression).

---

## What I suggest (in order, no code needed for the first two)

1. **In ribbontest**: select the WaterSurfCurl object and either reset the component (picks up all
   current defaults) or set `acrossHalfWidth 15`, `stripAcrossSegments 384`,
   `pivotAheadFraction 0.35`. If you want to judge CURL-1 again, also zero the FFT/swell/wind on
   that volume — full-front mode is only meaningful on flat water. Otherwise switch `mode` to
   FollowBreakLine and judge it on the island coastline.
2. **Re-test before deciding on removal** — with 37.5 cm facets and the full-front overlap both
   active, what you've been looking at is not the curl the math produces.
3. **With your authorization I'd fix Finding 3** (double-sided above-strip shading for the curl, no
   under strip): that's the one genuine rendering bug, and it will matter in live mode on the
   island too.
4. Optional polish afterwards: auto-derive `stripAcrossSegments` from width (spacing ≤ 8 cm so the
   knob can't silently regress), an across shoulder for the test ribbon, and a soft foam-dressed
   edge instead of the bare discard sweep at hand-over.

And to your "I almost want to remove it": the layer is cleanly severable — one component, render
only, no sim/physics coupling, strips destroyed on disable — so removal stays a 5-minute decision
whenever you want it. Nothing else depends on it (`ShoreFoamState`/roller/foam all read the front
field, not the sheet). My read: fix the two scene knobs first; the curl you confirmed at CURL-2.1
("begins to look like a roll") is still in there.

---

## Addendum — 12. Ocean Demo.unity (live FollowBreakLine curl, saved 07-16)

Checked after the bridge came back: this scene has a **live-mode** curl (mode FollowBreakLine,
512×512 strip, ±20 m along / ±5 m across → ~2 cm spacing, so Finding 1 does NOT apply here).
Volume: surfPeriod 4 s, wavelength AUTO (L0 = 1.56·T² ≈ 25 m → faceLen ≈ 2.5 m),
setStrength 0.68, compression 0.8. If this is where you saw the artifact, the drivers are:

1. **Tube geometry knobs are collapsed**: `pivotAheadFraction 0.144` (recommended ~0.35),
   `pivotHeightFraction 0.44` (vs 0.7), `curlStartFraction 0.828`. That means only the top ~17%
   sliver of the lip participates, orbiting a pivot sitting almost ON the crest and low — a thin
   flap corkscrewing in a tiny radius, which folds straight back through the face. With
   `masterGain 0.607` the effective max roll is ~120° — enough to overturn, not enough arc to
   read as a tube. This alone produces "weird, overlapping". Try pivot 0.35 / 0.7, curlStart 0.5,
   masterGain 1 and tune the timing with `rollSpeed`, not the gain.
2. **The ribbon is too narrow for this surf**: ±5 m across, but with `setStrength 0.68` the
   per-front break depth varies a lot (set amplitudes ~0.35–1.1×) while the break-line solve
   centres the ribbon on the MEAN wave. Individual lips break offshore/inshore of the ribbon and
   there is **no across fade** in live mode — a rolled lip that crosses the strip edge is cut off
   in a hard line, and some breaks are missed entirely. With `acrossHalfWidth 15` (the default)
   the ribbon covers most of the set spread.
3. **Finding 3 fully applies** at ~120° roll: the overturned interior is drawn by the underwater
   material — with this scene's water fog + volume scatter + realRefraction on, that interior
   shades darkest/glassiest of all. This is the code fix I'd like to make.

The roller particles are also in this scene; sheet + roller both live on the break line, so any
sheet artifact reads doubly busy next to them.
