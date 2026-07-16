# Foam B1–B4 — implementation report (2026-07-16, evening)

All four approved chunks from `FOAM_UNIFICATION_PLAN_2026-07-16.md` are implemented and
written to disk. **Nothing is tested in Unity yet** — recompile + the checklist at the
bottom.

## B1 — Veil depth test ("foam visible behind waves")
`FoamDensityComposite.shader`, `WaterFoamParticles.compute`, `WaterFoamParticles.cs`

- The composite now writes the splatted min foam depth to **SV_Depth** and z-tests
  **LEqual** (was ZTest Always) against the live depth buffer — which already contains
  every wave crest, because the ZWrite-On water surface (Transparent+0) draws before the
  veil (+5). Foam behind a nearer crest is rejected per pixel, exactly. ZWrite stays Off
  (the veil tests, it never blocks later draws).
- `VEIL_ZTEST_BIAS_METERS = 0.05`: the foam layer sits ON the surface, so its depth is
  biased a few cm camera-ward — it can never self-occlude against the surface it
  decorates, while behind-crest foam (metres deeper along the ray) still culls.
- `EyeDepthToRawDepth` = exact inverse of `LinearEyeDepth` via `_ZBufferParams`
  (reversed-Z handled by the params; empty texels output far depth).
- **Deleted the uncommitted 4-sample analytic occlusion march** (OCCLUDE_* + march loop +
  `_DensityCamWorld` uniform and its C# SetVector): the hardware test replaces it exactly
  and removes ~4× SurfaceWorldY per live particle per frame from the splat.

## B2 — Sphere fix ("weird round semi-transparent spheres")
`WaterFoamCommon.hlsl`, `FoamParticles.shader`, `SurfRollerParticles.shader`,
`SplashParticles.shader`, Ocean Demo scene

- **`FoamErosionLace(a, env) = a * FoamErosionAlpha(a, env)`** (new, WaterFoamCommon):
  texture-preserving erosion for sprites whose alpha IS the shape. Fresh particles now
  show the atlas' actual lace instead of a saturated disc (the old gate clamped 57% of
  in-shape pixels to opaque at env = 1). Switched: foam quads/spray, roller foam, splash
  legacy path. The packed splash dissolve (B channel) keeps the pure gate — correct there.
- **`FOAM_SPRITE_MIP_BIAS = -1.5`** (KWS idiom) on the foam + roller sprite lookups:
  distant sprites keep their lace instead of mipping into round blobs.
- **`SPRAY_IDLE_STRETCH = 1.3`**: slow/apex spray gets a fixed per-seed elongation — a
  droplet can no longer render as a perfect circle exactly when the velocity stretch has
  nothing to work with. Moving spray keeps the velocity stretch (now floored at 1.3).
- Ocean Demo: `flipbookFps 0 → 4` on both foam systems (static sprites read as objects;
  churning ones read as foam) and ocean `sprayChance 0.15 → 0.08`.
- NOTE: `FoamErosionLace` multiplies by sprite alpha, so overall foam is a touch more
  transparent than before — if the veil/quads look too thin, raise `_ParticleOpacity`
  (or the profile's look.opacity) rather than reverting the erosion.

## B3 — WaterFoamProfile (one master, per your 07-13 decision)
NEW `Runtime/WaterFoamProfile.cs` + hooks in WaterFoamParticles / WaterSurfRollerParticles /
WaterSplashEmitter

- ScriptableObject (`Create > AbstractOcclusion > Water > Water Foam Profile`) with:
  **Look** (tint, opacity, atlas, flipbook grid+fps, sizeHeroPower) + **Ambient** +
  **Veil** (opacity, gains, breakup) + **Roller** + **Splash** sections, each with a
  `drive` toggle.
- Every foam component gained an optional `profile` field: null = exactly the old
  behaviour; assigned = driven sections are re-applied **every frame** (a handful of
  field copies — live retuning in play mode, no editor plumbing), and the look/veil
  values ride over the materials via the MaterialPropertyBlock — **material assets are
  never written**, ending the divergent-.mat-copies drift class.
- To adopt in Ocean Demo: create one profile asset, assign it on the ocean's
  WaterFoamParticles (+ roller/splash if wanted). Tune the asset, everything follows.
- Deliberately NOT done blind: deleting/retargeting the 4 duplicate FoamParticles.mat
  copies across the other demo scenes — do that in-editor once a profile is in place
  (the copies stop mattering as soon as look.drive is on).

## B4 — Debt sweep
- **Roller particle struct 80 → 64 bytes** (removed dead `dAcross`/`birthOverCap` +
  2 pad floats) across compute/C#/shader — 32 KB less per 2048-pool, 25% less
  bandwidth on the roller Update.
- **SpawnBurst budget**: burst droplets now have their own per-frame cap
  (`_Capacity / 4`, own counter — `CounterCount` 2 → 3) — an impact storm can no longer
  flush living foam out of a small pool in one frame; bursts never starve behind the
  turbulence budget either.
- Dead `FOAM_NOISE_EPSILON` removed.
- Ocean Demo ocean foam `capacity 65536 → 16384`: the whole pool is drawn every frame
  (dead slots = degenerate quads), so 65k cost 393k verts/frame mostly for nothing;
  spray is bounded by tile caps anyway. Raise it again after P5 indirect draw if needed.

## Files touched
Shaders/: FoamDensityComposite.shader, FoamParticles.shader, SurfRollerParticles.shader,
SplashParticles.shader, WaterFoamCommon.hlsl, WaterFoamParticles.compute,
WaterSurfRoller.compute. Runtime/: WaterFoamParticles.cs, WaterSurfRollerParticles.cs,
WaterSplashEmitter.cs, **WaterFoamProfile.cs (new — Unity will generate its .meta)**.
Scenes/: 12. Ocean Demo.unity (3 values). Docs/: this report + FOAM_UNIFICATION_PLAN +
erosion_proof.png.

## Test checklist (in order)
1. **Recompile**: watch the console — the validator should stay green (no guarded
   constants were retuned). One new script (WaterFoamProfile.cs).
2. **B1**: Ocean Demo, fly low so a swell crest passes between the camera and a foam
   patch — the veil must now disappear behind the crest and reappear past it. Also check
   foam ON the surface did not develop holes/shimmer (if it did, raise
   `VEIL_ZTEST_BIAS_METERS` from 0.05 toward 0.1 in FoamDensityComposite.shader).
3. **B2**: the spray droplets should read as lacy, slightly elongated, churning bits —
   no clean circles. At distance the foam should stay textured instead of turning into
   soft balls. If everything looks too thin now, raise `_ParticleOpacity` on
   FoamParticles.mat / FoamDensityComposite.mat.
4. **B4**: throw a rigidbody in — splash bursts still fire; foam does not vanish
   pool-wide on multi-impacts. Roller (enable it in a shore scene) looks identical.
5. **B3**: create a Water Foam Profile asset, assign it on the ocean's foam component,
   and drag its sliders in play mode — foam retunes live; materials on disk stay clean.
6. The two staging scratch folders are safe to delete: `_to_delete/stage_particles/`
   (this session) and `KWSWater/_stage_tmp` (an earlier one).
