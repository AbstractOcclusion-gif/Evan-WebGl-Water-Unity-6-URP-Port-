// WebGpuWater - large-body atmosphere gate.
// Single definition of "does the large-body atmosphere pass have anything to do this frame".
// The fog + god-ray render feature is OCEAN-ONLY: it must never run for pools or bounded
// lakes, whose look stays byte-for-byte unchanged. Gating off the existing WaterVolume
// registry (rather than a new registration path) means there is no second source of truth
// that could drift from IsOceanClipmap.
//
// URP-only: the atmosphere pass is a URP ScriptableRendererFeature, so this gate only has a
// consumer when the Universal Render Pipeline is present (WEBGPUWATER_URP).
#if WEBGPUWATER_URP
using System.Collections.Generic;

namespace AbstractOcclusion.WebGpuWater
{
    internal static class LargeBodyAtmosphereGate
    {
        // True when at least one enabled body renders as an unbounded ocean clipmap. Bounded
        // bodies (pools / lakes) report IsOceanClipmap == false, so a pond-only scene never
        // arms the pass. Allocation-free and early-returning: it runs per camera, per frame.
        internal static bool HasActiveOcean
        {
            get
            {
                IReadOnlyList<WaterVolume> bodies = WaterVolume.Bodies;
                for (int i = 0; i < bodies.Count; i++)
                {
                    if (bodies[i] != null && bodies[i].IsOceanClipmap) return true;
                }
                return false;
            }
        }
    }
}
#endif
