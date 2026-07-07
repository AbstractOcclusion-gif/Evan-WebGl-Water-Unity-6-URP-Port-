# Cleanup / Reorg Backlog

Living list of issues deferred to a dedicated clean-up / reorganization session. Add entries with
enough diagnostic context that they can be picked up cold.

---

## God rays / water receiver shader vs. fog (opaque floaters)

**Reported:** 2026-07-07, during ocean foam work (digression).

**Symptom:** On small ponds, god rays and god-ray "fog" appear over opaque floating objects at
certain camera angles — looks like the shafts aren't sorted / occluded correctly against the floaters.

**Root cause (per Bert):** the issue originates in the **water receiver shader**. Now that fog is in,
the water receiver has become largely redundant / "a bit useless", and its interaction with the
god-ray volume is what produces the artifact.

**Investigation context (pool god-ray pass, `Runtime/Shaders/GodRays.shader`):**
- Additive volume: `Blend One One`, `ZWrite Off`, `ZTest Always`, `Cull Front`, drawn `Transparent+100`.
- Its ONLY occlusion against solids is the per-step break `if (pe > sceneEye) break;` against
  `_CameraDepthTexture`.
- Both pipelines have `m_RequireDepthTexture: 1`; `m_CopyDepthMode: 0` (AfterOpaque), so the depth
  texture holds opaque geometry (the floaters ARE opaque, so they are present in it).
- Because occlusion works, the residual glow is the shaft segment *in front* of the floater
  accumulating (valid volumetric scattering), which reads as haze on the object at grazing angles —
  compounded/overlapped by the now-redundant water receiver path.

**To tackle during cleanup:** review whether the water receiver shader is still needed post-fog;
if not, remove/retire it and re-check the god-ray layering. If kept, add a soft depth-proximity
fade near geometry to the god-ray march instead of the hard `break`, and/or damp density where a
sample is close to `sceneEye`.

---

## Ocean foam — multi-scale detail (Crest-style)

**Noted:** 2026-07-07, during ocean whitecap foam work.

Increment 2b adds a single world-tiled foam texture thresholded by coverage. Crest samples TWO
tiling scales and blends them by distance/LOD (`WhiteFoamTexture` + `d_Crest_FoamMultiScale`) so
foam has both fine near-camera lace and coarse far-field structure without one tile size looking
repetitive up close or mushy far away.

**Enhancement:** add an optional second foam-texture scale to `OceanFftFoam`/the surface blend,
blended by camera distance (reuse the same distance term the cascade fade already computes). Gate it
so single-scale stays the default. Only worth doing if the single-scale look reads too repetitive
near the camera or too soft toward the horizon.
