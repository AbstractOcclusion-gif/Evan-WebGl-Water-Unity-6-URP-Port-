// WebGpuWater - large-body atmosphere gate.
// Single definition of "should the fullscreen ocean god-ray pass run this frame". The pass is
// OCEAN-ONLY and reads GLOBAL shader uniforms, which the primary body publishes - so the gate
// tracks the primary body, matching exactly what the shader will sample. Bounded bodies report
// IsOceanClipmap == false and pools never set a god-ray density, so their look is untouched.
//
// URP-only: the pass is a URP ScriptableRendererFeature, so this gate only has a consumer when
// the Universal Render Pipeline is present (WEBGPUWATER_URP).
#if WEBGPUWATER_URP
namespace AbstractOcclusion.WebGpuWater
{
    internal static class LargeBodyAtmosphereGate
    {
        // True when the primary body is an unbounded ocean with god-ray shafts enabled. Gating on
        // the primary (not any body) keeps the CPU gate in lockstep with the globals the shader
        // reads, and avoids running the fullscreen raymarch when the shafts would be zero anyway.
        internal static bool HasActiveGodRayOcean
        {
            get
            {
                WaterVolume primary = WaterVolume.Primary;
                return primary != null && primary.IsOceanClipmap && primary.LargeGodRayDensity > 0f;
            }
        }
    }
}
#endif
