// WebGpuWater - shared CPU break-line solve for the surf layer.
//
// ONE home for the march + bisect that finds where the mean set wave first satisfies the
// break criterion (overCap = 1) along the camera's toward-shore direction. All reads go
// through the shore field's CPU arrays (closed form, no readback) - the same field the
// shader breaks on, so consumers sit exactly where the fronts visibly curl/foam. Used by
// WaterSurfCurl (ribbon placement) and WaterSurfRollerParticles (emission window); the
// two used to carry hand-synced copies of this solve.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    internal static class WaterSurfBreakLine
    {
        // March this many steps of this length from the camera along the toward-shore
        // direction (then bisect), so the solve covers ~200 m of approach.
        public const int SearchSteps = 128;
        public const float SearchStepMeters = 1.5f;
        public const int RefineBisections = 8;
        // Placement smoothing time constant (s) both consumers glide with: the camera
        // moves abruptly but the placement never teleports mid-front.
        public const float FollowSmoothingSeconds = 0.75f;

        /// <summary>Solve the break line nearest the body's camera. 'along' is the
        /// crest-parallel direction (travel = (-along.y, along.x), the shader's frame
        /// convention) WITHOUT any continuity flip - callers own their smoothing state
        /// and flip the sign against it. Fails (false) off-field, with the surf layer
        /// inactive, without a camera, or when no crossing exists along the march.</summary>
        public static bool TrySolve(WaterVolume volume, out Vector2 center, out Vector2 along)
        {
            center = default;
            along = default;
            ShoreWaveContext ctx = volume.ShoreWaveCtx;
            if (!ctx.SurfActive || ctx.Field == null) return false;
            Camera cam = volume.targetCamera != null ? volume.targetCamera : Camera.main;
            if (cam == null) return false;

            Vector2 probe = new Vector2(cam.transform.position.x, cam.transform.position.z);
            if (!ctx.Field.TrySampleShore(probe.x, probe.y, out float depth, out _,
                                          out float dirX, out float dirZ, out float slopeTan,
                                          out float influence)
                || influence <= 0f || dirX * dirX + dirZ * dirZ < 1e-6f)
                return false;

            Vector2 toShore = new Vector2(dirX, dirZ).normalized;
            float prevOver = LargeWaveField.SurfBreakOverCap(ctx, depth, slopeTan);
            bool startOutside = prevOver < 1f;
            // March shoreward while outside the break line, offshore while already inside it.
            float marchSign = startOutside ? 1f : -1f;
            Vector2 prev = probe;
            bool found = false;
            Vector2 low = default, high = default;
            for (int i = 1; i <= SearchSteps; i++)
            {
                Vector2 q = probe + toShore * (marchSign * SearchStepMeters * i);
                if (!ctx.Field.TrySampleShore(q.x, q.y, out depth, out _, out dirX, out dirZ,
                                              out slopeTan, out influence)
                    || influence <= 0f || depth <= 0f)
                    break; // left the field or hit land without crossing
                float over = LargeWaveField.SurfBreakOverCap(ctx, depth, slopeTan);
                if ((over >= 1f) != (prevOver >= 1f))
                {
                    low = prev;
                    high = q;
                    found = true;
                    break;
                }
                prev = q;
                prevOver = over;
            }
            if (!found) return false;

            bool lowOutside = startOutside; // 'low' is always on the starting side of the crossing
            for (int k = 0; k < RefineBisections; k++)
            {
                Vector2 mid = (low + high) * 0.5f;
                ctx.Field.TrySampleShore(mid.x, mid.y, out depth, out _, out dirX, out dirZ,
                                         out slopeTan, out _);
                bool midOutside = LargeWaveField.SurfBreakOverCap(ctx, depth, slopeTan) < 1f;
                if (midOutside == lowOutside) low = mid; else high = mid;
            }
            Vector2 hit = (low + high) * 0.5f;

            // Crest-parallel frame at the crossing: travel = toward shore (smoothed SDF
            // direction), along = its perpendicular (the shader's frame convention).
            if (!ctx.Field.TrySampleShore(hit.x, hit.y, out _, out _, out dirX, out dirZ, out _, out _)
                || dirX * dirX + dirZ * dirZ < 1e-6f)
                return false;
            Vector2 travel = new Vector2(dirX, dirZ).normalized;
            along = new Vector2(travel.y, -travel.x);
            center = hit;
            return true;
        }
    }
}
