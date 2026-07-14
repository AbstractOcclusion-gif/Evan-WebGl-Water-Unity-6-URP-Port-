# Master Base Clean — Unify the Surface-Height Source of Truth

**Date:** 2026-07-13
**Branch:** off `cleanup/remove-swe-shoal-foam` (pushed).
**Status:** plan for approval — **no code until you sign off.**
**Goal:** remove **Wall 1** — there is no single source of truth for surface height — so that shore (and every future wave feature) stops paying the byte-for-byte mirror tax. Scope is Wall 1 + opportunistic Wall 4 cleanups. **Wall 2/3 (coordinate frames + ocean depth field) is deferred to the shore Layer-A work**, which will build on this clean base.

---

## 1. The wall, precisely

Surface height is defined in **three** parallel places that must agree by hand:

- **GPU render** composes it in `WaterSurface.shader` vert: ripple (`_WaterTex`) + wind waves (`WaterWaves.hlsl`) + swell/chop (`WaterLargeWaves.hlsl`) + hero (`WaterHeroWave.hlsl`).
- **CPU buoyancy** re-derives it: ripple readback (`WaterSurfaceSampler.cs`) + a **hand-written analytic mirror** of wind + swell (`WaterWaveBank.cs` and `LargeWaveField.cs`, flagged "byte-for-byte").
- **FFT ocean** uses a **third pattern** entirely: GPU bake (`OceanFft.compute BakeHeightField`) → async readback.

So a wind/swell/shore change must be edited in HLSL **and** re-derived in C# or buoyancy desyncs. The deleted shoal factor had to be kept in sync across six files. This is the tax that made shore waves brutal, and it will tax shore run-up the same way.

**Key observation:** two of the three sources (ripple, FFT) are *already* "GPU computes → CPU reads back." Only the **analytic wind + swell** are hand-mirrored in C#. So the wall is smaller than it looks: unify by folding the analytic sources into the same bake-and-read-back pattern the FFT already proves works — then delete the C# mirrors. This is an **extension of an existing pattern, not a rewrite** (your "reuse, never rewrite" rule).

---

## 2. The core decision (needs your call)

**How do we make height single-source?**

- **Option 1 — GPU-authoritative unified height field (recommended).** All analytic wind + swell height is baked into a GPU height field (folded into / alongside the existing ripple + FFT readback), and the CPU buoyancy samples **one baked field** through the single `IWaterHeightSampler` seam. The wave math lives **once**, in HLSL. `LargeWaveField.cs` and the `WaveBank` CPU-sampling mirror are **deleted**.
  - *Pro:* true single source; deletes ~2 files of mirror math; the exact pattern FFT already uses; sets up shore Layer-A (a depth field baked the same way) and physical run-up (just another source in the bake).
  - *Con:* buoyancy inherits the FFT's ~1–2 frame readback latency for wind/swell (today those are instant on CPU). FFT already accepts this and floaters look fine, so the bar is proven — but it's a real change to verify.

- **Option 2 — centralized dual + parity test.** Keep the analytic math in HLSL **and** C#, but each in exactly one canonical file, with an automated edit-mode test asserting they produce identical values at sample points. No latency change.
  - *Pro:* zero latency; smaller change.
  - *Con:* still two definitions of the math — a weaker "single source"; the parity test is a guard, not a cure. Doesn't help shore/run-up reuse.

My recommendation is **Option 1**: it's the only one that actually removes the wall, and it's the foundation Option C exists to build. Option 2 is the fallback if readback latency proves unacceptable for buoyancy feel.

---

## 3. Target architecture (Option 1)

One authoritative **Surface Field** per body: a GPU-baked texture holding composited surface height (and the derivatives buoyancy needs) from every source — ripple sim, wind waves, swell, FFT, hero. Rendering and physics both consume it:

- **Render:** the vertex shader keeps composing from the shared HLSL wave functions (the math stays where it is) — OR samples the baked field; either way there is one math definition.
- **Physics:** `WaterVolume.Query.cs` / `IWaterHeightSampler` reads back the baked field (extending the FFT readback path). `WaterBuoyancy` is unchanged — it already goes through the one seam.
- **Deleted:** `LargeWaveField.cs` (C# swell mirror) and `WaveBank`'s CPU height/slope/velocity sampling — replaced by "sample the baked field."

Net: the CPU never re-derives wave math again. New height sources (shore run-up) are added in **one** place and buoyancy gets them for free.

---

## 4. Phased plan (reviewable chunks, compile + test each)

- **Phase 0 — Verify + git safety.** Confirm the exact current readback path (FFT `BakeHeightField` + async), the `IWaterHeightSampler` seam, and what derivatives buoyancy consumes. Snapshot branch, then a working branch off the pushed cleanup branch. *(This is the "never guess" grounding pass before touching code.)*
- **Phase 1 — Bake the analytic wind+swell into a readback height field.** Add the analytic sources to the GPU bake (reuse the FFT bake machinery / a small dedicated compute). No consumer change yet; verify the baked field matches the current CPU mirror numerically. Package compiles, behavior unchanged.
- **Phase 2 — Route CPU buoyancy to the baked field; delete the C# mirrors.** Point `WaterVolume.Query.cs` at the baked field; remove `LargeWaveField.cs` + `WaveBank` CPU sampling. **Test gate:** floaters on pond + lake + ocean ride correctly (this is the risk moment — readback latency + parity).
- **Phase 3 — Opportunistic Wall-4 cleanups (small, optional).** Delete the dead `_BedTex` decls in 3 shaders; rename `foamDecay`→survival wording or document once; centralize the wind-heading world vector; note (not fix) the waterline triplication. Each independently revertible.
- **Phase 4 — Verify.** Compile clean; buoyancy parity on all body types; a quick perf check on the extra bake; confirm nothing else referenced the deleted mirrors.

---

## 5. Risks

- **Readback latency for buoyancy** (Phase 2) — the one real behavioral risk. Mitigation: FFT already proves it's acceptable; if floaters feel laggy, fall back to Option 2 for the analytic part only.
- **WebGPU float readback / filtering quirks** — your notes flag WebGPU won't filter float32 and has readback nuances; the bake field format + manual bilinear must follow the existing `_WaterTexel` pattern.
- **Vertical velocity** — buoyancy uses a velocity mirror too (`WaveBank.SampleVerticalVelocity` / `LargeWaveField.VerticalVelocityAtQuery`); the baked field must carry or derive it, or velocity stays a small analytic term. To confirm in Phase 0.
- **Scope creep into Wall 2/3** — resist. This plan does **not** re-frame coordinates or build the ocean depth field; that's shore Layer-A, next.

---

## 6. What this unblocks

- **Shore Layer-A** (the world-aligned depth field) gets baked with the same machinery, in the same frame, as the height field — no new coordinate mess.
- **Physical run-up / wading** (shore gameplay, Layer-C) becomes "add one source to the bake," not "mirror in six files."

---

## Decisions for you

- **D1:** Option 1 (GPU-authoritative unified field, recommended) or Option 2 (centralized dual + parity test)?
- **D2:** Include the Phase-3 Wall-4 cleanups in this pass, or keep this strictly Wall-1 and do Wall-4 separately?
