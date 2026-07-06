// WebGpuWater - large-body atmosphere render feature (URP, RenderGraph).
// Adds the ocean-scale distance haze + god-ray shaft composite to a URP renderer. Add this
// feature once to the renderer used by the ocean camera; it self-gates, so it costs nothing
// and changes nothing on scenes without an unbounded ocean.
//
// Gating is the whole point of this feature in Increment 1: EnqueuePass runs only when an
// ocean body is live (LargeBodyAtmosphereGate.HasActiveOcean). Pools and bounded lakes never
// arm it, so their render path is untouched.
//
// URP-only: ScriptableRendererFeature is a URP type, so the whole file compiles only when the
// Universal Render Pipeline is present (WEBGPUWATER_URP).
#if WEBGPUWATER_URP
using UnityEngine.Rendering.Universal;

namespace AbstractOcclusion.WebGpuWater
{
    public sealed class LargeBodyAtmosphereFeature : ScriptableRendererFeature
    {
        LargeBodyAtmospherePass _pass;

        public override void Create()
        {
            _pass = new LargeBodyAtmospherePass();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_pass == null) return;                          // not yet created (edit-mode reload)
            if (!LargeBodyAtmosphereGate.HasActiveOcean) return; // ocean-only: pools/lakes never fogged
            renderer.EnqueuePass(_pass);
        }
    }
}
#endif
