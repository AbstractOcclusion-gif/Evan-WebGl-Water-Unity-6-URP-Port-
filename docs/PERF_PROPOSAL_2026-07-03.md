# Proposal: half-precision migration & half-res god rays

Status: **proposal only — no code touched.** Context: after the audit fixes, a 1 m pool runs ≈2 ms/frame on the target tablet, so neither item is urgent; both are headroom for bigger scenes (multi-lake, higher tiers, weaker devices). Recommendation summary: do the two *cheap* steps (§1 slice 1, §2 option A) and only build the half-res architecture (§2 option B) if profiling still shows god rays as a hot spot.

## 1. Half-precision (`half`) migration

**What.** Move colour/lighting/foam ALU in the CG passes to `half`; keep `float` where precision genuinely matters.

Keep `float` (non-negotiable): world/pool positions and ray math (`IntersectCube`, refraction rays, TIR guard), screen UVs and depth comparisons (SSR, contact foam, refraction), the manual-bilinear texel arithmetic (`floor/frac` on texel coordinates breaks visibly in half at 256²), wave phase accumulation (`_WaveTime`-based arguments — half wraps after ~minutes), flow-phase foam UV offsets.

Move to `half`: fresnel/pow results, reflection/refraction *colours*, foam pattern/core/lace/alpha math, wrapped diffuse + ambient, sun glint tint, fog/attenuation multiplications after the `exp` (the `exp` argument stays float), normal renormalisation results, particle shader colour paths.

**Why it works.** Mobile GPUs (Mali, Adreno, Apple) run fp16 at ~2× ALU rate and halve register pressure — the long WaterSurface fragment is very likely occupancy-limited, so the win can exceed the pure ALU ratio. Desktop compiles `half` as `float`, so the editor look is pixel-identical; on WebGPU, `half` maps to f16 only where the `shader-f16` feature exists, else silently stays f32 — degradation-safe in all directions.

**Risk.** Banding/sparkle only visible on-device, which is why this must land in slices with a tablet check after each:

1. WaterSurface colour/lighting/foam math (biggest win, ~1 session)
2. FoamParticles + SplashParticles shaders (small, mechanical)
3. GodRays/PoolWall/WaterReceiver internals (already `half4` outputs; smallest win)

Acceptance per slice: side-by-side screenshots on the tablet (fresnel gradient at grazing angle, foam lace edges, underwater tint) + GPU frame time before/after. Expected total win: 10–25 % of the surface-pass cost on weak GPUs; zero change on desktop.

## 2. God rays

**Current cost** after the last pass: tier-capped steps (12 on Low), hoisted exponentials, hard shadow taps. Worst case remains ~12–24 shadowmap+caustic samples per covered pixel, full-screen when the camera is inside the volume.

**Option A — dithered march (recommended first).** Offset each pixel's march start by a per-pixel interleaved-gradient-noise fraction of the step size (`tEnter += dt * IGN(pixel)`), which converts step-count banding into high-frequency noise the additive accumulation visually averages. That lets the Low tier drop from 12 to 6–8 steps at equal perceived quality. Cost: ~5 shader lines, no C# or architecture change, no new risk class. **Do this before any half-res work.**

**Option B — half-res offscreen pass (the real architecture).** Render the god-ray volume into a half-resolution RT via a URP `ScriptableRendererFeature`/`ScriptableRenderPass` (after transparents), then composite additively with a depth-aware upsample. Win: ~4× on march cost (16× at quarter res). Real costs and risks: a new RenderFeature the user must add to the URP renderer asset (setup friction for a drop-in package), a downsampled-depth pre-pass, and edge halos around objects (the crate) unless the upsample is bilateral — that upsample is where the engineering time goes. Estimate: ~1 day + device testing. **Only worth it if, after Option A, GPU capture still shows god rays > ~0.5 ms on the target tablet.**

**Sequencing.** 1) Option A → measure on tablet → 2) §1 slice 1 (surface half) → measure → 3) decide Option B with numbers in hand.
